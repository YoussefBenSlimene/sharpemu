<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# CPU Execution & Fault Handling

SharpEmu does **not** have an interpreter or a managed JIT. The single supported
engine is `CpuExecutionEngine.NativeOnly` (`src/SharpEmu.Core/Cpu/CpuExecutionEngine.cs`)
— guest x86-64 runs directly on the host CPU. Any other engine value fails
with `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED`. This page explains how that works,
how imports are diverted to HLE, and how the exception handler recovers from
the many faults native execution can produce.

The main files (all in `src/SharpEmu.Core/Cpu/Native/`):

- `DirectExecutionBackend.cs` — the backend (a large `sealed unsafe partial
  class` spread across many partial files).
- `DirectExecutionBackend.Imports.cs` — the managed HLE import gateway.
- `DirectExecutionBackend.NativeWorker.cs` — pooled raw-OS-thread executors.
- `DirectExecutionBackend.Exceptions.cs` — Windows VEH chain.
- `DirectExecutionBackend.IllegalInstruction.cs` — BMI `#UD` emulation.
- `DirectExecutionBackend.Amd64Compat.cs` — SSE4a / MONITORX / MWAITX.
- `DirectExecutionBackend.PosixSignals.cs` — POSIX signal bridge.
- `DirectExecutionBackend.GuestSampler.cs` — sampling profiler.
- `DirectExecutionBackend.Diagnostics.cs` — perf counters, recent-import ring.

## The execution entry stub

`DirectExecutionBackend.ExecuteEntry` (`DirectExecutionBackend.cs:6224`)
emits a 512-byte entry stub that:

1. Saves host non-volatile XMM registers.
2. Stores the host RSP into a TLS slot (so the fault handler can switch back
   to the host stack when a fault happens on the guest stack).
3. Loads guest `RSP`/`RBP`/`RDI`/`RSI`/`RDX`/`RCX`, sets `RAX=entryPoint`,
   and `jmp rax`.
4. On return, restores the host RSP and returns.

The stub runs via `RunGuestEntryStub` — a pooled native worker thread
(`DirectExecutionBackend.NativeWorker.cs`), or an inline `calli` fallback when
`SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1`.

### Why native worker threads?

