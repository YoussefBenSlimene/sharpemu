<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Runtime & Boot Flow

This page walks through what happens between `SharpEmu.exe eboot.bin` and the
first guest instruction executing. The entry points are:

- CLI: `src/SharpEmu.CLI/Program.cs:47` (`Main`)
- Runtime: `src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs:132` (`Run`)

## 1. CLI startup (`Program.Main`)

`Program.Run` (`src/SharpEmu.CLI/Program.cs:83`) does, in order:

1. `Updater.TryApply` — apply a pending self-update if present.
2. Strip the internal `--sharpemu-mitigated-child` flag (trusted only when
   `SHARPEMU_MITIGATED_CHILD=1` is set by the parent).
3. **No arguments** → `GuiLauncher.Run()` (the GUI frontend).
4. **With arguments** → attach to the parent terminal's console (the exe is
   `WinExe`, so it has no console by default) and force UTF-8 output.
5. `CheckHostArchitecture` — the process must be `Architecture.X64`; on Apple
   Silicon it prints a Rosetta 2 hint and exits 5.
6. On macOS/Linux: enable `HostMainThread`, spawn the emulator on a worker
   thread, and park the main thread in `HostMainThread.Pump` (the windowing
   loop must run there). On macOS also configure MoltenVK defaults and preload
   the Vulkan loader.
7. **Windows mitigated-child relaunch** (unless `SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`):
   re-launch the process with CET/CFG shadow-stack mitigations disabled,
   because CET's `set_context` IP validation interferes with the VEH CONTEXT
   rewriting. The child is killed on close via a Job object.
8. Parse arguments (`--debug-server`, `--log-level`, `--resolution`, etc.) into
   `SharpEmuRuntimeOptions` + `HostVideoOptions`. Merge GUI + per-game video
   settings.
9. If `--debug-server` is set, create a `DebuggerServerHost`, start it, and set
   `runtimeOptions.DebugHook = debugHost.Hook` so the dispatcher notifies the
   server at frame boundaries.
10. `SharpEmuRuntime.CreateDefault(runtimeOptions)` → `runtime.Run(ebootPath)`.
11. On return, log `LastSessionSummary`, `LastExecutionDiagnostics`, etc., and
    exit `0` for `ORBIS_GEN2_OK` or `4` otherwise.

> **GUI note:** the GUI never runs a game in its own process. Guest virtual
> memory is fixed-address and cannot be reused while guest-created host threads
> are alive, so `EmulatorProcess` (`src/SharpEmu.GUI/EmulatorProcess.cs:16`)
> spawns the CLI as an isolated child with the same mitigation flags and
> captures its stdout/stderr.

## 2. Runtime construction (`SharpEmuRuntime.CreateDefault`)

`src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs:76` wires the engine:

1. Build a `CpuExecutionOptions` from the public options (engine, strict
   dynlib resolution, import trace limit, debug hook).
2. Create a `ModuleManager` and register every HLE export via the
   **source-generated** `SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5)`,
   then `Freeze()` it. Freezing runs `WarmHleTypeInitializers` — every HLE
   type's `.cctor` and first JIT happen on a host thread *before* any guest
   thread can reach them (a `.cctor` firing on a hijacked guest stack would
   FailFast the CLR). See [`ModuleManager.cs:63`](../projects/hle.md#modulemanager).
3. Construct `PhysicalVirtualMemory` — the identity-mapped guest address space
   (see [Memory](memory-and-address-space.md)).
4. `new SharpEmuRuntime(new SelfLoader(), virtualMemory, new CpuDispatcher(virtualMemory, moduleManager), moduleManager, Aerolib.Instance, cpuExecutionOptions, fileSystem)`.

The runtime implements `ISharpEmuRuntime` (`src/SharpEmu.Core/Runtime/ISharpEmuRuntime.cs`)
which exposes `LoadImage`, `Run`, `DispatchHleCall`, and several diagnostic
string properties.

## 3. `SharpEmuRuntime.Run(ebootPath)`

`src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs:132` is the heart of the boot
sequence. Steps:

### 3.1 Bind `/app0`
`BindApp0Root` sets `SHARPEMU_APP0_DIR` to the eboot's directory (special-casing
a `decrypted/` sidecar so `/app0` points at the content-bearing parent), and
starts `AmprFileRegistry.BeginApp0IndexPreload` in the background.

