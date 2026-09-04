<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.HLE

The bridge between guest PS5 code (native x86-64 running under the execution
backend in `SharpEmu.Core`) and the host operating system. It provides three
things:

1. **The SysABI / module / export system** — a registry that maps a PS5 *NID*
   to a managed `int`-returning delegate.
2. **Host platform abstractions** — the `IHost*` interfaces backed by per-OS
   implementations, so the same HLE code runs on Windows, Linux, and macOS.
3. **Guest-side helpers** — TLS layout, GPU-image CPU-write tracking,
   write-watch diagnostics, page-protection enums, and the data-symbol stubs
   the guest dynamic linker expects.

`src/SharpEmu.HLE/SharpEmu.HLE.csproj` depends only on `SharpEmu.Logging`
plus `ppy.SDL3-CS` (for `SdlHostAudio`). `AllowUnsafeBlocks=true`.
`InternalsVisibleTo` includes `SharpEmu.Core` and `SharpEmu.Libs.Tests`, so
Core can reach `internal` platform implementations and tests can assert on
`internal` helpers. The `SharpEmu.SourceGenerators` project is referenced
build-only (`ReferenceOutputAssembly=false`) to force build ordering for the
`GenerateAerolibBinaryTask`.

> Read [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
> for the full dispatch story. This page is a file map and the "how to add an
> export" reference.

## SysABI & exports (root files)

| File | Purpose |
| --- | --- |
| `SysAbiExportAttribute.cs` | Method-level `[SysAbiExport]` attribute (`LibraryName`, `Nid`, `ExportName`, `Target`, `PreferLle`). |
| `SysAbiSymbol.cs` | `readonly struct` (Nid, AliasName, ExportName, Target) — the catalog shape. |
| `SysAbiFunction.cs` | `public delegate int SysAbiFunction(CpuContext context);` — every handler is normalized to this shape. The `int` return is an `OrbisGen2Result`-compatible code written to guest `RAX`. |
| `ExportedFunction.cs` | Runtime registration record: `LibraryName`, `Nid`, `Name`, `Target`, `Function`, `PreferLle`. Constructed by the generated registry. |
| `Generation.cs` | `[Flags] enum Generation { None=0, Gen4=1, Gen5=2 }`. PS4 is `Gen4`, PS5 is `Gen5`. |
| `OrbisGen2Result.cs` | Synthetic kernel result codes prefixed `ORBIS_GEN2_`. |
| `IModuleManager.cs` / `ModuleManager.cs` | Dispatch contract + implementation. `RegisterExports`, `Freeze`, `TryGetFunction`, `TryGetExport`, `TryDispatch`. Uses `ConcurrentDictionary` with `StringComparer.Ordinal`. |
| `ISymbolCatalog.cs` | `TryGetByNid` / `TryGetByExportName`. Implemented by `Aerolib`. |

### ModuleManager

- `_dispatchTable` (NID → `Delegate`), `_exportTable` (NID →
  `ExportedFunction`), `_exportNameTable` (Name → `ExportedFunction`), all
  `ConcurrentDictionary`.
- `RegisterExports` (`ModuleManager.cs:19`) is gated by `_registrationGate`;
  duplicate NIDs are logged and skipped.
- `Freeze` (`ModuleManager.cs:51`) sets `_isFrozen` and runs
  `WarmHleTypeInitializers`.
- `WarmHleTypeInitializers` (`ModuleManager.cs:63`) runs every HLE type's
  `.cctor` and force-JITs every method on a host thread **before** any guest
  thread can reach them. (A `.cctor` firing on a hijacked guest stack would
  FailFast the CLR.)
- `TryDispatch` (`ModuleManager.cs:271`): NID lookup → generation filter →
  `ClearRaxWriteFlag` → invoke; if the handler did not write RAX, the
  dispatcher writes the `int` return itself.

## CPU state (root files)

| File | Purpose |
| --- | --- |
| `CpuRegister.cs` | `enum CpuRegister : int { Rax=0 … R15=15 }` — indexes a `ulong[16]` register file. |
| `CpuContext.cs` | `sealed class CpuContext(ICpuMemory, Generation)`. The HLE's model of a guest thread's CPU state at the moment an export is dispatched. Holds `_registers[16]`, `_xmmRegisters[32]` (16 XMM × 2 qwords), `_ymmUpperRegisters[32]`. See below. |

### `CpuContext` API

- Indexer `this[CpuRegister]` (`CpuContext.cs:44`); writing `Rax` sets
  `_raxWritten` (`:50-53`). `ClearRaxWriteFlag`/`WasRaxWritten` (`:57-62`).
- Vector: `Get/SetXmmRegister`, `Get/SetYmmUpper`, `Get/SetYmmRegister`,
  `ClearYmmUpper`, `ClearAllYmmUpper` (`:64-120`).
- Typed memory accessors over `ctx.Memory`: `TryReadByte/UInt16/Int32/UInt32/UInt64`,
  `TryWriteUInt16/Int32/UInt32/Int64/UInt64` (`:144-247`).
  `TryReadNullTerminatedUtf8(address, capacity, out string)` (`:249`)
  bulk-reads in 128-byte chunks with a per-byte fallback.
- Stack: `PushUInt64`/`PopUInt64` via `Rsp` (`:316-334`).
- Returns: `SetReturn(int result, Type?)` (`:336`); `SetReturn(OrbisGen2Result)`
  (`:349`).

## Memory contracts (root files)

| File | Purpose |
| --- | --- |
| `ICpuMemory.cs` | The base memory contract the HLE uses: `TryRead`/`TryWrite`/`TryCopy`. |
| `ICpuMemoryWrapper.cs` | Unwraps decorators to the real implementation without reflection (`Inner`). |
| `IGuestMemoryAllocator.cs` | `AllocateAt`, `TryAllocateAtExact`, `TryBackFixedRange`, `TryAllocateAtOrAbove`, `TryFreeGuestMemory`. |
| `IGuestAddressSpace.cs` | `TryProtect(address, size, GuestPageProtection)`. |

## Guest-side helpers (root)

| File | Purpose |
| --- | --- |
| `GuestWriteWatch.cs` | Diagnostic-only write watcher, active only with `SHARPEMU_WATCH_*`. See [`guest-write-watch.md`](../guest-write-watch.md). |
| `GuestTlsTemplate.cs` | Process-wide registry of ELF `PT_TLS` templates and per-thread DTVs. AMD64 **TLS Variant II**. `StartupStaticTlsReservation = 0x20000`. `RegisterModule`, `SeedThreadBlock`, `ResolveAddress` (`__tls_get_addr`). |
| `GuestThreadExecution.cs` | Continuation/blocking state machine for guest threads. `[ThreadStatic]`. Records: `GuestThreadStartRequest`, `GuestThreadSnapshot`, `GuestImportCallFrame`, `GuestCpuContinuation`. Interfaces: `IGuestThreadBlockWaiter`, `IGuestThreadScheduler`. Static API: `EnterGuestThread`, `RequestCurrentThreadBlock`, `EnterImportCallFrame`, etc. `GuestThreadAbandoned` event. |
| `GuestPageProtection.cs` | `[Flags] enum { None, Read, Write, Execute }` — the HLE-facing page-protection enum. |
| `GuestImageWriteTracker.cs` | Detects guest CPU writes into memory that backs a host GPU image. Enabled by `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1`. Signal-handler-safe (no allocation, no locks in the fault path). |
| `GuestCStringAttribute.cs` | Marks a `string` parameter of a `[SysAbiExport]` handler as a guest null-terminated UTF-8 pointer with a `MaxLength` byte cap. |
| `HleDataSymbols.cs` | Process-static data symbols the guest dynamic linker expects: stack-chk guard canary (`0xC0DEC0DECAFEBA00`), `progname`, `libc_need_flag`/`libc_internal_need_flag`. |

## `Host/` (namespace `SharpEmu.HLE.Host`)

The host platform abstractions. All interfaces live here.

| File | Purpose |
| --- | --- |
| `IHostPlatform.cs` | Aggregates `Memory`, `Threading`, `Symbols`, `Audio`, `Input`. |
| `HostPlatform.cs` | Static accessor: `HostPlatform.Current` lazily picks the backend (Windows/Posix; else `PlatformNotSupportedException` — native guest execution requires x86-64 on Windows/Linux/macOS). |
| `IHostMemory.cs` | `Allocate`/`Reserve`/`Commit`/`Free`/`Protect`/`ProtectRaw`/`Query`/`FlushInstructionCache`. |
| `IHostThreading.cs` | `AllocateTlsSlot`/`FreeTlsSlot`/`SetTlsValue`/`GetTlsValue`, `CurrentThreadId`, `TrySetCurrentThreadAffinity`, `RequestTimerResolution`, `CreateNativeThread`, `WaitForThreadExit`, `TryCaptureThreadRegisters`. |
| `IHostInput.cs` | Gamepad state snapshots, rumble, lightbar, keyboard. |
| `IHostAudioOutput.cs` / `IHostAudioStream.cs` / `IHostPcmAudioOutput.cs` | Audio output. `OpenStereoPcm16Stream`, `Submit` (may block to pace the guest), `QueuedMilliseconds`. `HostPcmFormat` = `Signed16`/`Float32`. |
| `IHostFaultHandling.cs` | Installation mechanics for the process-wide fault interception (`CreateHandlerThunk`, `AddFirstChanceHandler`, `SetUnhandledFilter`). Implementations live next to the execution backend in Core. |
| `IHostSymbolResolver.cs` / `HostRuntimeFunction.cs` | `GetAddress(HostRuntimeFunction)` — `HostRuntimeFunction` is an enum: `TlsGetValue`, `QueryPerformanceCounter`, `SwitchToThread`, `Sleep`, `WaitForSingleObject`, `SetEvent`, `ExitThread`. |
| `IHostWindowInputSource.cs` / `HostWindowInputSource` (static) / `WindowHostInput.cs` | The active host window contract + process-wide bridge. `WindowHostInput` forwards to `HostWindowInputSource.Current` (so SDL on the window thread owns device discovery). |
| Supporting types | `HostPageProtection`, `HostRegionInfo`, `HostRegionState`, `HostCapturedRegisters`, `HostGamepadState` (+ `HostGamepadButtons`, `HostGamepadType`, `HostGamepadConnection`, `HostMotionState`, `HostTouchState`, `HostAdaptiveTriggerEffect`), `GuestAudioClock`. |

### Per-OS implementations

**`Host/Windows/`** (namespace `SharpEmu.HLE.Host.Windows`, all `internal sealed`):
- `WindowsHostPlatform.cs` — wires `WindowsHostMemory` +
  `WindowsHostThreading` + `WindowsHostSymbolResolver` + **`SdlHostAudio`** +
  `WindowHostInput` (audio is SDL on Windows, not winmm).
- `WindowsHostMemory.cs` — `VirtualAlloc`/`VirtualFree`/`VirtualProtect`/
  `VirtualQuery` + `FlushInstructionCache` via `LibraryImport`.
- `WindowsHostThreading.cs` — kernel32 + winmm: `TlsAlloc/Free`, thread
  creation (`STACK_SIZE_PARAM_IS_A_RESERVATION`), `TryCaptureThreadRegisters`
  (Win64 `CONTEXT_AMD64_CONTROL_INTEGER` — no XMM), `timeBeginPeriod(1)`.
- `WindowsHostSymbolResolver.cs` — resolves `HostRuntimeFunction` values via
  `GetModuleHandle("kernel32.dll")` + `GetProcAddress`.
- `WindowsWaveOutAudio.cs` — winmm `waveOut*` alternative audio backend (not
  selected by `WindowsHostPlatform`).

**`Host/Posix/`** (namespace `SharpEmu.HLE.Host.Posix`, all `internal`):
- `PosixHostPlatform.cs` — wires `PosixHostMemory` + `PosixHostThreading` +
  `PosixHostSymbolResolver` + **`SdlHostAudio`** + `WindowHostInput`.
- `PosixHostMemory.cs` — `mmap`/`mprotect`/`munmap` with a shadow region
  table. Linux uses `MAP_FIXED_NOREPLACE`; macOS falls back to
  `mach_vm_allocate` with `VM_FLAGS_FIXED` (no overwrite).
- `PosixHostThreading.cs` — thin wrapper around `PosixHostStubs`;
  `TryCaptureThreadRegisters` returns `false` (no portable suspend+GetThreadContext).
- `PosixHostSymbolResolver.cs` — maps `HostRuntimeFunction` to JIT-emitted
  stubs in `PosixHostStubs`.
- `PosixHostStubs.cs` — emits x86-64 machine code at runtime into an RWX
  page, written to the Win64 calling convention so Core's emission code stays
  identical across platforms. Stubs: `TlsGetValue`, `QueryPerformanceCounter`,
  `SwitchToThread`, `Sleep`, `WaitForSingleObject`/`SetEvent`/`ExitThread`,
  worker events (macOS `dispatch_semaphore`, Linux `sem_*`),
  `CreateWorkerThread`/`WaitForWorkerThreadExit`,
  `CreateWin64ToSysVThunk` (shuffles rcx/rdx/r8/r9 → rdi/rsi/rdx/rcx).
- `PosixHostAudio.cs` + `PosixAlsaAudioStream.cs` (ALSA) +
  `PosixCoreAudioStream.cs` (AudioQueue) — native fallbacks, **not selected**
  by `PosixHostPlatform`.

**`Host/Sdl/SdlHostAudio.cs`** (namespace `SharpEmu.HLE.Host.Sdl`):
- Implements `IHostPcmAudioOutput`. Backend name `"sdl3"`. **The actual
  audio backend selected by both `WindowsHostPlatform` and
  `PosixHostPlatform`.** `TargetQueuedMilliseconds` defaults to 60, overridable
  via `SHARPEMU_AUDIO_LATENCY_MS`. Reports the device-played position to
  `GuestAudioClock` so video presentation can sync to what the player hears.
  `SHARPEMU_LOG_AUDIO_QUEUE=1` for per-second queue diagnostics.

### Cross-cutting statics

| File | Purpose |
| --- | --- |
| `GuestAudioClock.cs` | The only clock that advances at the rate the player hears. Used by video presentation to avoid running ahead of guest audio. |
| `HostMemory.cs` | `static unsafe class` — cross-platform host virtual-memory API with Win32 semantics. Separate from `IHostMemory`. Used by `GuestImageWriteTracker`. |
| `HostSessionControl.cs` | Lets host-facing libraries (VideoOut, AudioOut) request cooperative guest shutdown without depending on Core. |
| `HostMainThread.cs` | Runs work on the real process main thread (macOS requires its windowing event loop there). The CLI moves emulation onto a worker thread and parks the main thread in `Pump`. |

## `Aerolib/`

| File | Purpose |
| --- | --- |
| `Aerolib/Aerolib.cs` | The runtime NID → name catalog (`sealed class : ISymbolCatalog`). Lazy singleton + `Empty` placeholder. `LoadFromEmbeddedBinary` loads the `aerolib.bin` resource. See [SysABI §aerolib](../architecture/hle-and-sysabi.md#the-aerolib-catalog). |

## Native/PInvoke dependencies

- **Windows:** `kernel32.dll` (VM/thread/TLS/handle APIs) and `winmm.dll`
  (`timeBeginPeriod`, `waveOut*`).
- **POSIX:** `libc` (`mmap`, `pthread_*`, `gettid`, `sched_yield`, `usleep`,
  `sem_*`); macOS additionally `libSystem.B.dylib` (`mach_*`,
  `dispatch_semaphore_*`, `pthread_threadid_np`); Linux `libasound.so.2` (ALSA);
  macOS AudioToolbox (CoreAudio).
- **SDL3 native** library (bundled by `ppy.SDL3-CS`).

> The ALSA and CoreAudio backends are present but **not selected**; both
> platforms use `SdlHostAudio`. `WindowsWaveOutAudio` is in the same category.

## How to add an HLE export — checklist

1. Author a `static`, non-generic method returning `int` in a type that is at
   least `internal` (and in an assembly whose `InternalsVisibleTo` includes
   the generated registry's consumer). Annotate with `[SysAbiExport]`. If
   `Nid` is omitted, the generator derives it from `ExportName` via
   `Ps5Nid.Compute`; if `ExportName` is omitted, it falls back to the method
   name.
2. Choose a handler shape (`CpuContext` only, parameterless, or typed args
   up to 6). Use `[GuestCString(maxLength)]` for string parameters.
3. Return via `ctx.SetReturn(...)` or write `ctx[CpuRegister.Rax]` directly.
4. Access guest memory through `ctx.Memory` (typed helpers) or cast to
   `IGuestAddressSpace` for mmap/mprotect-style ops.
5. Set `Target = Generation.Gen4 | Generation.Gen5` for multi-generation
   handlers.
6. The generator emits the thunk; the analyzer validates the shape and NID.

See [SysABI / HLE export system](../architecture/hle-and-sysabi.md) for the
full reference.

## Gotchas

- **macOS requires the windowing event loop on the real main thread** — that's
  why `HostMainThread` exists. Don't "simplify" `PosixHostMemory`'s
  `MAP_FIXED_NOREPLACE`/`mach_vm_allocate` logic to plain `MAP_FIXED`.
- **Darwin doesn't support unnamed `sem_init`** — `PosixHostStubs` uses
  `dispatch_semaphore` on macOS and unnamed POSIX semaphores on Linux.
- **Win64-vs-SysV ABI thunk:** on POSIX, managed SysV-compiled callbacks must
  be wrapped in `CreateWin64ToSysVThunk` because emitted x86-64 call sites use
  the Win64 convention.
- **`TryCaptureThreadRegisters` returns false on POSIX** — diagnostics that
  depend on it work only on Windows.
- **Registration lifecycle:** `ModuleManager.Freeze()` must be called; a
  `.cctor` or first JIT on a guest thread's hijacked stack fail-fasts the CLR.
- **`HostRuntimeFunction` is an enum** — adding a member requires updating both
  `WindowsHostSymbolResolver.GetAddress` and `PosixHostSymbolResolver.GetAddress`
  (a new emitted stub in `PosixHostStubs.BuildStubs`).
- **Signal-handler-safe paths** must be allocation- and lock-free (see
  `GuestImageWriteTracker.TryHandleWriteFault`).

## Related

- [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
- [SharpEmu.SourceGenerators guide](source-generators.md)
- [SharpEmu.Libs guide](libs.md) — where the actual HLE exports live.
