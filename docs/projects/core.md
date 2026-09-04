<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.Core

The reusable emulation engine. Owns the entire guest lifecycle: parsing
SELF/ELF executables, mapping guest memory, executing guest x86-64
instructions directly on the host CPU, and dispatching HLE calls for PS5 OS
imports. The biggest and most central project.

`src/SharpEmu.Core/SharpEmu.Core.csproj` references `SharpEmu.HLE`,
`SharpEmu.Libs`, and `SharpEmu.Logging`. Its only NuGet dependency is
**Iced** (the x86 disassembler/encoder). `AllowUnsafeBlocks=true` is required
because the CPU backend emits and executes raw machine code. `InternalsVisibleTo`
includes `SharpEmu.Libs.Tests`.

> Read [Architecture overview](../architecture/overview.md) first for where
> Core fits. This page is a file map.

## Root files

| File | Purpose |
| --- | --- |
| `IFileSystem.cs` | Minimal file-system abstraction (`Exists`, `TryReadAllBytes`) so the loader can read `param.json` without a hard `System.IO` dependency. Namespace `SharpEmu.Core`. |
| `PhysicalFileSystem.cs` | Default `IFileSystem` over real disk. |

## `Runtime/` (namespace `SharpEmu.Core.Runtime`)

| File | Purpose |
| --- | --- |
| `ISharpEmuRuntime.cs` | Public contract: `LoadImage`, `Run → OrbisGen2Result`, `DispatchHleCall(nid, context)`, diagnostic properties (`LastExecutionDiagnostics`, `LastExecutionTrace`, `LastSessionSummary`, `LastBasicBlockTrace`, `LastMilestoneLog`), `IDisposable`. |
| `SharpEmuRuntimeOptions.cs` | `readonly struct`: `CpuEngine`, `StrictDynlibResolution`, `ImportTraceLimit`, `ICpuDebugHook? DebugHook`. |
| `SharpEmuRuntime.cs` | The facade (`sealed class`). Static `CreateDefault(options)` wires the engine. `Run(ebootPath)` is the boot sequence — see [Runtime & boot flow](../architecture/runtime-flow.md). |

## `Loader/` (namespace `SharpEmu.Core.Loader`)

Parses PS4/PS5 executable formats and performs full dynamic linking.