### 3.2 Reset & load
Reset runtime state (`FiberExports.ResetRuntimeState()`,
`KernelModuleRegistry.Reset()`), then `LoadImage`: read the file and call
`SelfLoader.Load`.

### 3.3 Configure app info
Push app metadata (title, title id, version) into the HLE subsystems that need
it (`VideoOutExports`, `KernelMemoryCompatExports`, `SaveDataExports`,
`SystemServiceExports`).

### 3.4 Register the main module
`KernelModuleRegistry.RegisterModule` — first computing the image range
(`TryComputeImageRange`) and `.eh_frame` info (`TryGetEhFrameInfo`).

### 3.5 Pick the generation
`Generation.Gen5` if `ElfHeader.AbiVersion == 2`, else `Gen4`. PS5 ELF
executables set `AbiVersion=2`.

### 3.6 Load adjacent SCE modules
`LoadAdjacentSceModules` discovers `.prx`/`.sprx` modules in `sce_module/`,
`sce_modules/`, `Media/Modules/`, `Media/Plugins/` (skips `libkernel.prx` /
`libkernel_sys.prx` unless `SHARPEMU_PRELOAD_ALL_SCE_MODULES=1`), loads each
with `SelfLoader.LoadAdditional`, merges their import stubs and runtime
symbols, installs FMOD compatibility hooks, and registers each.

### 3.7 Rebind imported data symbols
`RebindImportedDataSymbols` rewrites imported **data** relocations
(`R_X86_64_*` for `SymbolTypeObject`) using the merged runtime symbol table
(`SHARPEMU_LOG_DATA_REBIND=1` traces it).

### 3.8 Run module initializers
`RunAllInitializers` → `RunPreloadedModuleInitializers` dispatches each
module's `DT_INIT` via `CpuDispatcher.DispatchModuleInitializer`. The main
image's own `DT_INIT` is not auto-run today (it commonly resolves to
`imageBase+0x10` inside the ELF header on PS5 dumps).

### 3.9 Enter the guest
`CpuDispatcher.DispatchEntry(entryPoint, generation, ...)` runs the guest.
See [CPU execution](cpu-execution.md) for what happens inside.

### 3.10 Post-run diagnostics
If the result is a trap, memory fault, or not-implemented, build a detailed
`LastExecutionDiagnostics` string: decode the instruction at RIP via
`IcedDecoder`, show the opcode preview, the last control transfer, the
import-stub-at-RIP lookup, and the aerolib NID resolution. Host shutdown
(`HostSessionControl.IsShutdownRequested`) skips this to avoid delaying the
GUI exit callback.

> `Dispose` keeps the guest address space mapped if
> `CpuDispatcher.NativeSessionLeaked` is true — guest worker threads may still
> be inside guest code when the main entry returns.

## 4. The loader (`SelfLoader.Load`)

`src/SharpEmu.Core/Loader/SelfLoader.cs` parses PS4/PS5 executable formats:
bare decrypted ELF64, fake-signed SELF (fSELF), `eboot.bin`, and `.prx`/`.sprx`
modules. It performs full dynamic linking.

`LoadCore` (`SelfLoader.cs:151`) steps:

