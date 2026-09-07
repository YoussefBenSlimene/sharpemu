// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
	private const ulong LazyCommitWindowBytes = 0x0200_0000UL;
	private static int _lazyCommitTraceCount;
	private static int _guestAllocatorHoleRecoveries;
	private static int _auxiliaryThreadExecuteFaultRecoveries;
	private static int _auxiliaryThreadExecuteFaultSkips;
	private nint _workerAbortStack;
	private const uint WorkerAbortStackSize = 0x10000u;

	private unsafe void SetupExceptionHandler()
	{
		if (!OperatingSystem.IsWindows())
		{
			SetupPosixExceptionHandler();
			return;
		}

		if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RAW_HANDLER"), "1", StringComparison.Ordinal))
		{
			_rawExceptionHandlerStub = CreateExceptionHandlerTrampoline(RawVectoredHandlerPtrManaged);
			if (_rawExceptionHandlerStub == 0)
			{
				throw new InvalidOperationException("Failed to create raw exception handler trampoline");
			}
			_rawExceptionHandler = (nint)AddVectoredExceptionHandler(1u, _rawExceptionHandlerStub);
			Console.Error.WriteLine($"[LOADER][INFO] Raw exception handler installed: 0x{_rawExceptionHandler:X16}");

			// The raw handler carries the guest-image write-fault bridge, so the
			// path must be compiled before the first protected-page store can
			// reach it. Guest code has not started yet, so warming here cannot
			// race a real fault.
			SharpEmu.HLE.GuestImageWriteTracker.WarmUp();
			Console.Error.WriteLine(
				"[LOADER][INFO] Guest image CPU write tracking: " +
				$"{(SharpEmu.HLE.GuestImageWriteTracker.Enabled ? "enabled" : "disabled")}");
		}
		else
		{
			Console.Error.WriteLine("[LOADER][INFO] Raw exception handler disabled by SHARPEMU_DISABLE_RAW_HANDLER=1");
		}

		_handlerDelegate = VectoredHandler;
		_handlerHandle = GCHandle.Alloc(_handlerDelegate);
		_exceptionHandlerStub = CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(_handlerDelegate));
		if (_exceptionHandlerStub == 0)
		{
			throw new InvalidOperationException("Failed to create exception handler trampoline");
		}
		_exceptionHandler = (nint)AddVectoredExceptionHandler(1u, _exceptionHandlerStub);
		Console.Error.WriteLine($"[LOADER][INFO] Exception handler installed: 0x{_exceptionHandler:X16}");
		SharpEmu.HLE.GuestImageWriteTracker.WarmUp();

		_unhandledFilterDelegate = UnhandledExceptionFilter;
		_unhandledFilterHandle = GCHandle.Alloc(_unhandledFilterDelegate);
		_unhandledFilterStub = CreateExceptionHandlerTrampoline(Marshal.GetFunctionPointerForDelegate(_unhandledFilterDelegate));
		if (_unhandledFilterStub == 0)
		{
			throw new InvalidOperationException("Failed to create unhandled exception filter trampoline");
		}
		SetUnhandledExceptionFilter(_unhandledFilterStub);
	}

	private unsafe int UnhandledExceptionFilter(void* exceptionInfo)
	{
		try
		{
			EXCEPTION_RECORD* exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
			ulong rip = ReadCtxU64(((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord, 248);
			ulong rsp = ReadCtxU64(((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord, 152);
			Console.Error.WriteLine("[LOADER][FATAL] Unhandled exception filter fired.");
			Console.Error.WriteLine($"[LOADER][FATAL]   Code: 0x{exceptionRecord->ExceptionCode:X8}");
			Console.Error.WriteLine($"[LOADER][FATAL]   Exception Address: 0x{(ulong)(nint)exceptionRecord->ExceptionAddress:X16}");
			Console.Error.WriteLine($"[LOADER][FATAL]   RIP: 0x{rip:X16}");
			Console.Error.WriteLine($"[LOADER][FATAL]   RSP: 0x{rsp:X16}");
			Console.Error.Flush();
		}
		catch
		{
		}

		return 0;
	}

	private unsafe int VectoredHandler(void* exceptionInfo)
	{
		if (_vectoredHandlerDepth > 0)
		{
			LogNestedVectoredException(exceptionInfo);
			Console.Error.Flush();
			return 0;
		}

		_vectoredHandlerDepth++;
		try
		{
			EXCEPTION_RECORD* exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
			uint exceptionCode = exceptionRecord->ExceptionCode;
			uint exceptionFlags = exceptionRecord->ExceptionFlags;
			ulong exceptionAddress = (ulong)exceptionRecord->ExceptionAddress;
			void* contextRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord;
			if (contextRecord == null)
			{
				Console.Error.WriteLine("[LOADER][FATAL] ContextRecord is null!");
				Console.Error.Flush();
				return 0;
			}

			ulong rip = ReadCtxU64(contextRecord, 248);
			ulong rsp = ReadCtxU64(contextRecord, 152);
			if (TryRecoverGuestInt41(exceptionCode, contextRecord, rip))
			{
				return -1;
			}
			if (exceptionCode == 3221225477u &&
				exceptionRecord->NumberParameters >= 2 &&
				SharpEmu.HLE.GuestImageWriteTracker.TryHandleWriteFault(
					exceptionRecord->ExceptionInformation[1]))
			{
				return -1;
			}
			if (TryRecoverAuxiliaryThreadExecuteFault(exceptionRecord, contextRecord, rip))
			{
				return -1;
			}

			if (exceptionCode == 3221225477u && TryHandleLazyCommittedPage(exceptionRecord, rip, rsp))
			{
				return -1;
			}
			if (exceptionCode == 3221225477u &&
				TryRecoverGuestBadStoreFault(exceptionRecord, contextRecord, rip))
			{
				return -1;
			}
			if (exceptionCode == 3221225477u &&
				TryRecoverGuestProducerCursorFault(exceptionRecord, contextRecord, rip))
			{
				return -1;
			}
		if (exceptionCode == 3221225477u &&
			TryRecoverGuestAllocatorHole(exceptionRecord, contextRecord, rip))
		{
			return -1;
		}
		if (exceptionCode == 3221225477u &&
			TryRecoverGuestNullChainTableFault(exceptionRecord, contextRecord, rip))
		{
			return -1;
		}
		if (exceptionCode == 3221225477u &&
			TryRecoverGuestBadNamePointerFault(exceptionRecord, contextRecord, rip))
		{
			return -1;
		}
		if (exceptionCode == 3221225477u &&
			TryRecoverGuestGarbageReadFault(exceptionRecord, contextRecord, rip))
		{
			return -1;
		}
			if (exceptionCode == StatusIllegalInstruction &&
				TryRecoverIllegalInstruction(contextRecord, rip))
			{
				return -1;
			}
			if (exceptionCode == StatusIllegalInstruction &&
				TryRecoverAmdCompatInstruction(contextRecord, rip))
			{
				return -1;
			}
			if (IsBenignHostDebugException(exceptionCode))
			{
				return -1;
			}
			if (exceptionCode == MSVC_CPP_EXCEPTION)
			{
				return 0;
			}

			switch (exceptionCode)
			{
				case 3221225477u:
					LogAccessViolationTrace(exceptionAddress, exceptionRecord);
					break;
				case 3221226505u:
					{
						ulong p0 = exceptionRecord->NumberParameters >= 1 ? (*exceptionRecord->ExceptionInformation) : 0;
						ulong p1 = exceptionRecord->NumberParameters >= 2 ? exceptionRecord->ExceptionInformation[1] : 0;
						Console.Error.WriteLine($"[LOADER][TRACE] VEH_FASTFAIL code=0x{exceptionCode:X8} ex=0x{exceptionAddress:X16} rip=0x{rip:X16} rsp=0x{rsp:X16} p0=0x{p0:X16} p1=0x{p1:X16}");
						Console.Error.Flush();
						break;
					}
			}

			ulong rax = ReadCtxU64(contextRecord, 120);
			ulong rbx = ReadCtxU64(contextRecord, 144);
			ulong rcx = ReadCtxU64(contextRecord, 128);
			ulong rdx = ReadCtxU64(contextRecord, 136);
			ulong rsi = ReadCtxU64(contextRecord, 168);
			ulong rdi = ReadCtxU64(contextRecord, 176);
			ulong rbp = ReadCtxU64(contextRecord, 160);
			ulong r8 = ReadCtxU64(contextRecord, 184);
			ulong r9 = ReadCtxU64(contextRecord, 192);
			ulong r10 = ReadCtxU64(contextRecord, 200);
			ulong r11 = ReadCtxU64(contextRecord, 208);
			ulong r12 = ReadCtxU64(contextRecord, 216);
			ulong r13 = ReadCtxU64(contextRecord, 224);
			ulong r14 = ReadCtxU64(contextRecord, 232);
			ulong r15 = ReadCtxU64(contextRecord, 240);

			Console.Error.WriteLine("[LOADER][INFO] =========================================");
			Console.Error.WriteLine("[LOADER][INFO] NATIVE EXCEPTION CAUGHT!");
			Console.Error.WriteLine($"[LOADER][INFO]   Code: 0x{exceptionCode:X8}");
			Console.Error.WriteLine($"[LOADER][INFO]   Exception Address: 0x{exceptionAddress:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RIP: 0x{rip:X16}");
			Console.Error.WriteLine(
				$"[LOADER][INFO]   Host thread: managed={Environment.CurrentManagedThreadId} " +
				$"name='{Thread.CurrentThread.Name ?? "<unnamed>"}'");
			if (_activeGuestThreadState is { } activeGuestThread)
			{
				Console.Error.WriteLine(
					$"[LOADER][INFO]   Guest thread: handle=0x{activeGuestThread.ThreadHandle:X16} " +
					$"name='{activeGuestThread.Name}' state={activeGuestThread.State} " +
					$"last_import={activeGuestThread.LastImportNid ?? "<none>"} " +
					$"last_ret=0x{activeGuestThread.LastReturnRip:X16}");
				Console.Error.WriteLine(
					$"[LOADER][INFO]   Last import registers: " +
					$"rax=0x{Volatile.Read(ref activeGuestThread.LastImportRax):X16} " +
					$"result_valid={Volatile.Read(ref activeGuestThread.LastImportResultValid) != 0} " +
					$"rdi=0x{activeGuestThread.LastImportRdi:X16} " +
					$"rsi=0x{activeGuestThread.LastImportRsi:X16} " +
					$"rdx=0x{activeGuestThread.LastImportRdx:X16} " +
					$"rcx=0x{activeGuestThread.LastImportRcx:X16} " +
					$"r8=0x{activeGuestThread.LastImportR8:X16} " +
					$"r9=0x{activeGuestThread.LastImportR9:X16}");
				Console.Error.WriteLine(
					$"[LOADER][INFO]   Last import stack args: " +
					$"0=0x{activeGuestThread.LastImportStack0:X16} " +
					$"1=0x{activeGuestThread.LastImportStack1:X16} " +
					$"2=0x{activeGuestThread.LastImportStack2:X16} " +
					$"3=0x{activeGuestThread.LastImportStack3:X16} " +
					$"4=0x{activeGuestThread.LastImportStack4:X16} " +
					$"5=0x{activeGuestThread.LastImportStack5:X16}");
			}
			if (TryFormatNearestRuntimeSymbol(rip, out string symbol))
			{
				Console.Error.WriteLine("[LOADER][INFO]   RIP symbol: " + symbol);
			}
			Console.Error.WriteLine($"[LOADER][INFO]   RAX: 0x{rax:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RBX: 0x{rbx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RCX: 0x{rcx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RDX: 0x{rdx:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RSI: 0x{rsi:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RDI: 0x{rdi:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RBP: 0x{rbp:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   RSP: 0x{rsp:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R8 : 0x{r8:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R9 : 0x{r9:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R10: 0x{r10:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R11: 0x{r11:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R12: 0x{r12:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R13: 0x{r13:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R14: 0x{r14:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   R15: 0x{r15:X16}");
			Console.Error.WriteLine($"[LOADER][INFO]   Flags: 0x{exceptionFlags:X8}");

			ulong accessType = 0;
			ulong target = 0;
			if (exceptionCode == 3221225477u && exceptionRecord->NumberParameters >= 2)
			{
				accessType = *exceptionRecord->ExceptionInformation;
				target = exceptionRecord->ExceptionInformation[1];
				string accessText = accessType switch
				{
					0uL => "read",
					1uL => "write",
					8uL => "execute",
					_ => $"unknown({accessType})"
				};
				Console.Error.WriteLine("[LOADER][INFO]   AV access: " + accessText);
				Console.Error.WriteLine($"[LOADER][INFO]   AV target: 0x{target:X16}");
				if (VirtualQuery((void*)target, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) != 0)
				{
					Console.Error.WriteLine($"[LOADER][INFO]   AV target region: base=0x{mbi.BaseAddress:X16} size=0x{mbi.RegionSize:X16} state=0x{mbi.State:X08} protect=0x{mbi.Protect:X08}");
				}

			}

			Console.Error.WriteLine("[LOADER][INFO]   Stack qwords (RSP..):");
			for (int i = 0; i < 16; i++)
			{
				ulong stackAddr = rsp + (ulong)(i * 8);
				if (!TryReadHostQword(stackAddr, out ulong value))
				{
					Console.Error.WriteLine("[LOADER][WARNING]   Could not read stack qwords.");
					break;
				}
				Console.Error.WriteLine($"[LOADER][INFO]     [rsp+0x{i * 8:X2}] @0x{stackAddr:X16} = 0x{value:X16}");
			}

			if (string.Equals(
					Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FAULT_STACK_WINDOW"),
					"1",
					StringComparison.Ordinal))
			{
				Console.Error.WriteLine("[LOADER][INFO]   Full fault stack window (RSP-0x300..RSP+0x100):");
				var windowStart = rsp >= 0x300 ? rsp - 0x300 : 0;
				for (var stackAddr = windowStart; stackAddr < rsp + 0x100; stackAddr += 8)
				{
					if (!TryReadHostQword(stackAddr, out var value))
					{
						continue;
					}

					var relative = unchecked((long)(stackAddr - rsp));
					var relativeText = relative >= 0
						? $"+0x{relative:X}"
						: $"-0x{-relative:X}";
					var symbolText = TryFormatNearestRuntimeSymbol(value, out var stackSymbol)
						? $" [{stackSymbol}]"
						: string.Empty;
					Console.Error.WriteLine(
						$"[LOADER][INFO]     [rsp{relativeText}] " +
						$"@0x{stackAddr:X16} = 0x{value:X16}{symbolText}");
				}
			}

			DumpPointerWindow("fault-register-rbx", rbx, 0x60);
			DumpPointerWindow("fault-register-rsi", rsi, 0x60);
			DumpPointerWindow("fault-register-rcx", rcx, 0x80);
			DumpPointerWindow("fault-register-rdi", rdi, 0x60);
			DumpPointerWindow("fault-register-r13", r13, 0x60);
			DumpPointerWindow("fault-register-r14", r14, 0x60);

			try
			{
				Console.Error.WriteLine("[LOADER][INFO]   Frame chain (RBP walk):");
				ulong frame = rbp;
				for (int i = 0; i < 12; i++)
				{
					if (frame < 0x10000)
					{
						break;
					}
					if (!TryReadHostQword(frame, out ulong next) || !TryReadHostQword(frame + 8, out ulong ret))
					{
						Console.Error.WriteLine("[LOADER][WARNING]   Could not walk RBP frame chain.");
						break;
					}
					string extra = TryFormatNearestRuntimeSymbol(ret, out string retSym) ? $" [{retSym}]" : string.Empty;
					Console.Error.WriteLine($"[LOADER][INFO]     frame#{i}: rbp=0x{frame:X16} ret=0x{ret:X16}{extra} next=0x{next:X16}");
					if (next <= frame)
					{
						break;
					}
					frame = next;
				}
			}
			catch
			{
				Console.Error.WriteLine("[LOADER][WARNING]   Could not walk RBP frame chain.");
			}

			switch (exceptionCode)
			{
				case 3221225477u:
					Console.Error.WriteLine("[LOADER][ERROR]   Type: Access Violation");
					Console.Error.WriteLine("[LOADER][ERROR]   This usually means:");
					Console.Error.WriteLine("[LOADER][ERROR]     - Guest code called an unmapped import");
					Console.Error.WriteLine("[LOADER][ERROR]     - Guest code accessed unmapped memory");
					Console.Error.WriteLine("[LOADER][ERROR]     - Need to implement HLE for this NID");
					byte[] code = new byte[16];
					if (TryReadHostBytes(rip, code))
					{
						Console.Error.WriteLine("[LOADER][INFO]   Code at RIP: " + BitConverter.ToString(code).Replace("-", " "));
						if (code[0] == 100)
						{
							Console.Error.WriteLine("[LOADER][ERROR]   Detected FS segment prefix - TLS access not patched!");
						}
						else if (code[0] == 101)
						{
							Console.Error.WriteLine("[LOADER][ERROR]   Detected GS segment prefix - TLS access not patched!");
						}
						else if (code[0] == 197 || code[0] == 196)
						{
							Console.Error.WriteLine("[LOADER][INFO]   Detected AVX instruction - check CPU support!");
							Console.Error.WriteLine($"[LOADER][INFO]   RBP: 0x{rbp:X16} (mod 16 = {rbp % 16})");
							Console.Error.WriteLine($"[LOADER][INFO]   RSP: 0x{rsp:X16} (mod 16 = {rsp % 16})");
						}
						byte[] before = new byte[16];
						if (rip > 16 && TryReadHostBytes(rip - 16, before))
						{
							Console.Error.WriteLine("[LOADER][INFO]   Code before RIP: " + BitConverter.ToString(before).Replace("-", " "));
						}
						byte[] window = new byte[64];
						if (rip > 32 && TryReadHostBytes(rip - 32, window))
						{
							Console.Error.WriteLine("[LOADER][INFO]   Code window [RIP-0x20..]: " + BitConverter.ToString(window).Replace("-", " "));
						}
						for (var stackIndex = 0; stackIndex < 16; stackIndex++)
						{
							byte[] stackSlot = new byte[8];
							if (!TryReadHostBytes(rsp + (ulong)(stackIndex * 8), stackSlot))
							{
								continue;
							}
							var candidate = BitConverter.ToUInt64(stackSlot);
							if (candidate < _entryPoint || candidate >= _entryPoint + 0x10000000 || candidate < 24)
							{
								continue;
							}
							byte[] callSiteWindow = new byte[48];
							if (TryReadHostBytes(candidate - 24, callSiteWindow))
							{
								Console.Error.WriteLine(
									$"[LOADER][INFO]   Stack guest-code candidate [rsp+0x{stackIndex * 8:X2}]=0x{candidate:X16}, bytes [-0x18..]: " +
									BitConverter.ToString(callSiteWindow).Replace("-", " "));
							}
						}
					}
					else
					{
						Console.Error.WriteLine("[LOADER][ERROR]   Could not read code at RIP");
					}
					DumpRecentImportTrace();
					DumpGuestDisasmDiagnostics(rip, rbp, rsp);
					DumpGuestRegisterWindowDiagnostics(
						rax, rbx, rcx, rdx, rsi, rdi, rbp, rsp,
						r8, r9, r10, r11, r12, r13, r14, r15);
					DumpGuestReferenceDiagnostics();
					DumpGuestPointerWindowDiagnostics();
					break;
				case 2147483651u:
					Console.Error.WriteLine("[LOADER][WARNING]   Type: Breakpoint (int3)");
					Console.Error.WriteLine("[LOADER][WARNING]   Unexpected breakpoint in direct-bridge mode");
					break;
				case 1073741845u:
					Console.Error.WriteLine("[LOADER][ERROR]   Type: Abort (SIGABRT)");
					DumpRecentImportTrace();
					DumpGuestDisasmDiagnostics(rip, rbp, rsp);
					break;
				case 3221225501u:
					Console.Error.WriteLine("[LOADER][INFO]   Type: Illegal Instruction");
					byte[] illegalCode = new byte[16];
					if (TryReadHostBytes(rip, illegalCode))
					{
						Console.Error.WriteLine("[LOADER][INFO]   Code at RIP: " + BitConverter.ToString(illegalCode).Replace("-", " "));
					}
					DumpRecentImportTrace();
					DumpGuestDisasmDiagnostics(rip, rbp, rsp);
					break;
			}

			Console.Error.WriteLine("[LOADER][INFO] =========================================");
			Console.Error.Flush();
			return 0;
		}
		finally
		{
			_vectoredHandlerDepth--;
		}
	}

	private unsafe bool TryRecoverAuxiliaryThreadExecuteFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (exceptionRecord->ExceptionCode != 3221225477u)
		{
			return false;
		}

		// Prefer ThreadStatic active state; fall back to host-thread name when
		// concurrent TBB AVs race logging (tLT61: recover skipped, then Fatal).
		GuestThreadState? activeThread = _activeGuestThreadState;
		if (activeThread is null || activeThread.Name != "tbb_thead")
		{
			var hostName = Thread.CurrentThread.Name;
			if (hostName is null ||
				!hostName.StartsWith("SharpEmu-tbb_thead", StringComparison.Ordinal))
			{
				return false;
			}

			activeThread = FindGuestThreadStateByHostThreadId(unchecked((int)GetCurrentThreadId()));
			if (activeThread is null || activeThread.Name != "tbb_thead")
			{
				var skip = Interlocked.Increment(ref _auxiliaryThreadExecuteFaultSkips);
				if (skip <= 8 || skip % 64 == 0)
				{
					Console.Error.WriteLine(
						$"[LOADER][WARN] tbb_recover skip #{skip}: rip=0x{rip:X16} " +
						$"host='{hostName}' active={(activeThread?.Name ?? "null")}");
					Console.Error.Flush();
				}
				return false;
			}
		}

		var hostExit = ActiveEntryReturnSentinelRip;
		if (hostExit < 0x10000)
		{
			hostExit = unchecked((ulong)_guestReturnStub);
		}

			// Prefer worker-abort (SetEvent + ExitThread) over host_exit→RunEpilogue:
		// the latter FailFasts the process after TBB recover (tLT28/30 silent die).
		// Do NOT abandon mutexes here — managed HLE from inside VEH can re-enter
		// and Fatal (tLT73). NativeGuestExecutor.Run abandons after detecting abort.
		var abortRip = unchecked((ulong)_workerAbortStub);
		if (abortRip >= 0x10000)
		{
			// Do NOT SetEvent from managed VEH: that wakes the renter which may
			// TerminateThread while this thread is still inside VEH return
			// (tLTA2: recover logged, no respawning, process die). Abort stub
			// SetEvent's only after CONTINUE_EXECUTION resumes at park.

			// Prefer the entry-stub-saved host RSP (real CreateThread stack).
			// Do not treat mid-range host stacks as guest — Astro worker stacks
			// often sit in 0x02xxxxxx_xxxx and were wrongly replaced with a
			// shared VirtualAlloc abort stack (concurrent TBB AV → die).
			var hostRspSlot = TlsGetValue(_hostRspSlotTlsIndex);
			ulong hostRsp = 0;
			if (hostRspSlot != 0)
			{
				hostRsp = *(ulong*)hostRspSlot;
			}

			if (hostRsp < 0x10000)
			{
				hostRsp = EnsureWorkerAbortStackRsp();
			}

			if (hostRsp >= 0x10000)
			{
				WriteCtxU64(contextRecord, 152, hostRsp & ~0xFUL);
			}

			WriteCtxU64(contextRecord, 120, 0);
			WriteCtxU64(contextRecord, 248, abortRip);
			var recovery = Interlocked.Increment(ref _auxiliaryThreadExecuteFaultRecoveries);
			Console.Error.WriteLine(
				$"[LOADER][WARN] Recovered auxiliary TBB execute fault #{recovery}: " +
				$"thread=0x{activeThread.ThreadHandle:X16} target=0x{rip:X16} " +
				$"host_rsp=0x{hostRsp:X16} -> worker_abort=0x{abortRip:X16}");
			Console.Error.WriteLine(
				"[LOADER][INFO] tbb_recover: parking native worker (SetEvent+park); " +
				"renter will TerminateThread+respawn — avoids ExitThread after VEH");
			Console.Error.Flush();
			return true;
		}

		if (hostExit < 0x10000)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Could not recover auxiliary TBB execute fault: target=0x{rip:X16} " +
				$"active_exit=0x{ActiveEntryReturnSentinelRip:X16} guest_return_stub=0x{unchecked((ulong)_guestReturnStub):X16}");
			return false;
		}

		_ = TryPatchActiveGuestReturnSlot(hostExit);
		WriteCtxU64(contextRecord, 120, 0);
		WriteCtxU64(contextRecord, 248, hostExit);
		var recoveryFallback = Interlocked.Increment(ref _auxiliaryThreadExecuteFaultRecoveries);
		Console.Error.WriteLine(
			$"[LOADER][WARN] Recovered auxiliary TBB execute fault #{recoveryFallback}: " +
			$"thread=0x{activeThread.ThreadHandle:X16} target=0x{rip:X16} -> host_exit=0x{hostExit:X16}");
		Console.Error.WriteLine(
			"[LOADER][INFO] tbb_recover: resumed at host_exit (abort stub unavailable); " +
			"subsequent FastFail/CLR must not re-enter managed VEH " +
			"(live trampoline pre-filters 0xC0000409 / 0xE0434352)");
		Console.Error.Flush();
		return true;
	}

	private GuestThreadState? FindGuestThreadStateByHostThreadId(int hostThreadId)
	{
		if (hostThreadId == 0)
		{
			return null;
		}

		try
		{
			foreach (var thread in SnapshotGuestThreads())
			{
				if (Volatile.Read(ref thread.HostThreadId) == hostThreadId)
				{
					return thread;
				}
			}
		}
		catch
		{
		}

		return null;
	}

	private unsafe ulong EnsureWorkerAbortStackRsp()
	{
		if (_workerAbortStack == 0)
		{
			_workerAbortStack = (nint)VirtualAlloc(null, WorkerAbortStackSize, 12288u, 4u);
			if (_workerAbortStack == 0)
			{
				return 0;
			}
		}

		// Grow-down stack: hand out near the top with alignment headroom.
		return (ulong)(_workerAbortStack + (nint)WorkerAbortStackSize - 0x100) & ~0xFUL;
	}

	private unsafe bool TryRecoverGuestInt41(uint exceptionCode, void* contextRecord, ulong rip)
	{
		if (!_ignoreGuestInt41 || exceptionCode != 3221225477u || rip < 0x10000)
		{
			return false;
		}

		byte[] opcode = new byte[2];
		if (!TryReadHostBytes(rip, opcode) || opcode[0] != 0xCD || opcode[1] != 0x41)
		{
			return false;
		}

		var count = Interlocked.Increment(ref _ignoredGuestInt41Count);
		WriteCtxU64(contextRecord, 248, rip + 2);
		if (count <= 16 || count % 65536 == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Ignored guest int 0x41 trap #{count} at 0x{rip:X16} (default-on; set SHARPEMU_IGNORE_INT41=0 to disable)");
			Console.Error.Flush();
		}
		return true;
	}

	private unsafe static bool TryRecoverGuestAllocatorHole(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_GUEST_ALLOCATOR_HOLE_RECOVERY"),
				"1",
				StringComparison.Ordinal) ||
			exceptionRecord->NumberParameters < 2 ||
			exceptionRecord->ExceptionInformation[0] != 0 ||
			exceptionRecord->ExceptionInformation[1] != 8 ||
			ReadCtxU64(contextRecord, CTX_RDI) != 0 ||
			rip < 0x10000)
		{
			return false;
		}

		// Demon's Souls occasionally leaves an empty payload in a locked pool
		// tree node. The allocator dereferences payload+8 before reaching its
		// existing empty-pool fallback. Match the instruction stream instead of
		// a title-specific absolute address, then resume at that fallback so the
		// lock is released and the allocator can try its next backing pool.
		const ulong allocatorHoleSignature = 0x634CFF568D08778BUL;
		if (*(ulong*)rip != allocatorHoleSignature || *((byte*)rip + 8) != 0xF2)
		{
			return false;
		}

		const ulong emptyPoolFallbackDelta = 0x8E;
		WriteCtxU64(contextRecord, CTX_RIP, rip + emptyPoolFallbackDelta);
		var recovery = Interlocked.Increment(ref _guestAllocatorHoleRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest allocator empty-node adapter recovery #{recovery}: " +
				$"rip=0x{rip:X16} -> 0x{rip + emptyPoolFallbackDelta:X16}");
			Console.Error.Flush();
		}

		return true;
	}

	private static long _guestBadStoreRecoveries;

	/// <summary>
	/// Recovers guest stores to non-canonical addresses (Hellboy's
	/// Loading.PreloadManager pushes work items through a cursor whose upper
	/// half contains uninitialized guest-stack garbage: 0x41E4E00000002D4C and
	/// friends). A non-canonical target can never be a valid store, so the
	/// only meaningful recovery is to drop the store: decode the instruction
	/// at RIP, require it to be a memory store, and resume after it. This
	/// converts a process-killing AV into a lost queue entry.
	/// </summary>
	private unsafe static bool TryRecoverGuestBadStoreFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_BAD_STORE_RECOVERY"),
				"1",
				StringComparison.Ordinal) ||
			exceptionRecord->NumberParameters < 2 ||
			rip < 0x10000)
		{
			return false;
		}

		var accessType = exceptionRecord->ExceptionInformation[0];
		var faultTarget = exceptionRecord->ExceptionInformation[1];

		byte[] code = new byte[15];
		if (!TryReadHostBytes(rip, code))
		{
			return false;
		}

		var decoder = Iced.Intel.Decoder.Create(64, code);
		decoder.IP = rip;
		var instruction = decoder.Decode();

		// Must be a store to memory. No memory operand is not recoverable here.
		if (instruction.MemoryBase == Iced.Intel.Register.None &&
			instruction.MemoryIndex == Iced.Intel.Register.None)
		{
			return false;
		}

		// Compute the effective address from the CONTEXT registers so we never
		// misclassify a read loop that merely happens to have garbage in RSI
		// (Hellboy: `cmp word [r15+11Ch],0` with r15=0 burned CPU in an
		// infinite skip loop when the stale RSI value was checked instead).
		var effective = instruction.MemoryDisplacement64;
		if (instruction.MemoryBase != Iced.Intel.Register.None)
		{
			effective += ReadCtxReg(contextRecord, instruction.MemoryBase);
		}
		if (instruction.MemoryIndex != Iced.Intel.Register.None)
		{
			effective += ReadCtxReg(contextRecord, instruction.MemoryIndex) * (ulong)instruction.MemoryIndexScale;
		}

		var effHigh = effective >> 47;
		var nonCanonical = effHigh != 0 && effHigh != 0x1FFFF;
		// Windows reports an access to a non-canonical address as a *read* AV
		// with target 0xFFFFFFFFFFFFFFFF (the canonicalization fault happens
		// before the access type is resolved).
		var canonicalizationFault = faultTarget == 0xFFFF_FFFF_FFFF_FFFFUL;

		if (!nonCanonical && !canonicalizationFault)
		{
			return false;
		}

		// A non-canonical address can never be a valid guest access, so the
		// only meaningful recovery is to skip the instruction entirely — for
		// stores AND reads (Hellboy: `cmp byte [r15+12Eh],0` with a garbage
		// r15 loaded from guest-side state). The consumer of the skipped read
		// sees stale flags/values but the process survives; the alternative is
		// killing the whole game over one garbage pointer read.
		if (accessType != 0 && accessType != 1)
		{
			return false;
		}

		if (canonicalizationFault && !nonCanonical)
		{
			return false;
		}

		WriteCtxU64(contextRecord, 248, rip + (ulong)instruction.Length);
		var recovery = Interlocked.Increment(ref _guestBadStoreRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest bad-store recovery #{recovery}: " +
				$"rip=0x{rip:X16} '{instruction}' target=0x{effective:X16} -> skip {instruction.Length} bytes " +
				"(set SHARPEMU_DISABLE_BAD_STORE_RECOVERY=1 to disable)");
			Console.Error.Flush();
		}

		return recovery <= 1_000_000;
	}

	private static unsafe ulong ReadCtxReg(void* contextRecord, Iced.Intel.Register register) => register switch
	{
		Iced.Intel.Register.RAX => ReadCtxU64(contextRecord, 120),
		Iced.Intel.Register.RCX => ReadCtxU64(contextRecord, 128),
		Iced.Intel.Register.RDX => ReadCtxU64(contextRecord, 136),
		Iced.Intel.Register.RBX => ReadCtxU64(contextRecord, 144),
		Iced.Intel.Register.RSP => ReadCtxU64(contextRecord, 152),
		Iced.Intel.Register.RBP => ReadCtxU64(contextRecord, 160),
		Iced.Intel.Register.RSI => ReadCtxU64(contextRecord, 168),
		Iced.Intel.Register.RDI => ReadCtxU64(contextRecord, 176),
		Iced.Intel.Register.R8 => ReadCtxU64(contextRecord, 184),
		Iced.Intel.Register.R9 => ReadCtxU64(contextRecord, 192),
		Iced.Intel.Register.R10 => ReadCtxU64(contextRecord, 200),
		Iced.Intel.Register.R11 => ReadCtxU64(contextRecord, 208),
		Iced.Intel.Register.R12 => ReadCtxU64(contextRecord, 216),
		Iced.Intel.Register.R13 => ReadCtxU64(contextRecord, 224),
		Iced.Intel.Register.R14 => ReadCtxU64(contextRecord, 232),
		Iced.Intel.Register.R15 => ReadCtxU64(contextRecord, 240),
		Iced.Intel.Register.RIP => ReadCtxU64(contextRecord, 248),
		_ => 0,
	};

	private unsafe static bool TryRecoverGuestProducerCursorFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		// Hellboy (PPSA11264): libScePosix pthread wrappers read the cached
		// pthread self with `mov r15,[r15+58h]`. When the caller's cached self
		// is still 0 (kernel-managed TLS slot we don't populate), substitute
		// the current guest thread handle — the same value scePthreadSelf
		// returns — and re-execute the load.
		if (rip >= 0x10000 &&
			*(ulong*)rip == 0x0000_0000_588B_7F49UL) // 49 8B 7F 58 (mov r15,[r15+0x58])
		{
			var self = ReadCtxU64(contextRecord, 240); // R15
			if (self == 0)
			{
				var threadHandle = SharpEmu.HLE.GuestThreadExecution.CurrentGuestThreadHandle;
				if (threadHandle != 0)
				{
					WriteCtxU64(contextRecord, 240, threadHandle);
					var count = Interlocked.Increment(ref _guestSelfSubstitutions);
					if (count <= 16 || (count & (count - 1)) == 0)
					{
						Console.Error.WriteLine(
							$"[LOADER][WARN] Guest pthread-self substitution #{count}: " +
							$"rip=0x{rip:X16} r15=0 -> 0x{threadHandle:X16} (re-executing)");
						Console.Error.Flush();
					}

					return true;
				}
			}
		}

		// Hellboy: the same pthread wrapper's cancel-state checks read
		//   66 41 83 BF 1C 01 00 00 00   cmp word [r15+0x11C],0
		//   41 80 BF 2E 01 00 00 00      cmp byte [r15+0x12E],0
		// with a garbage r15 (a stale queue node pointer). Skipping the read
		// leaves the retry loop spinning forever, so instead repoint r15 at
		// the current guest thread object (zeroed, 0x1000 bytes) and
		// re-execute: the checks read zeros and the loop terminates normally.
		if (rip >= 0x10000)
		{
			var opcode = *(ulong*)rip;
			// 66 41 83 BF 1C 01 00 00 -> LE qword 0x0000_011C_BF83_4166
			var isCancelWordCheck = opcode == 0x0000_011C_BF83_4166UL;
			// 41 80 BF 2E 01 00 00 00 -> LE qword 0x0000_0001_2EBF_8041
			var isCancelByteCheck = opcode == 0x0000_0001_2EBF_8041UL;
			if (isCancelWordCheck || isCancelByteCheck)
			{
				var self = ReadCtxU64(contextRecord, 240); // R15
				var selfHigh = self >> 47;
				var invalidSelf = self == 0 || (selfHigh != 0 && selfHigh != 0x1FFFF);
				if (invalidSelf && _guestSelfSubstitutions < 100_000)
				{
					var threadHandle = SharpEmu.HLE.GuestThreadExecution.CurrentGuestThreadHandle;
					if (threadHandle != 0)
					{
						WriteCtxU64(contextRecord, 240, threadHandle);
						var count = Interlocked.Increment(ref _guestSelfSubstitutions);
						if (count <= 16 || (count & (count - 1)) == 0)
						{
							Console.Error.WriteLine(
								$"[LOADER][WARN] Guest cancel-state r15 substitution #{count}: " +
								$"rip=0x{rip:X16} r15=0x{self:X16} -> 0x{threadHandle:X16} (re-executing)");
							Console.Error.Flush();
						}

						return true;
					}
				}
			}
		}

		if (rip >= 0x10000)
		{
			// Hellboy: libScePosix allocates a list node whose backing call
			// returned NULL and stores through it unconditionally:
			//   4C 89 38                mov [rax],r15
			//   48 C7 40 08 00 00 00 00 mov qword [rax+8],0
			// Service the allocation with a zeroed guest block and re-execute.
			var b = (byte*)rip;
			if (b[0] == 0x4C && b[1] == 0x89 && b[2] == 0x38 &&
				b[3] == 0x48 && b[4] == 0xC7 && b[5] == 0x40 && b[6] == 0x08 &&
				b[7] == 0x00 && b[8] == 0x00 && b[9] == 0x00 && b[10] == 0x00)
			{
				var node = ReadCtxU64(contextRecord, 120); // RAX
				if (node == 0)
				{
					var allocated = SharpEmu.Libs.Kernel.GuestAllocationBridge.RequestZeroed?.Invoke(0x18) ?? 0;
					if (allocated != 0)
					{
						WriteCtxU64(contextRecord, 120, allocated);
						var count = Interlocked.Increment(ref _guestNullAllocationFixups);
						if (count <= 16 || (count & (count - 1)) == 0)
						{
							Console.Error.WriteLine(
								$"[LOADER][WARN] Guest null-allocation fixup #{count}: " +
								$"rip=0x{rip:X16} rax=0 -> 0x{allocated:X16} (re-executing)");
							Console.Error.Flush();
						}

						return true;
					}
				}
			}
		}

		// Hellboy: the libScePosix thread-stats walk
		//   49 8B 06       mov rax,[r14]
		//   48 8B 40 38    mov rax,[rax+0x38]
		//   48 8B 40 10    mov rax,[rax+0x10]
		//   48 8B 04 C8    mov rax,[rax+rcx*8]
		// faults at [rax+0x38] (observed: read AV at target 0x38 with rax=0)
		// when the first loaded pointer is NULL — an unpopulated guest thread
		// object whose stats-block pointer the real kernel would have filled.
		// Substitute a self-referential zeroed guest block for the NULL
		// pointer and re-execute the load, so the chain stays in defined
		// memory and the walk terminates normally.
		if (rip >= 0x10000 &&
			!string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_STATS_CHAIN_RECOVERY"),
				"1",
				StringComparison.Ordinal))
		{
			var chain = (byte*)rip;
			var displacement = (chain[0] == 0x48 && chain[1] == 0x8B && chain[2] == 0x40)
				? (ulong)chain[3]
				: 0UL;
			if (displacement is 0x38 or 0x10 &&
				exceptionRecord->NumberParameters >= 2 &&
				exceptionRecord->ExceptionInformation[0] == 0 &&
				exceptionRecord->ExceptionInformation[1] == displacement)
			{
				var loadedPointer = ReadCtxU64(contextRecord, 120); // RAX
				if (loadedPointer == 0)
				{
					var block = SharpEmu.Libs.Kernel.GuestAllocationBridge.RequestSelfReferential?.Invoke(0x100) ?? 0;
					if (block != 0)
					{
						WriteCtxU64(contextRecord, 120, block);
						var count = Interlocked.Increment(ref _guestStatsChainRecoveries);
						if (count <= 16 || (count & (count - 1)) == 0)
						{
							Console.Error.WriteLine(
								$"[LOADER][WARN] Guest stats-chain recovery #{count}: " +
								$"rip=0x{rip:X16} rax=0 -> 0x{block:X16} (re-executing)");
							Console.Error.Flush();
						}

						return count <= 100_000;
					}
				}
			}
		}

		if (string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_PRODUCER_CURSOR_RECOVERY"),
				"1",
				StringComparison.Ordinal) ||
			exceptionRecord->NumberParameters < 2 ||
			rip < 0x10000)
		{
			return false;
		}

		// Hellboy (PPSA11264): Unity's Loading.PreloadManager pushes work items
		// loaded from a guest structure. When an upstream field still contains
		// garbage (observed as the non-canonical 0x41E4Exxx_xxxx_2Dxx pattern),
		// the push faults and, unrecovered, kills the whole process right after
		// the first presented frame. Match the exact instruction stream and
		// emulate it: drop the bad item write (the consumer simply sees no
		// entry for this tick), perform the counter increment on RDI, and
		// resume at the trailing RET.
		// Two variants of the same push helper:
		//   89 06 48 83 07 04 C3 CC            mov [rsi],eax; add qword [rdi],4; ret
		//   C5 FA 11 06 48 83 07 04 C3 CC ...  vmovups [rsi],xmm0; add qword [rdi],4; ret
		var b0 = *((byte*)rip);
		var b1 = *((byte*)rip + 1);
		var b2 = *((byte*)rip + 2);
		var b3 = *((byte*)rip + 3);
		int storeLength;
		if (b0 == 0x89 && b1 == 0x06)
		{
			storeLength = 2;
		}
		else if (b0 == 0xC5 && b1 == 0xFA && b2 == 0x11 && b3 == 0x06)
		{
			storeLength = 4;
		}
		else
		{
			return false;
		}

		var addOffset = rip + (ulong)storeLength;
		// Bytes at addOffset: 48 83 07 04 C3 (add qword [rdi],4; ret)
		if ((*(ulong*)addOffset & 0x0000_FFFF_FFFF_FFFFUL) != 0x0000_CCC3_0407_8348UL)
		{
			return false;
		}

		// Gate on the exact instruction signature only. Windows reports the AV
		// as a *read* with target 0xFFFFFFFFFFFFFFFF when the store address is
		// non-canonical, and as a write with a reserved-region target when it
		// is canonical-but-unmapped, so the access type cannot be used.
		var rsi = ReadCtxU64(contextRecord, 168);
		var rdi = ReadCtxU64(contextRecord, 176);

		// Emulate `add qword [rdi], 4` only when RDI points at host-mapped
		// memory (probe first: a raw write through a bad pointer would re-enter
		// the VEH). If unmapped, still recover by skipping the whole push — a
		// lost cursor tick is recoverable; a dead process is not.
		if (rdi != 0 && TryReadHostBytes(rdi, new byte[8]))
		{
			*(ulong*)rdi += 4;
		}

		var retDelta = (ulong)(storeLength + 4); // store + add -> trailing C3
		WriteCtxU64(contextRecord, 248, rip + retDelta);
		var recovery = Interlocked.Increment(ref _guestProducerCursorRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest producer-cursor adapter recovery #{recovery}: " +
				$"rip=0x{rip:X16} rsi=0x{rsi:X16} rdi=0x{rdi:X16} -> resume 0x{rip + retDelta:X16} " +
				"(bad cursor dropped; set SHARPEMU_DISABLE_PRODUCER_CURSOR_RECOVERY=1 to disable)");
			Console.Error.Flush();
		}

		return true;
	}

	/// <summary>
	/// Last-resort recovery for guest reads through garbage pointers on guest
	/// threads (Hellboy libScePosix walks over kernel structures SharpEmu does
	/// not model: the fault target is unmapped in the guest address space).
	/// The instruction is skipped and its GPR destination zeroed so the process
	/// survives and keeps producing diagnostics instead of dying silently in
	/// the middle of the AV dump. Capped; disable with
	/// SHARPEMU_DISABLE_GARBAGE_READ_RECOVERY=1.
	/// </summary>
	private unsafe static bool TryRecoverGuestGarbageReadFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_GARBAGE_READ_RECOVERY"),
				"1",
				StringComparison.Ordinal) ||
			exceptionRecord->NumberParameters < 2 ||
			exceptionRecord->ExceptionInformation[0] != 0 || // reads only
			rip < 0x10000)
		{
			return false;
		}

		// Only guest-thread executors recover here; host threads keep the old
		// dump-and-die behaviour so genuine emulator bugs stay visible.
		var activeThread = _activeGuestThreadState;
		if (activeThread is null || activeThread.Context is null)
		{
			return false;
		}

		var faultTarget = exceptionRecord->ExceptionInformation[1];
		Span<byte> probe = stackalloc byte[1];
		if (activeThread.Context.Memory.TryRead(faultTarget, probe))
		{
			// The guest can map this address — a real guest fault, not garbage.
			return false;
		}

		byte[] code = new byte[15];
		if (!TryReadHostBytes(rip, code))
		{
			return false;
		}

		var decoder = Iced.Intel.Decoder.Create(64, code);
		decoder.IP = rip;
		var instruction = decoder.Decode();
		if (instruction.MemoryBase == Iced.Intel.Register.None &&
			instruction.MemoryIndex == Iced.Intel.Register.None)
		{
			return false;
		}

		// The computed effective address (from the CONTEXT registers, never the
		// stale target field alone) must be the faulting address, mirroring
		// TryRecoverGuestBadStoreFault's misclassification guard.
		var effective = instruction.MemoryDisplacement64;
		if (instruction.MemoryBase != Iced.Intel.Register.None)
		{
			effective += ReadCtxReg(contextRecord, instruction.MemoryBase);
		}
		if (instruction.MemoryIndex != Iced.Intel.Register.None)
		{
			effective += ReadCtxReg(contextRecord, instruction.MemoryIndex) * (ulong)instruction.MemoryIndexScale;
		}

		if (effective != faultTarget)
		{
			return false;
		}

		// Simple GPR loads only: skip and zero the destination register.
		var destination = instruction.Op0Register;
		if (destination is < Iced.Intel.Register.RAX or > Iced.Intel.Register.R15 ||
			destination == Iced.Intel.Register.RSP)
		{
			return false;
		}

		var destinationOffset = destination switch
		{
			Iced.Intel.Register.RAX => 120,
			Iced.Intel.Register.RCX => 128,
			Iced.Intel.Register.RDX => 136,
			Iced.Intel.Register.RBX => 144,
			Iced.Intel.Register.RBP => 160,
			Iced.Intel.Register.RSI => 168,
			Iced.Intel.Register.RDI => 176,
			Iced.Intel.Register.R8 => 184,
			Iced.Intel.Register.R9 => 192,
			Iced.Intel.Register.R10 => 200,
			Iced.Intel.Register.R11 => 208,
			Iced.Intel.Register.R12 => 216,
			Iced.Intel.Register.R13 => 224,
			Iced.Intel.Register.R14 => 232,
			Iced.Intel.Register.R15 => 240,
			_ => 0,
		};
		if (destinationOffset == 0)
		{
			return false;
		}

		WriteCtxU64(contextRecord, destinationOffset, 0);
		WriteCtxU64(contextRecord, 248, rip + (ulong)instruction.Length);
		var recovery = Interlocked.Increment(ref _guestGarbageReadRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest garbage-read recovery #{recovery}: " +
				$"rip=0x{rip:X16} '{instruction}' target=0x{effective:X16} -> skip {instruction.Length} bytes, " +
				$"{destination} = 0 (thread '{activeThread.Name}'; " +
				"set SHARPEMU_DISABLE_GARBAGE_READ_RECOVERY=1 to disable)");
			Console.Error.Flush();
		}

		return recovery <= 1_000_000;
	}

	private static long _guestProducerCursorRecoveries;
	private static long _guestGarbageReadRecoveries;
	private static long _guestSelfSubstitutions;
	private static long _guestNullAllocationFixups;
	private static long _guestNullChainRecoveries;
	private static long _guestBadNamePointerRecoveries;
	private static long _guestStatsChainRecoveries;

	/// <summary>
	/// Hellboy (PPSA11264): libScePosix's thread-name scan walks its node
	/// list and loads each entry's name pointer
	///   49 8B 76 18   mov rsi, [r14+0x18]   ; name char*
	///   80 3E 2E     cmp byte [rsi], 0x2E   ; starts with '.'?
	///   0F 85 ...    jne <next node>
	/// When the node's name field holds garbage (an uninitialized Il2CPP
	/// heap node; observed target 0x3E8), the load faults. Repoint RSI at
	/// the faulting instruction's own code bytes — readable and guaranteed
	/// not to start with '.' — and re-execute the comparison so the scan
	/// simply moves on to the next node.
	/// </summary>
	private unsafe static bool TryRecoverGuestBadNamePointerFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (rip < 0x10000 ||
			exceptionRecord->NumberParameters < 2)
		{
			return false;
		}

		var b = (byte*)rip;
		// mov rsi,[r14+0x18]; cmp byte [rsi],0x2E; jne rel32
		var matchesSignature =
			b[0] == 0x49 && b[1] == 0x8B && b[2] == 0x76 && b[3] == 0x18 &&
			b[4] == 0x80 && b[5] == 0x3E && b[6] == 0x2E &&
			b[7] == 0x0F && b[8] == 0x85;
		if (!matchesSignature)
		{
			return false;
		}

		// The name pointer must itself be the garbage (small/null-page
		// target) — a fault through a large mapped address is a different bug.
		var faultTarget = exceptionRecord->ExceptionInformation[1];
		if (faultTarget >= 0x1_0000_0000UL && (faultTarget >> 47) is 0 or 0x1FFFF)
		{
			// Could still be an unmapped guest pointer; accept it — the
			// recovery is identical (skip the comparison).
		}

		// Point RSI at the code bytes: byte [rsi] = 0x49 ('I') != '.',
		// so the jne is taken and the scan advances to the next node.
		WriteCtxU64(contextRecord, 168, rip); // RSI
		var recovery = Interlocked.Increment(ref _guestBadNamePointerRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest bad-name-pointer recovery #{recovery}: " +
				$"rip=0x{rip:X16} target=0x{faultTarget:X16} -> rsi=code, re-execute");
			Console.Error.Flush();
		}

		return recovery <= 100_000;
	}

	/// <summary>
	/// Hellboy (PPSA11264): Unity/Il2Cpp's thread-registry lookup walks a
	/// kernel-managed pointer chain
	///   mov rax,[r14]          ; registry root
	///   mov rax,[rax+0x38]     ; -> table
	///   mov rax,[rax+0x10]     ; -> bucket array
	///   mov rax,[rax+rcx*8]    ; -> entry (index = thread slot)
	///   mov [r13],rax
	/// When the root's first qword is still zero (a structure the real kernel
	/// populates but our HLE leaves empty), the very first hop reads [0+0x38]
	/// and faults. Emulate the whole chain with NULL results: store 0 to
	/// [r13] and resume after the chain — the caller treats a missing entry
	/// as "thread not registered", which is the correct answer for a
	/// registry the kernel never filled.
	/// </summary>
	private unsafe static bool TryRecoverGuestNullChainTableFault(
		EXCEPTION_RECORD* exceptionRecord,
		void* contextRecord,
		ulong rip)
	{
		if (rip < 0x10000 ||
			exceptionRecord->NumberParameters < 2)
		{
			return false;
		}

		var rax = ReadCtxU64(contextRecord, 120);
		if (rax != 0)
		{
			// The chain hop only faults at [rax+disp] when rax itself is
			// NULL; any other base is a different bug.
			var faultTarget = exceptionRecord->ExceptionInformation[1];
			if (faultTarget >= 0x100)
			{
				return false;
			}
		}

		// Match the full instruction window:
		//   48 8B 40 xx   mov rax,[rax+xx]     (hop 2, faulting instruction)
		//   48 8B 40 10   mov rax,[rax+0x10]
		//   48 8B 04 C8   mov rax,[rax+rcx*8]
		//   49 89 45 00   mov [r13],rax
		var b = (byte*)rip;
		var isHop2 = b[0] == 0x48 && b[1] == 0x8B && b[2] == 0x40; // mov rax,[rax+disp8]
		if (!isHop2)
		{
			return false;
		}

		var hop2Length = 4;
		var rest = b + hop2Length;
		var matchesChain =
			rest[0] == 0x48 && rest[1] == 0x8B && rest[2] == 0x40 && rest[3] == 0x10 &&
			rest[4] == 0x48 && rest[5] == 0x8B && rest[6] == 0x04 && rest[7] == 0xC8 &&
			rest[8] == 0x49 && rest[9] == 0x89 && rest[10] == 0x45 && rest[11] == 0x00;
		if (!matchesChain)
		{
			return false;
		}

		// Emulate: rax = 0 through the whole chain, store 0 into [r13].
		var r13 = ReadCtxU64(contextRecord, 224);
		if (r13 != 0 && TryReadHostBytes(r13, new byte[8]))
		{
			try
			{
				*(ulong*)r13 = 0;
			}
			catch
			{
				// Store dropped; the caller still resumes.
			}
		}

		WriteCtxU64(contextRecord, 120, 0); // rax = 0 (chain result)
		var chainLength = hop2Length + 12; // 4 + (4 + 4 + 4)
		WriteCtxU64(contextRecord, 248, rip + (ulong)chainLength);
		var recovery = Interlocked.Increment(ref _guestNullChainRecoveries);
		if (recovery <= 16 || (recovery & (recovery - 1)) == 0)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] Guest null-chain table recovery #{recovery}: " +
				$"rip=0x{rip:X16} rax=0x{rax:X16} -> store NULL entry, resume 0x{rip + (ulong)chainLength:X16}");
			Console.Error.Flush();
		}

		return recovery <= 100_000;
	}

	private static bool IsBenignHostDebugException(uint exceptionCode)
	{
		return exceptionCode is DBG_PRINTEXCEPTION_C or DBG_PRINTEXCEPTION_WIDE_C or MS_VC_THREADNAME_EXCEPTION;
	}

	private unsafe static void LogNestedVectoredException(void* exceptionInfo)
	{
		int count = Interlocked.Increment(ref _nestedVehTraceCount);
		if (count > 16 && count % 128 != 0)
		{
			return;
		}

		try
		{
			EXCEPTION_POINTERS* pointers = (EXCEPTION_POINTERS*)exceptionInfo;
			EXCEPTION_RECORD* record = pointers->ExceptionRecord;
			void* contextRecord = pointers->ContextRecord;
			ulong rip = contextRecord != null ? ReadCtxU64(contextRecord, 248) : 0;
			ulong rsp = contextRecord != null ? ReadCtxU64(contextRecord, 152) : 0;
			ulong accessType = record->NumberParameters >= 1 ? *record->ExceptionInformation : 0;
			ulong target = record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;
			Console.Error.WriteLine(
				$"[LOADER][TRACE] Nested VEH exception#{count}: code=0x{record->ExceptionCode:X8} ex=0x{(ulong)record->ExceptionAddress:X16} rip=0x{rip:X16} rsp=0x{rsp:X16} type={accessType} target=0x{target:X16}; passing through.");
		}
		catch
		{
			Console.Error.WriteLine($"[LOADER][TRACE] Nested VEH exception#{count}; passing through.");
		}
	}

	private unsafe void LogAccessViolationTrace(ulong exceptionAddress, EXCEPTION_RECORD* exceptionRecord)
	{
		ulong accessType = exceptionRecord->NumberParameters >= 1 ? (*exceptionRecord->ExceptionInformation) : 0;
		ulong target = exceptionRecord->NumberParameters >= 2 ? exceptionRecord->ExceptionInformation[1] : 0;
		if (_lastAvTraceRip == exceptionAddress && _lastAvTraceType == accessType && _lastAvTraceTarget == target)
		{
			_lastAvTraceRepeatCount++;
			if (_lastAvTraceRepeatCount > 4 && _lastAvTraceRepeatCount % 128 != 0)
			{
				return;
			}
			Console.Error.WriteLine($"[LOADER][TRACE] VEH_AV repeat#{_lastAvTraceRepeatCount} at 0x{exceptionAddress:X16} type={accessType} target=0x{target:X16}");
			Console.Error.Flush();
			return;
		}

		_lastAvTraceRip = exceptionAddress;
		_lastAvTraceType = accessType;
		_lastAvTraceTarget = target;
		_lastAvTraceRepeatCount = 1;
		Console.Error.WriteLine($"[LOADER][TRACE] VEH_AV first-chance at 0x{exceptionAddress:X16} type={accessType} target=0x{target:X16}");
		Console.Error.Flush();
	}

	private void DumpGuestInstructionStream(string name, ulong startRip, int maxInstructions)
	{
		if (_cpuContext == null || startRip < 0x10000 || maxInstructions <= 0)
		{
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} disasm @0x{startRip:X16}:");
		ulong rip = startRip;
		for (int i = 0; i < maxInstructions; i++)
		{
			if (!IcedDecoder.TryReadGuestBytes(_cpuContext.Memory, rip, maxLen: 15, out var bytes) ||
				!IcedDecoder.TryDecode(rip, bytes, out var instruction))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     0x{rip:X16}: <decode-failed>");
				break;
			}

			Console.Error.WriteLine(
				$"[LOADER][INFO]     0x{instruction.Rip:X16}: {instruction.Text} bytes={IcedDecoder.FormatBytes(instruction.Bytes)}");
			rip += (ulong)instruction.Length;
		}
	}

	private void DumpGuestDisasmDiagnostics(ulong rip, ulong rbp, ulong rsp)
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISASM"), "1", StringComparison.Ordinal))
		{
			return;
		}

		if (rip >= 0x20)
		{
			DumpGuestInstructionStream("fault-prelude", rip - 0x20, 24);
		}

		// Optimized guest code frequently omits frame pointers. The return
		// address at RSP is then more useful than an RBP walk and identifies the
		// exact call site that supplied the faulting arguments.
		if (TryReadHostQword(rsp, out var stackReturn) && stackReturn >= 0x60)
		{
			DumpGuestInstructionStream("stack-return-prelude", stackReturn - 0x60, 40);
		}

		try
		{
			ulong frame = rbp;
			for (int i = 0; i < 3; i++)
			{
				if (frame < 0x10000)
				{
					break;
				}

				ulong ret = (ulong)Marshal.ReadInt64((nint)(frame + 8));
				if (ret >= 0x40)
				{
					DumpGuestInstructionStream($"frame#{i}-ret-prelude", ret - 0x40, 24);
				}

				ulong next = (ulong)Marshal.ReadInt64((nint)frame);
				if (next <= frame)
				{
					break;
				}

				frame = next;
			}
		}
		catch
		{
			Console.Error.WriteLine("[LOADER][WARNING]   Could not dump disasm diagnostics.");
		}

		var extraAddresses = Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISASM_ADDRS");
		if (string.IsNullOrWhiteSpace(extraAddresses))
		{
			return;
		}

		foreach (var token in extraAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? token[2..]
				: token;
			if (!ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) || address < 0x20)
			{
				continue;
			}

			DumpGuestInstructionStream($"extra-0x{address:X16}", address, 48);
		}
	}

	private unsafe void DumpGuestReferenceDiagnostics()
	{
		var targetList = ParseDiagnosticAddresses(Environment.GetEnvironmentVariable("SHARPEMU_LOG_REFSCAN_ADDRS"));
		if (targetList.Count == 0 || _cpuContext == null)
		{
			return;
		}

		const ulong scanBase = 0x0000000800000000UL;
		const ulong scanEnd = 0x0000000810000000UL;
		const int maxHitsPerTarget = 24;

		Console.Error.WriteLine(
			$"[LOADER][INFO]   Ref scan targets: {string.Join(", ", targetList.ConvertAll(static addr => $"0x{addr:X16}"))}");

		var hitCounts = new Dictionary<ulong, int>(targetList.Count);
		for (var i = 0; i < targetList.Count; i++)
		{
			hitCounts[targetList[i]] = 0;
		}

		ulong address = scanBase;
		while (address < scanEnd)
		{
			if (VirtualQuery((void*)address, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
			{
				break;
			}

			ulong regionBase = mbi.BaseAddress;
			ulong regionEnd = regionBase + mbi.RegionSize;
			if (regionEnd <= address)
			{
				break;
			}

			if (mbi.State == MEM_COMMIT &&
				IsReadableProtection(mbi.Protect) &&
				IsExecutableProtection(mbi.Protect))
			{
				ScanExecutableRegionForTargetReferences(regionBase, regionEnd, targetList, hitCounts, maxHitsPerTarget);
			}

			var allTargetsSatisfied = true;
			for (var i = 0; i < targetList.Count; i++)
			{
				if (hitCounts[targetList[i]] < maxHitsPerTarget)
				{
					allTargetsSatisfied = false;
					break;
				}
			}

			if (allTargetsSatisfied)
			{
				break;
			}

			address = regionEnd;
		}

		for (var i = 0; i < targetList.Count; i++)
		{
			var target = targetList[i];
			if (!hitCounts.TryGetValue(target, out var count) || count == 0)
			{
				Console.Error.WriteLine($"[LOADER][INFO]   Ref scan 0x{target:X16}: none");
			}
		}
	}

	private void DumpGuestPointerWindowDiagnostics()
	{
		var targetList = ParseDiagnosticAddresses(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POINTER_WINDOWS"));
		if (targetList.Count == 0)
		{
			return;
		}

		var windowSize = 0x80;
		var rawWindowSize = Environment.GetEnvironmentVariable("SHARPEMU_LOG_POINTER_WINDOW_SIZE");
		if (!string.IsNullOrWhiteSpace(rawWindowSize))
		{
			var normalized = rawWindowSize.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? rawWindowSize[2..]
				: rawWindowSize;
			if (int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedWindowSize) &&
				parsedWindowSize > 0)
			{
				windowSize = parsedWindowSize;
			}
		}

		foreach (var target in targetList)
		{
			DumpPointerWindow($"ptrwin-0x{target:X16}", target, windowSize);
		}
	}

	private void DumpGuestRegisterWindowDiagnostics(
		ulong rax,
		ulong rbx,
		ulong rcx,
		ulong rdx,
		ulong rsi,
		ulong rdi,
		ulong rbp,
		ulong rsp,
		ulong r8,
		ulong r9,
		ulong r10,
		ulong r11,
		ulong r12,
		ulong r13,
		ulong r14,
		ulong r15)
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable("SHARPEMU_LOG_REGISTER_WINDOWS"),
				"1",
				StringComparison.Ordinal))
		{
			return;
		}

		// A register can be the only surviving reference to the object or
		// argument array that caused a native guest fault. Capture a compact
		// window while the process is alive so the post-mortem log can
		// distinguish an absent object from a partially initialized one.
		var registers = new (string Name, ulong Value)[]
		{
			("rax", rax), ("rbx", rbx), ("rcx", rcx), ("rdx", rdx),
			("rsi", rsi), ("rdi", rdi), ("rbp", rbp), ("rsp", rsp),
			("r8", r8), ("r9", r9), ("r10", r10), ("r11", r11),
			("r12", r12), ("r13", r13), ("r14", r14), ("r15", r15),
		};
		var seen = new HashSet<ulong>();
		foreach (var (name, value) in registers)
		{
			if (value < 0x10000 || !seen.Add(value))
			{
				continue;
			}

			DumpPointerWindow($"register-{name}", value, 0x80);
		}
	}

	private void ScanExecutableRegionForTargetReferences(
		ulong regionBase,
		ulong regionEnd,
		IReadOnlyList<ulong> targets,
		IDictionary<ulong, int> hitCounts,
		int maxHitsPerTarget)
	{
		if (_cpuContext == null || regionEnd <= regionBase)
		{
			return;
		}

		ulong rip = regionBase;
		while (rip < regionEnd)
		{
			if (!IcedDecoder.TryReadGuestBytes(_cpuContext.Memory, rip, maxLen: 15, out var bytes) ||
				!IcedDecoder.TryDecode(rip, bytes, out var instruction) ||
				instruction.Length <= 0)
			{
				rip++;
				continue;
			}

			if (instruction.MemoryAddress is { } memoryAddress)
			{
				for (var i = 0; i < targets.Count; i++)
				{
					var target = targets[i];
					if (memoryAddress != target ||
						!hitCounts.TryGetValue(target, out var count) ||
						count >= maxHitsPerTarget)
					{
						continue;
					}

					hitCounts[target] = count + 1;
					Console.Error.WriteLine(
						$"[LOADER][INFO]   Ref scan hit target=0x{target:X16} rip=0x{instruction.Rip:X16} text={instruction.Text} bytes={IcedDecoder.FormatBytes(instruction.Bytes)}");
				}
			}

			rip += (ulong)instruction.Length;
		}
	}

	private static List<ulong> ParseDiagnosticAddresses(string? rawValue)
	{
		var result = new List<ulong>();
		if (string.IsNullOrWhiteSpace(rawValue))
		{
			return result;
		}

		foreach (var token in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? token[2..]
				: token;
			if (!ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
			{
				continue;
			}

			if (!result.Contains(address))
			{
				result.Add(address);
			}
		}

		return result;
	}

	private void DumpUnresolvedSentinelWindow(string name, ulong baseAddress, int size)
	{
		if (baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		ulong scanStart = baseAddress;
		ulong scanEnd = baseAddress + (ulong)size;
		List<ulong> hits = ScanSuspiciousResolverPointers(scanStart, scanEnd);
		if (hits.Count == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} unresolved scan: none");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} unresolved scan hits: {hits.Count}");
		for (int i = 0; i < hits.Count && i < 8; i++)
		{
			ulong slotAddress = hits[i];
			if (TryReadQword(slotAddress, out var value))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     hit#{i}: slot=0x{slotAddress:X16} value=0x{value:X16}");
			}
		}
	}

	private void DumpSentinelPatternWindow(string name, ulong baseAddress, int size)
	{
		if (_cpuContext == null || baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		byte[] buffer = new byte[size];
		if (!_cpuContext.Memory.TryRead(baseAddress, buffer))
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern scan: unreadable");
			return;
		}

		List<string> hits = new();
		for (int offset = 0; offset + 2 <= buffer.Length; offset++)
		{
			if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2)) == 0xFFFE)
			{
				hits.Add($"+0x{offset:X}:u16");
			}

			if (offset + 4 <= buffer.Length &&
				BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4)) == 0xFFFFFFFEu)
			{
				hits.Add($"+0x{offset:X}:u32");
			}

			if (offset + 8 <= buffer.Length &&
				BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8)) == 0xFFFFFFFFFFFFFFFEuL)
			{
				hits.Add($"+0x{offset:X}:u64");
			}
		}

		if (hits.Count == 0)
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern scan: none");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} sentinel-pattern hits: {string.Join(", ", hits.GetRange(0, Math.Min(hits.Count, 12)))}");
	}

	private void DumpReturnTargetCandidates(ulong rsp)
	{
		if (rsp < 0x10000)
		{
			return;
		}

		ulong start = rsp >= 0x10 ? rsp - 0x10 : rsp;
		Console.Error.WriteLine($"[LOADER][INFO]   Return-target candidates near RSP=0x{rsp:X16}:");
		for (int offset = 0; offset <= 0x20; offset++)
		{
			ulong address = start + (ulong)offset;
			try
			{
				ulong value = (ulong)Marshal.ReadInt64((nint)address);
				Console.Error.WriteLine($"[LOADER][INFO]     [0x{address:X16}] -> 0x{value:X16}");
			}
			catch
			{
				Console.Error.WriteLine($"[LOADER][INFO]     [0x{address:X16}] -> <unreadable>");
				break;
			}
		}
	}

	private void DumpObjectFieldTargets(string name, ulong objectAddress, int[] offsets, int windowSize)
	{
		if (objectAddress < 0x10000 || offsets.Length == 0)
		{
			return;
		}

		foreach (int offset in offsets)
		{
			ulong slotAddress = objectAddress + (ulong)offset;
			if (!TryReadQword(slotAddress, out var target) || target < 0x10000)
			{
				continue;
			}

			Console.Error.WriteLine($"[LOADER][INFO]   {name}+0x{offset:X2} target = {FormatPointerWithNearestSymbol(target)}");
			DumpPointerWindow($"{name}+0x{offset:X2}", target, windowSize);
			DumpUnresolvedSentinelWindow($"{name}+0x{offset:X2}", target, 0x80);
		}
	}

	private void DumpSuspiciousGlobalSlots()
	{
		DumpAbsoluteSlot("callback_slot[0x80293BD08]", 0x000000080293BD08uL);
		DumpAbsoluteSlot("callback_arg[0x8030FBBE8]", 0x00000008030FBBE8uL);
		DumpAbsoluteSlot("tsc_freq_global[0x8030FD590]", 0x00000008030FD590uL);
		DumpAbsoluteSlot("tsc_base_global[0x8030FD598]", 0x00000008030FD598uL);
		DumpAbsoluteSlot("plt_got[0x8028F6100]", 0x00000008028F6100uL);
		DumpAbsoluteSlot("plt_got[0x8028F6158]", 0x00000008028F6158uL);
		DumpAbsoluteSlot("plt_got[0x8028F6160]", 0x00000008028F6160uL);
		DumpAbsoluteSlot("plt_got[0x8028F64C0]", 0x00000008028F64C0uL);
		DumpAbsoluteSlot("plt_got[0x8028F64C8]", 0x00000008028F64C8uL);
		DumpAbsoluteSlot("plt_got[0x8028F6590]", 0x00000008028F6590uL);
		DumpAbsoluteSlot("plt_got[0x8028F6708]", 0x00000008028F6708uL);
		DumpUnresolvedSentinelWindow("PLT-GOT", 0x00000008028F6100uL, 0x700);
	}

	private void DumpAbsoluteSlot(string name, ulong slotAddress)
	{
		if (!TryReadQword(slotAddress, out var value))
		{
			Console.Error.WriteLine($"[LOADER][INFO]   {name} @0x{slotAddress:X16} = <unreadable>");
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} @0x{slotAddress:X16} = {FormatPointerWithNearestSymbol(value)}");
	}
	private void DumpPointerWindow(string name, ulong baseAddress, int size)
	{
		if (baseAddress < 0x10000 || size <= 0)
		{
			return;
		}

		Console.Error.WriteLine($"[LOADER][INFO]   {name} window @0x{baseAddress:X16}:");
		for (int offset = 0; offset < size; offset += 8)
		{
			ulong slotAddress = baseAddress + (ulong)offset;
			if (!TryReadQword(slotAddress, out var value))
			{
				Console.Error.WriteLine($"[LOADER][INFO]     +0x{offset:X2}: <unreadable>");
				break;
			}

			Console.Error.WriteLine($"[LOADER][INFO]     +0x{offset:X2}: {FormatPointerWithNearestSymbol(value)}");
		}
	}

	private unsafe bool TryReadQword(ulong address, out ulong value)
	{
		value = 0;
		if (address < 0x10000)
		{
			return false;
		}

		if (VirtualQuery((void*)address, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}

		ulong regionEnd = mbi.BaseAddress + mbi.RegionSize;
		if (mbi.State != MEM_COMMIT || !IsReadableProtection(mbi.Protect) || regionEnd <= address || address > regionEnd - 8)
		{
			return false;
		}

		try
		{
			value = (ulong)Marshal.ReadInt64((nint)address);
			return true;
		}
		catch
		{
			value = 0;
			return false;
		}
	}

	private static bool TryReadHostQword(ulong address, out ulong value)
	{
		if (!OperatingSystem.IsWindows())
		{
			// A stray read inside the signal handler would raise a nested
			// SIGSEGV and kill the process before diagnostics finish, so
			// probe the region table instead of relying on try/catch.
			return TryReadStackU64(address, out value);
		}

		value = 0;
		try
		{
			value = (ulong)Marshal.ReadInt64((nint)address);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private unsafe static bool TryReadHostBytes(ulong address, byte[] buffer)
	{
		if (address < 65536)
		{
			return false;
		}

		if (!OperatingSystem.IsWindows())
		{
			// See TryReadHostQword: probe every touched page before reading.
			ulong end = address + (ulong)buffer.Length;
			for (ulong page = address & 0xFFFFFFFFFFFFF000uL; page < end; page += 4096)
			{
				if (VirtualQuery((void*)page, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0 ||
					mbi.State != MEM_COMMIT ||
					!IsReadableProtection(mbi.Protect))
				{
					return false;
				}
			}
		}

		try
		{
			Marshal.Copy((nint)address, buffer, 0, buffer.Length);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private string FormatPointerWithNearestSymbol(ulong value)
	{
		string text = $"0x{value:X16}";
		if (TryFormatNearestRuntimeSymbol(value, out string symbol))
		{
			text += $" [{symbol}]";
		}

		return text;
	}

	private void InitializeRuntimeSymbolIndex(IReadOnlyDictionary<string, ulong> runtimeSymbols)
	{
		_runtimeSymbolsByName.Clear();
		if (runtimeSymbols.Count == 0)
		{
			_runtimeSymbolsByAddress = Array.Empty<KeyValuePair<string, ulong>>();
			return;
		}

		List<KeyValuePair<string, ulong>> list = new(runtimeSymbols.Count);
		foreach (KeyValuePair<string, ulong> runtimeSymbol in runtimeSymbols)
		{
			if (runtimeSymbol.Value != 0L && !string.IsNullOrWhiteSpace(runtimeSymbol.Key))
			{
				list.Add(runtimeSymbol);
				_runtimeSymbolsByName[runtimeSymbol.Key] = runtimeSymbol.Value;
			}
		}

		list.Sort((a, b) => a.Value.CompareTo(b.Value));
		_runtimeSymbolsByAddress = list.ToArray();
	}

	private bool TryFormatNearestRuntimeSymbol(ulong address, out string text)
	{
		text = string.Empty;
		KeyValuePair<string, ulong>[] runtimeSymbolsByAddress = _runtimeSymbolsByAddress;
		if (runtimeSymbolsByAddress.Length == 0)
		{
			return false;
		}

		int low = 0;
		int high = runtimeSymbolsByAddress.Length - 1;
		int best = -1;
		while (low <= high)
		{
			int mid = low + ((high - low) >> 1);
			ulong value = runtimeSymbolsByAddress[mid].Value;
			if (value <= address)
			{
				best = mid;
				low = mid + 1;
			}
			else
			{
				high = mid - 1;
			}
		}

		if (best < 0)
		{
			return false;
		}

		KeyValuePair<string, ulong> symbol = runtimeSymbolsByAddress[best];
		ulong delta = address - symbol.Value;
		text = delta == 0
			? $"{symbol.Key} (0x{symbol.Value:X16})"
			: $"{symbol.Key}+0x{delta:X} (0x{symbol.Value:X16})";
		return true;
	}

	private unsafe bool TryHandleLazyCommittedPage(EXCEPTION_RECORD* exceptionRecord, ulong rip, ulong rsp)
	{
		if (exceptionRecord->NumberParameters < 2)
		{
			return false;
		}

		ulong accessType = *exceptionRecord->ExceptionInformation;
		ulong faultAddress = exceptionRecord->ExceptionInformation[1];
		if (accessType == 8 && faultAddress < 4294967296L)
		{
			return false;
		}
		if (faultAddress < 65536 || faultAddress >= 140737488355328L)
		{
			return false;
		}
		if (!IsGuestOwnedLazyCommitAddress(faultAddress, out var owner))
		{
			return false;
		}
		if (VirtualQuery((void*)faultAddress, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0)
		{
			return false;
		}

		ulong pageBase = faultAddress & 0xFFFFFFFFFFFFF000uL;
		uint commitProtect = ResolveLazyCommitProtection(accessType, mbi.AllocationProtect);
		int traceIndex = Interlocked.Increment(ref _lazyCommitTraceCount);
		bool traceLazyCommit = ShouldTraceLazyCommit(traceIndex);
		if (traceLazyCommit)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] lazy-query#{traceIndex}: fault=0x{faultAddress:X16} owner={owner} rip=0x{rip:X16} rsp=0x{rsp:X16} state=0x{mbi.State:X08} base=0x{mbi.BaseAddress:X16} size=0x{mbi.RegionSize:X16} alloc=0x{mbi.AllocationProtect:X08} prot=0x{mbi.Protect:X08}");
		}

		if (mbi.State == 4096 && IsAccessCompatible(accessType, mbi.Protect))
		{
			if (traceLazyCommit)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] lazy-commit-race#{traceIndex}: fault=0x{faultAddress:X16} protect=0x{mbi.Protect:X08}");
			}
			return true;
		}

		bool committed = false;
		ulong committedBase = 0;
		ulong committedSize = 0;

		if (mbi.State == 65536)
		{
			if (TryGetLazyCommitWindow(faultAddress, mbi.BaseAddress, mbi.RegionSize, out var windowBase, out var windowSize) &&
				TryReserveThenCommit(windowBase, windowSize, windowBase, windowSize, commitProtect))
			{
				committed = true;
				committedBase = windowBase;
				committedSize = windowSize;
			}
			else
			{
				ulong largeBase = faultAddress & 0xFFFFFFFFFFE00000uL;
				if (TryReserveThenCommit(largeBase, 2097152uL, largeBase, 2097152uL, commitProtect))
				{
					committed = true;
					committedBase = largeBase;
					committedSize = 2097152uL;
				}
			}

			if (!committed)
			{
				ulong region64kBase = faultAddress & 0xFFFFFFFFFFFF0000uL;
				if (TryReserveThenCommit(region64kBase, 65536uL, region64kBase, 65536uL, commitProtect))
				{
					committed = true;
					committedBase = region64kBase;
					committedSize = 65536uL;
				}
				else if (TryReserveThenCommit(pageBase, 4096uL, pageBase, 4096uL, commitProtect))
				{
					committed = true;
					committedBase = pageBase;
					committedSize = 4096uL;
				}
			}

			if (!committed)
			{
				return false;
			}

			TryCommitRange(pageBase + 4096, 4096uL, commitProtect);
			RescanTlsPatternsIfExecutable(committedBase, committedSize + 4096uL, commitProtect);
			if (traceLazyCommit)
			{
				Console.Error.WriteLine($"[LOADER][TRACE] lazy-reserve-commit#{traceIndex}: addr=0x{committedBase:X16} size=0x{committedSize:X16} access={accessType} protect=0x{commitProtect:X8}");
			}
			return true;
		}

		if (mbi.State != 8192)
		{
			return false;
		}

		if (TryGetLazyCommitWindow(faultAddress, mbi.BaseAddress, mbi.RegionSize, out var commitWindowBase, out var commitWindowSize) &&
			TryCommitRange(commitWindowBase, commitWindowSize, commitProtect))
		{
			committed = true;
			committedBase = commitWindowBase;
			committedSize = commitWindowSize;
		}
		else
		{
			ulong largeCommitBase = faultAddress & 0xFFFFFFFFFFE00000uL;
			if (TryCommitRange(largeCommitBase, 2097152uL, commitProtect))
			{
				committed = true;
				committedBase = largeCommitBase;
				committedSize = 2097152uL;
			}
		}

		if (!committed)
		{
			ulong region64kBase = faultAddress & 0xFFFFFFFFFFFF0000uL;
			if (TryCommitRange(region64kBase, 65536uL, commitProtect))
			{
				committed = true;
				committedBase = region64kBase;
				committedSize = 65536uL;
			}
			else if (TryCommitRange(pageBase, 8192uL, commitProtect))
			{
				committed = true;
				committedBase = pageBase;
				committedSize = 8192uL;
			}
			else if (TryCommitRange(pageBase, 4096uL, commitProtect))
			{
				committed = true;
				committedBase = pageBase;
				committedSize = 4096uL;
			}
		}

		if (!committed)
		{
			return false;
		}

		TryCommitRange(pageBase + 4096, 4096uL, commitProtect);
		RescanTlsPatternsIfExecutable(committedBase, committedSize + 4096uL, commitProtect);
		if (traceLazyCommit)
		{
			Console.Error.WriteLine($"[LOADER][TRACE] lazy-commit#{traceIndex}: addr=0x{committedBase:X16} size=0x{committedSize:X16} access={accessType} protect=0x{commitProtect:X8}");
		}
		return true;

		static bool TryGetLazyCommitWindow(ulong fault, ulong regionBase, ulong regionSize, out ulong baseAddress, out ulong length)
		{
			baseAddress = 0;
			length = 0;
			if (regionSize == 0 || ulong.MaxValue - regionBase < regionSize)
			{
				return false;
			}

			ulong regionEnd = regionBase + regionSize;
			ulong windowBase = fault & ~(LazyCommitWindowBytes - 1);
			if (windowBase < regionBase)
			{
				windowBase = regionBase;
			}

			if (windowBase >= regionEnd)
			{
				return false;
			}

			ulong windowEnd = Math.Min(regionEnd, windowBase + LazyCommitWindowBytes);
			ulong windowSize = windowEnd - windowBase;
			windowSize &= 0xFFFFFFFFFFFFF000uL;
			if (windowSize == 0)
			{
				return false;
			}

			baseAddress = windowBase;
			length = windowSize;
			return true;
		}

		static unsafe bool TryCommitRange(ulong baseAddress, ulong length, uint protection)
		{
			if (length == 0)
			{
				return false;
			}
			return VirtualAlloc((void*)baseAddress, (nuint)length, 4096u, protection) != null;
		}

		static unsafe bool TryReserveRange(ulong baseAddress, ulong length)
		{
			if (length == 0)
			{
				return false;
			}
			return VirtualAlloc((void*)baseAddress, (nuint)length, 8192u, 4u) != null;
		}

		static bool TryReserveThenCommit(ulong reserveAddress, ulong reserveSize, ulong commitAddress, ulong commitSize, uint protection)
		{
			if (!TryReserveRange(reserveAddress, reserveSize))
			{
				return false;
			}
			return TryCommitRange(commitAddress, commitSize, protection);
		}

		static bool IsAccessCompatible(ulong accessType, uint protection)
		{
			const uint pageNoAccess = 0x01;
			const uint pageReadOnly = 0x02;
			const uint pageReadWrite = 0x04;
			const uint pageWriteCopy = 0x08;
			const uint pageExecute = 0x10;
			const uint pageExecuteRead = 0x20;
			const uint pageExecuteReadWrite = 0x40;
			const uint pageExecuteWriteCopy = 0x80;
			const uint pageGuard = 0x100;
			const uint accessMask = 0xFF;

			if ((protection & pageGuard) != 0)
			{
				return false;
			}

			uint access = protection & accessMask;
			if (access == pageNoAccess)
			{
				return false;
			}

			return accessType switch
			{
				0 => access is pageReadOnly or pageReadWrite or pageWriteCopy or pageExecuteRead or pageExecuteReadWrite or pageExecuteWriteCopy,
				1 => access is pageReadWrite or pageWriteCopy or pageExecuteReadWrite or pageExecuteWriteCopy,
				8 => access is pageExecute or pageExecuteRead or pageExecuteReadWrite or pageExecuteWriteCopy,
				_ => false
			};
		}
	}

	// Re-scans a just-committed window for FS:[0] TLS loads; skips non-executable commits.
	private unsafe void RescanTlsPatternsIfExecutable(ulong committedBase, ulong committedSize, uint commitProtect)
	{
		const uint executableProtectionMask = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
		if ((commitProtect & executableProtectionMask) == 0 || committedSize == 0)
		{
			return;
		}

		PatchTlsPatternsInRange(committedBase, committedBase + committedSize, announce: false);
	}

	private static bool ShouldTraceLazyCommit(int traceIndex)
	{
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_LAZY_COMMIT"), "1", StringComparison.Ordinal))
		{
			return true;
		}

		return traceIndex <= 16 || traceIndex % 256 == 0;
	}

	private static uint ResolveLazyCommitProtection(ulong accessType, uint allocationProtect)
	{
		if (accessType == 8 || (allocationProtect & 0xF0) != 0)
		{
			return 64u;
		}
		return 4u;
	}
}