| File | Purpose |
| --- | --- |
| `ElfHeader.cs` | `[StructLayout]` blittable 64-byte ELF64 header. `AbiVersion==2` ⇒ Gen5/PS5. |
| `ProgramHeader.cs` | ELF64 program header + `ProgramHeaderType` (`Load`, `Dynamic`, `Tls`, `GnuEhFrame`, `SceRela`, `SceProcParam`, `SceDynLibData`, `SceRelro`) and `[Flags] ProgramHeaderFlags` (`Execute`/`Write`/`Read`). |
| `ImportedSymbolRelocation.cs` | `readonly record struct(ulong TargetAddress, long Addend, string Nid, bool IsData)` — output of the relocation pass for NID-keyed imports; consumed by `SharpEmuRuntime.RebindImportedDataSymbols`. |
| `ParamLoader.cs` | `Ps5ParamJsonReader.TryReadPs5Param` parses `sce_sys/param.json` (title id, versions, localized title). |
| `ISelfLoader.cs` | `interface ISelfLoader` with `Load`/`LoadAdditional` overloads. `LoadAdditional` keeps existing memory and skips `param.json`. |
| `SelfImage.cs` | `sealed class` result of loading: `ElfHeader`, `ProgramHeaders`, `MappedRegions`, `ImportStubs` (address → NID), `RuntimeSymbols` (name/NID → address), `ImportedRelocations`, `PreInitializerFunctions`, `InitializerFunctions`, `EntryPoint`, `ProcParamAddress`, `Title`/`TitleId`/`Version`, TLS info. |
| `SelfLoader.cs` | The ~2900-line loader. Key constants: image bases, import-stub region (`0x700000000000`), Sony `DT_SCE_*` dynamic tags. See [Runtime & boot flow](../architecture/runtime-flow.md#the-loader-selfloaderload). |

## `Memory/` (namespace `SharpEmu.Core.Memory`)

The identity-mapped guest address space. See
[Memory & address space](../architecture/memory-and-address-space.md).

| File | Purpose |
| --- | --- |
| `IVirtualMemory.cs` | `interface IVirtualMemory : ICpuMemory` (extends `SharpEmu.HLE.ICpuMemory`). Adds `Clear`, `Map`, `SnapshotRegions`. |
| `VirtualMemoryRegion.cs` | `readonly struct` describing one mapped region. |
| `VirtualMemory.cs` | Pure-managed backing store (`byte[]` per region). For tests. |
| `PhysicalVirtualMemory.cs` | Production backend (`sealed unsafe class`, ~2000 lines). `IGuestMemoryAllocator`, `IGuestAddressSpace`, `IDisposable`. Guest allocator arena, page-protection bookkeeping, fixed-address allocation, lazy commit, protection-changing writes, identity pointer. |

## `Cpu/` (namespace `SharpEmu.Core.Cpu` and sub-namespaces)

### Top-level

| File | Purpose |
| --- | --- |
| `CpuExecutionEngine.cs` | `enum { NativeOnly = 0 }` — only one mode. |
| `CpuExecutionOptions.cs` | `readonly struct`: `EnableDisasmDiagnostics`, `CpuEngine`, `StrictDynlibResolution`, `ImportTraceLimit`, `ICpuDebugHook? DebugHook`. |
| `ICpuDispatcher.cs` | Contract: `LastTrapInfo`, `LastMemoryFaultInfo`, `LastControlTransferInfo`, `LastNotImplementedInfo`, trace strings, `LastSessionSummary`, `DispatchEntry`, `DispatchModuleInitializer`. |
| `CpuDispatcher.cs` | `sealed class` (~733 lines). Owns frame-setup: stack, TLS, registers, return-to-host stub, dynlib fallback stub, entry-params. See [Runtime & boot flow §5](../architecture/runtime-flow.md#5-cpu-dispatch-cpudispatcherdispatchentry). |
| `CpuTrapInfo.cs` ... `CpuSessionSummary.cs` | Diagnostic/result DTOs. `CpuControlTransferInfo` carries the last call/jump/ret. `CpuExitReason`: `Exited, SentinelReturn, ReturnedToHost, Halted, BudgetExceeded, CpuTrap, UnhandledException, UnhandledSyscall, NativeBackendUnavailable`. |
| `RuntimeStubNids.cs` | `internal static` constants for synthetic NIDs (`__internal_bootstrap_bridge`, `__internal_kernel_dynlib_dlsym`). |
| `ITrackedCpuMemory.cs` / `TrackedCpuMemory.cs` | Wraps an `ICpuMemory` to capture the last `CpuMemoryAccessFailure` on a failed read/write. |

### `Cpu/Disasm/` (namespace `SharpEmu.Core.Cpu.Disasm`)

| File | Purpose |
| --- | --- |
| `DecodedInst.cs` | `readonly struct` (Rip, Length, Text, Mnemonic, Iced `FlowControl`, `NearBranchTarget?`, `MemoryAddress?`, `byte[] Bytes`). |
| `IcedDecoder.cs` | `static` helper wrapping the Iced library. `TryDecode`, `TryReadGuestBytes` (byte-at-a-time so unmapped tails don't fail the whole read), `FormatBytes`. Used by diagnostics and VEH disasm dumpers. |

### `Cpu/Debugging/` (namespace `SharpEmu.Core.Cpu.Debugging`)

The seam between Core and the Debugger. Core stays debugger-agnostic.

| File | Purpose |
| --- | --- |
| `ICpuDebugHook.cs` | The seam: `OnFrameEnter(ICpuDebugFrame)`, `OnFrameExit(...)`, `OnStall(...)`. Implementations must be thread-safe. Coarse-grained (frame boundaries). |
| `ICpuDebugFrame.cs` | Live view of guest state at a dispatch boundary: `Kind`, `Generation`, `EntryPoint`, `Label`, `Memory`, `GetRegister`/`SetRegister`, `Rip`/`Rflags`, `FsBase`/`GsBase`, `GetXmm`, `ImportStubs`. |
| `CpuDebugFrameKind.cs` | `enum { ProcessEntry, ModuleInitializer }`. |
| `CpuStallInfo.cs` | `enum CpuStallKind { ImportLoop }` + `readonly struct` with NID, IP, dispatch index, args, library/function names. |
| `CpuContextDebugFrame.cs` | `internal sealed` adapter from a live `CpuContext` to `ICpuDebugFrame`. |

### `Cpu/Emulation/` (namespace `SharpEmu.Core.Cpu.Emulation`)

Pure-software implementations of instructions the host CPU may not have.

| File | Purpose |
| --- | --- |
| `GprOperandSize.cs` | `enum { Bits32=32, Bits64=64 }` (value doubles as the all-zero count result for LZCNT/TZCNT). |
| `BmiInstructionEmulator.cs` | BMI1/BMI2/ABM GPR instructions (Andn, Blsi, Blsmsk, Blsr, Bextr, Bzhi, Tzcnt, Lzcnt, Rorx, Sarx, Shlx, Shrx, Pdep, Pext). |
| `Sse4aBitFieldEmulator.cs` | AMD SSE4a EXTRQ/INSERTQ bit-field math. Ported from Kyty. |

### `Cpu/Native/` (namespace `SharpEmu.Core.Cpu.Native`)

The direct-execution backend. See [CPU execution](../architecture/cpu-execution.md).

| File | Purpose |
| --- | --- |
| `INativeCpuBackend.cs` | `interface`: `BackendName`, `LastError`, `TryExecute(...)`. |
| `DirectExecutionBackend.cs` + partials | `sealed unsafe partial class` (the heart of the emulator). Partials: `Imports`, `NativeWorker`, `Exceptions`, `IllegalInstruction`, `Amd64Compat`, `PosixSignals`, `GuestSampler`, `Diagnostics`. Implements `INativeCpuBackend`, `IGuestThreadScheduler`, `IDisposable`. |
| `Sse4aExtrqBlendPatch.cs` | `static` — load-time rewrite of a specific EXTRQ+VPBLENDD idiom to PEXTRB+PINSRD. |
| `JitStubs.cs` | `static unsafe` helpers for emitting small x86 stubs (`JmpWithIndex`, `Call9`, `JmpRax`, `SafeCall`, `TlsAccessPattern`, `FindTlsAccessPatterns`). |
| `StubManager.cs` | `sealed unsafe class` — 1 MiB executable arena for PLT-style stubs. (Largely superseded by partial-class methods on `DirectExecutionBackend`.) |
| `CpuPatcher.cs` | `sealed unsafe class` — currently a stub. Contains an `UnsafeCodeReader : Iced.Intel.CodeReader`. |
| `PosixHostStubs.cs` | `internal static unsafe class` — Win64-ABI-shaped native helpers for POSIX (TlsGetValue, QPC, SwitchToThread, Sleep, worker events, `CreateWin64ToSysVThunk`). |
| `NullHostFaultHandling.cs` | `internal sealed` no-op `IHostFaultHandling` for POSIX (fault bridge installed directly by the backend). |

#### `Cpu/Native/Windows/` (namespace `SharpEmu.Core.Cpu.Native.Windows`)

| File | Purpose |
| --- | --- |
| `WindowsFaultHandling.cs` | `internal sealed unsafe partial class` — emits the native VEH pre-filter thunk. Passes CLR/C++/FastFail/stack-overflow to `CONTINUE_SEARCH` without entering managed code; switches to host stack when fault is on the guest stack. |
| `WindowsFaultCodes.cs` | NTSTATUS constants (`AccessViolation=0xC0000005`, `IllegalInstruction=0xC000001D`, `FastFail=0xC0000409`, `ClrManagedException=0xE0434352`, ...). |
| `Win64ContextOffsets.cs` | Byte offsets into the Win64 `CONTEXT` (`Size=0x4D0`, `Mxcsr=52`, `Rax=120`, ..., `Rip=248`). |

## Public API surface

The contracts Core exposes to the rest of the solution:

- `SharpEmu.Core.Runtime.ISharpEmuRuntime`, `SharpEmuRuntime`,
  `SharpEmuRuntimeOptions`.
- `SharpEmu.Core.IFileSystem`, `PhysicalFileSystem`.
- `SharpEmu.Core.Loader.ISelfLoader`, `SelfLoader`, `SelfImage`, `ElfHeader`,
  `ProgramHeader`, `ImportedSymbolRelocation`, `ParamLoader`.
- `SharpEmu.Core.Memory.IVirtualMemory`, `VirtualMemory`,
  `PhysicalVirtualMemory`, `VirtualMemoryRegion`.
- `SharpEmu.Core.Cpu.ICpuDispatcher`, `CpuDispatcher`, and the options/result
  types.
- `SharpEmu.Core.Cpu.Native.INativeCpuBackend`, `DirectExecutionBackend`,
  `Sse4aExtrqBlendPatch`, `JitStubs`, `StubManager` (`public`); `CpuPatcher`
  (`public`); `PosixHostStubs`, `NullHostFaultHandling` (`internal`).
- `SharpEmu.Core.Cpu.Native.Windows.WindowsFaultHandling` (`internal sealed
  partial`); `WindowsFaultCodes`, `Win64ContextOffsets` (`internal static`).
- `SharpEmu.Core.Cpu.Debugging.ICpuDebugHook`, `ICpuDebugFrame`,
  `CpuDebugFrameKind`, `CpuStallInfo`/`CpuStallKind`.
- `SharpEmu.Core.Cpu.Disasm.IcedDecoder`, `DecodedInst`.
- `SharpEmu.Core.Cpu.Emulation.BmiInstructionEmulator`,
  `Sse4aBitFieldEmulator`, `GprOperandSize`.

## Native dependencies (transitive)

- **Iced** — x86 disassembler/encoder.
- **Silk.NET** — Vulkan/Windowing (referenced by GUI/Libs).
- **SDL3** — window/event/input host.
- **FFmpeg.AutoGen** — native FFmpeg bindings (loaded into the `plugins`
  folder; see the [CLI guide](cli.md)).

## Gotchas

- There is **no interpreter and no managed JIT**. `NativeOnly` is the only
  mode. Within native execution, **LLE is preferred** wherever a guest
  implementation exists; HLE is the fallback.
- The mitigated-child relaunch (Windows) is required because CET interferes
  with VEH CONTEXT rewriting. See [CPU execution §mitigated-child](../architecture/cpu-execution.md#the-mitigated-child-relaunch-windows-only).
- On macOS, concurrent GC is disabled to avoid Rosetta 2 stalls; the process
  must be `osx-x64` (run under Rosetta 2 on Apple Silicon).

## Related

- [Runtime & boot flow](../architecture/runtime-flow.md)
- [Memory & address space](../architecture/memory-and-address-space.md)
- [CPU execution & fault handling](../architecture/cpu-execution.md)