1. `TryLoadParamJson` (main image only) — read `sce_sys/param.json` for title/version.
2. Optionally `virtualMemory.Clear()` + reset `GuestTlsTemplate`.
3. `ParseLayout` — inspect the leading 4 bytes: SELF magic → parse `SelfHeader`
   + `SelfSegment[]`; otherwise expect ELF magic (throw a clear "still-encrypted
   retail eboot — use fSELF" error if neither).
4. `ReadUnmanaged<ElfHeader>` + `ValidateElfHeader` (ELF64, little-endian,
   `EM_X86_64 == 62`).
5. `ParseProgramHeaders`.
6. Detect `PT_TLS`, assign a TLS module id.
7. Size the image + `DetermineRequestedImageBase` — PS5 main image uses
   `0x0000000800000000`, PS4 uses `0x00000000_00400000`; additional modules
   scan the existing regions for a free slot.
8. Allocate at the exact base (`TryAllocateAtExact`, falling back to
   page-by-page `TryBackFixedRange`).
9. `MapLoadSegments` — copy each `PT_LOAD`'s file bytes into the address space
   and zero-fill `.bss`. Encrypted/compressed SELF segments are rejected.
10. `RegisterModuleTlsTemplate` — seed `GuestTlsTemplate` with the module's
    `tdata` (TLS Variant II).
11. `ResolveAndPatchImportStubs` — parse the dynamic table (standard +
    Sony `DT_SCE_*` tags), collect `RELA` + `JMPREL` relocations, resolve
    symbol names, **create the import trap-stub region** at
    `0x0000700000000000` (each slot = `{0xCC (int3), 0xC3 (ret), pad, NID hash @+8}`),
    compute and write relocation values, and return the `address → NID` map.
    Weak symbols use `S=0` and do not get a trap stub.
12. `RegisterRuntimeSymbolsAndHooks` — index section + dynamic symbol tables,
    install the `kernel_dynlib_dlsym` runtime stub.
13. `CollectInitializerFunctions` — read `DT_INIT`, `DT_PREINIT_ARRAY`,
    `DT_INIT_ARRAY`.
14. `ResolveProcParamAddress` (`PT_SCE_PROCPARAM`).

The relocation types supported are standard `R_X86_64_*` (1,2,4,6,7,8,10,11,16
DTPMOD64, 17 DTPOFF64, 18 TPOFF64, 24,32,33,38). `COPY (5)` and `IRELATIVE
(37)` throw — runtime-linker support is deferred.

## 5. CPU dispatch (`CpuDispatcher.DispatchEntry`)

`src/SharpEmu.Core/Cpu/CpuDispatcher.cs` prepares a guest frame:

1. `TryMapStackRegion` — 2 MiB stack near the stack base.
2. `TryMapTlsRegion` — 64 KiB + the startup static TLS reservation.
3. Build a `CpuContext` with `Rip=entryPoint`, `Rflags=0x202`,
   `FsBase=GsBase=tlsBase`.
4. Map the **return-to-host stub** (`hlt; int3` = `0xF4 0xCC`) and push its
   address as the initial return slot.
5. Seed a zeroed sentinel frame and `RBP`/`RSP`.
6. `InitializeTls` — write the FreeBSD-amd64 Variant-II TCB slots
   (`+0x28=0xC0DEC0DECAFEBA00` canary, etc.) then `GuestTlsTemplate.SeedThreadBlock`.
7. For process entry: map a dynlib fallback stub (`xor eax,eax; ret`) and call
   `InitializeProcessEntryFrame` — encode `argv` (`SHARPEMU_GUEST_ARGS` may
   append up to 2 extra args), write the 0x20-byte PS5 entry-params struct
   (`argc`, `argv[0..2]`), set `RDI=entryParamsAddress`, `RSI=programExitHandlerAddress`.
   Optionally inject a bootstrap payload if the entry-point bytes match the
   known bootstrap signature.
8. For module initializers: `InitializeModuleInitializerFrame` zeroes the
   argument registers.
9. Reject any engine other than `CpuExecutionEngine.NativeOnly`.
10. Notify `ICpuDebugHook.OnFrameEnter` (the debug server, if attached).
11. Lazily construct `DirectExecutionBackend(_moduleManager)` and `TryExecute`.
12. `OnFrameExit` + build `CpuSessionSummary`.

From here, execution is inside the native backend — see
[CPU execution & fault handling](cpu-execution.md).

## Next

- [Memory & address space](memory-and-address-space.md) — the addresses this
  flow maps.
- [CPU execution & fault handling](cpu-execution.md) — what happens after
  `DispatchEntry`.