Each `NativeGuestExecutor` emits a native run loop: `WaitForSingleObject(work)
→ RunPrologue([UnmanagedCallersOnly]) → call guest stub → RunEpilogue →
SetEvent(done)`. This avoids the CLR's "UnmanagedCallersOnly from managed
code" FailFast that occurred when guest stubs sat above CLR-managed frames on
a CLR-created thread. The loop is pure emitted x86 (no CLR unwind info). It
includes TBB-storm hardening (Astro's `tbb_thead` bursts): a
`_nativeWorkerRunLimiter` semaphore, refuse-and-yield on exhaustion, and
`TerminateThread`+respawn of parked aborted workers.

The number of concurrent workers defaults to 2, capped 1–64 via
`SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT`.

## Import trampolines

`SetupImportStubs` (`DirectExecutionBackend.cs:1251`) resolves each NID into
one of three targets (also described in
[SysABI / HLE](hle-and-sysabi.md#how-a-guest-call-reaches-dispatch)):

1. **LLE direct bridge** — patch the import stub to
   `movabs r11, <target>; jmp r11`. Preferred for `libc` leaf functions and
   any `ExportedFunction` marked `PreferLle`. Gated by the
   `SHARPEMU_LLE_LIBC_*` family. **Kernel libraries are always HLE.**
2. **Native intrinsic** — `TryCreateNativeImportIntrinsic` hand-emits x86 for
   hot leaf functions (`rdtsc`, `QueryPerformanceCounter`, `usleep`,
   `strlen`/`wcslen`, `memcmp`/`strcmp`/`strcasecmp`/`strncmp`/`strcpy`/
   `strncpy`/`memcpy`/`memset`/`memmove`/`strchr`/`strrchr`/`memchr`).
3. **Managed HLE gateway** — `CreateImportHandlerTrampoline` saves all
   volatile guest state (RAX/AL, R10/R11, MXCSR, FPU control, XMM0–7), reads
   the host RSP from a TLS slot, and calls `ImportDispatchGatewayManaged`
   (`DirectExecutionBackend.Imports.cs`), a reverse-P/Invoke. The gateway
   loads the guest GPRs into `CpuContext`, dispatches (bootstrap bridge /
   `kernel_dynlib_dlsym` / il2cpp / the registered HLE export), stores the
   vector return (XMM0/1) back into the arg pack, and returns.

The gateway also handles: leaf fast-paths (`TryDispatchHotMemoryLeaf` for
memcpy/memmove, `TryDispatchLeafImport`), the import-loop guard
(`ShouldForceGuestExitOnImportLoop` with a 2048-entry signature history),
guest-thread block/exit/context-transfer continuations, and vector-return
marshalling.

On POSIX, managed callbacks are SysV-compiled, so `ResolveWin64CallbackPtr`
wraps them in `PosixHostStubs.CreateWin64ToSysVThunk` (saves rdi/rsi,
shuffles rcx/rdx/r8/r9 → rdi/rsi/rdx/rcx). The emitted call sites always use
the Win64 ABI.

## TLS load patching

Guest code reads its TLS base via `mov reg, fs:[0]` / `mov reg, gs:[0]`.
Because the guest FS/GS base must be a real host TLS slot (not a guest-only
value), `PatchTlsPatterns` (`DirectExecutionBackend.cs:3149`) scans up to
128 MiB of executable guest memory (from the entry-point allocation base
**and** the standard PS5/PS4 image base) for those TLS-load patterns and
TLS-immediate stores, and rewrites them to call a TLS handler stub
(`CreateTlsHandler`) which preserves all registers + flags and calls
`TlsGetValue(_guestTlsBaseTlsIndex)`. Lazy-committed executable windows are
re-scanned after a fault (`RescanTlsPatternsIfExecutable`).

> If you add a new TLS-reading code path, ensure the pattern is matched here
> or it will fault with a "FS segment prefix - TLS access not patched!"
> diagnostic.

## The exception handler

Native execution faults frequently — and intentionally. The handler recovers
from all of them.

### Windows VEH

`DirectExecutionBackend.Exceptions.cs` installs three layers via
`SetupExceptionHandler`: a raw pre-filter thunk, a managed `VectoredHandler`,
and an `UnhandledExceptionFilter` (all emitted through
`CreateExceptionHandlerTrampoline`). `WindowsFaultHandling`
(`Cpu/Native/Windows/WindowsFaultHandling.cs`) emits the native pre-filter
thunk: NTSTATUS pre-filtering that passes CLR-managed exceptions
(`0xE0434352`), MSVC C++ exceptions (`0xE06D7363`), FastFail (`0xC0000409`),
and stack-overflow (`0xC00000FD`) straight to `EXCEPTION_CONTINUE_SEARCH`
without entering managed code (entering managed code from a cooperative-GC
thread FailFasts). FastFail gets a native stderr breadcrumb. The thunk reads
`gs:[8]`/`gs:[0x10]` (TEB stack limits) to decide whether the fault is on
the guest stack (switch to the saved host RSP via `TlsGetValue` before the
managed callback) or a host stack (call directly).

The managed `VectoredHandler` chain handles, in order:
- guest `int41` trap recovery (`SHARPEMU_IGNORE_INT41`, default on),
- `GuestImageWriteTracker` write fault,
- auxiliary TBB execute-fault recovery (`TryRecoverAuxiliaryThreadExecuteFault`),
- lazy-committed page demand-paging (`TryHandleLazyCommittedPage`),
- guest allocator empty-node recovery (Demon's Souls),
- **`#UD` recovery** → `TryRecoverIllegalInstruction` (BMI) and
  `TryRecoverAmdCompatInstruction` (SSE4a / MONITORX / MWAITX),
- benign debug exceptions,
- MSVC C++ exceptions (pass-through).

### POSIX signals

`DirectExecutionBackend.PosixSignals.cs` installs `sigaction` handlers for
`SIGSEGV`/`SIGBUS`/`SIGILL`/`SIGTRAP`/`SIGABRT` that rebuild a Win64
`EXCEPTION_POINTERS` view from `ucontext`/`mcontext`, run the same
`VectoredHandler`/`TryRecoverUnresolvedSentinel` recovery chain, and write
register changes back through `sigreturn`. It bridges XMM state on Linux
(fpstate FXSAVE image); **Darwin XMM bridging is not yet implemented**
(`_posixXmmContextBridged` stays false), so SSE4a recovery declines there.

> Under Rosetta 2 a cold (never-JITted) handler is silently never invoked, so
> `WarmUpPosixSignalPath` runs the handler once with fabricated inputs before
> install.

## `#UD` software emulation

The PS5's Zen 2 cores implement BMI1/BMI2/ABM and SSE4a; Intel hosts and
Rosetta 2 do not, so these are emulated in software.

### BMI1/BMI2/ABM (`IllegalInstruction`)

`DirectExecutionBackend.IllegalInstruction.TryRecoverIllegalInstruction`
decodes the faulting instruction (window shrinks 15→11→8→4→2 bytes to survive
page-boundary reads), evaluates it via `BmiInstructionEmulator`
(`Cpu/Emulation/BmiInstructionEmulator.cs` — `Andn, Blsi, Blsmsk, Blsr, Bextr,
Bzhi, Tzcnt, Lzcnt, Rorx, Sarx, Shlx, Shrx, Pdep, Pext`), writes the result +
EFLAGS into the CONTEXT, and steps RIP. EFLAGS are updated per the vendor
manual; "undefined" flags are left untouched for determinism. Iced `Register`
enums are mapped to Win64 CONTEXT GPR offsets (`TryGetGprSlot`/
`TryGetGpr64Offset`).

### SSE4a (`Amd64Compat`)

`DirectExecutionBackend.Amd64Compat.TryRecoverSse4aExtractInsert` catches any
immediate-form `EXTRQ`/`INSERTQ` and uses `Sse4aBitFieldEmulator`
(`Cpu/Emulation/Sse4aBitFieldEmulator.cs`) to compute the result into the XMM
register in the CONTEXT, then steps RIP. It also handles `MONITORX`/`MWAITX`
(no-op + `Thread.Yield`). Ported from Kyty's `X64InstructionEmulator`.

There's also a **load-time patch**: `Sse4aExtrqBlendPatch` (a `static` class)
recognises one specific 12-byte `EXTRQ+VPBLENDD` idiom Sony's compiler emits
and rewrites it at load time into `PEXTRB+PINSRD` (SSE4.1, which Intel/Rosetta 2
support). This is the fast path; the fault-time fallback handles the rest.

## Lazy commit / demand paging

Large non-executable reserves (≥1 GiB, above the 4 GiB full-commit limit) may
be reserved-only and primed with a 64 MiB leading chunk. On a page fault the
VEH/signal handler calls `TryHandleLazyCommittedPage` which reserves+commits
a 32 MiB window around the fault address (falling back to 2 MiB / 64 KiB / 8
KiB / 4 KiB). This is the path that supports titles with very large virtual
reservations (e.g. "Poppy").

## Unresolved-sentinel recovery

ELF unresolved weak/data symbols are sometimes written as the sentinels
`0xFFFE`, `0xFFFFFFFE`, or `0xFFFFFFFFFFFFFFFE`. If guest code calls or loads
through one, the raw VEH/signal handler (`TryRecoverUnresolvedSentinel`) walks
the stack to find a plausible return address and resumes there, masking the
unresolved import. `IsUnresolvedSentinel` is the shared predicate (in both
`SharpEmuRuntime` and `DirectExecutionBackend.Diagnostics`).

## The guest-thread scheduler

`DirectExecutionBackend` implements `IGuestThreadScheduler` (the contract is
in `src/SharpEmu.HLE/GuestThreadExecution.cs`). It maintains
`_guestThreads`/`_externalGuestThreads`/`_readyGuestThreads`, a stall
watchdog, blocked-thread continuations, and pending guest exceptions delivered
at safe points. `GuestContinuationRunner`/`GuestExecutionRunner` are pooled
background threads. `GuestThreadExecution` is the ambient (thread-static)
scheduler/handle.

HLE exports that need to block a guest thread (e.g. waiting on a mutex) use
`GuestThreadExecution.RequestCurrentThreadBlock(...)`; the gateway detects the
pending block and parks the guest thread until the waiter's `TryWake()` runs.
`GuestThreadAbandoned` fires on unclean teardown (e.g. TBB `worker_abort`) so
libs can abandon mutexes.

## Diagnostics & profiling

- `DirectExecutionBackend.GuestSampler.cs` — sampling profiler
  (`SHARPEMU_PROFILE_GUEST_RIP=1`): a background thread samples each guest
  thread's host RIP, attributes samples to guest addresses (`app+…`,
  `stub:NID`, `host`) and wait labels, and reports top RIPs/pages/waits.
- `DirectExecutionBackend.Diagnostics.cs` — perf counters for HLE dispatch
  (`SHARPEMU_PERF_HLE`, `SHARPEMU_PERF_HLE_NODICT`), the recent-import ring,
  suspicious unresolved-sentinel pointer scans, return-RIP probes.
- `DirectExecutionBackend.Imports.cs`'s stall evidence (an import-loop guard)
  feeds the debug server's `Stall` stop reason — see
  [`debugger-server.md`](../debugger-server.md).

## The mitigated-child relaunch (Windows only)

The host process re-launches itself with CET/CFG shadow-stack mitigations
disabled (`PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF`,
`…2_CET_USER_SHADOW_STACKS_ALWAYS_OFF`,
`…2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF`,
`…2_XTENDED_CONTROL_FLOW_GUARD_ALWAYS_OFF`), because CET's `set_context` IP
validation interferes with the VEH CONTEXT rewriting.
`SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1` opts out. The child is set
`SHARPEMU_MITIGATED_CHILD=1` and the `--sharpemu-mitigated-child` arg is
trusted only when that env var is set. See
[`src/SharpEmu.CLI/Program.cs`](../projects/cli.md).

## Cross-platform host stubs

`PosixHostStubs.cs` (`internal static unsafe class`) provides Win64-ABI-shaped
native helpers for POSIX: `TlsGetValue` (macOS: `mov rax, gs:[rcx*8]`; Linux:
wraps `pthread_getspecific`), `QueryPerformanceCounter` (`rdtsc`),
`SwitchToThread` (`sched_yield`), `Sleep` (`usleep`), worker events
(macOS `dispatch_semaphore_*`, Linux `sem_*`), and `CreateWin64ToSysVThunk`.
These are emitted as raw x86-64 at runtime into an RWX page, so the emission
code in Core stays identical across platforms.

## Next

- [SysABI / HLE export system](hle-and-sysabi.md) — what the gateway dispatches
  *to*.
- [SharpEmu.Core guide](../projects/core.md) — the file map for everything
  above.
