<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.Libs

The collection of PS5 system libraries implemented as HLE. This is where most
"game compatibility" work happens: one folder per library, each containing
`[SysAbiExport]` methods. See [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
for how exports are authored and dispatched.

`src/SharpEmu.Libs/SharpEmu.Libs.csproj` references `SharpEmu.HLE`,
`SharpEmu.Logging`, the three `SharpEmu.ShaderCompiler*` projects,
`SharpEmu.LibAtrac9`, and `SharpEmu.SourceGenerators` (as an analyzer,
`ReferenceOutputAssembly=false`). NuGet packages: `FFmpeg.AutoGen`, `NLayer`,
`ppy.SDL3-CS`, `Silk.NET.Vulkan` (+ EXT/KHR extensions).
`AllowUnsafeBlocks=true`. `InternalsVisibleTo` includes `SharpEmu.Libs.Tests`
and `SharpEmu.Core` (the stall watchdog reads `GpuWaitRegistry` for
diagnostics). `AdditionalFiles` includes `scripts/ps5_names.txt` so the
analyzer can flag unknown export names.

## The library pattern

Each subfolder is one library module (or a closely related group). A library
is a class with `[SysAbiExport]` methods — there is no per-module registration
code to write. At compile time, `SysAbiExportGenerator` collects every
`[SysAbiExport]` method in the assembly into
`SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation)`, and at
runtime `SharpEmuRuntime.CreateDefault` calls it and
`ModuleManager.RegisterExports` indexes them by NID. To add a library you add
a subfolder and a class with annotated methods; nothing else.

> **Error codes:** return `OrbisGen2Result.ORBIS_GEN2_*` codes (or the
> PS5-specific `SCE_ERROR_*` values the library documents). Stubs that only
> return success/zero without implementing the expected state are **not
> accepted** — see [`CONTRIBUTING.md`](../../CONTRIBUTING.md).

## Root files

| File | Purpose |
| --- | --- |
| `HostTiming.cs` | Host timing helpers for HLE timing-based exports. |
| `HostFsPath.cs` | Host filesystem path helpers (the `/app0` mount, save data paths). |
| `LibcStdioExports.cs` | `libc` stdio exports (`printf`-family, file I/O). |
| `LibcInternalExports.cs` | `libc` internal exports. |
| `CxxAbiExports.cs` | C++ ABI exports (Itanium ABI runtime — `__cxa_*`). |

## Subsystem guide

One row per subfolder. "Status" reflects the tested surface (see
`tests/SharpEmu.Libs.Tests/`); individual exports may still be stubs.

### OS / Kernel

| Folder | Purpose | Notes |
| --- | --- | --- |
| `Kernel/` | `libKernel` — the largest and most central library. Threads (`scePthread*`), mutexes, semaphores, events, condition variables, process/module queries, memory mapping, dynlib (dynamic linker) helpers. | The threading/sync model is built on `IGuestThreadScheduler` (in `SharpEmu.HLE`) and `IHostThreading`. Blocking HLE exports use `GuestThreadExecution.RequestCurrentThreadBlock(...)`. `KernelModuleRegistry` tracks loaded modules; `KernelMemoryCompatExports` handles PS4/PS5 memory quirks. |
| `SystemService/` | `libSceSystemService` — system state, events, power state, app lifecycle. | Receives app metadata at boot (`SharpEmuRuntime` calls `SystemServiceExports`). `HostSessionControl` requests cooperative shutdown. |
| `UserService/` | `libSceUserService` — user accounts, login state. | |
| `Pad/` | `libScePad` — controller input. | Reads `HostGamepadState` snapshots via `IHostInput`. Merge policy between physical devices and keyboard lives here. |
| `Mouse/` | `libSceMouse` — mouse input. | |
| `SaveData/` | `libSceSaveData` — save data read/write. | Uses `HostFsPath`. |
| `Rtc/` | `libSceRtc` — real-time clock. | |
| `Random/` | `libSceRandom` — random number generation. | |
| `Fiber/` | `libSceFiber` — fibers (cooperative user threads). | `FiberExports.ResetRuntimeState()` is called at boot. |
| `Network/` | `libSceNet` / `libSceHttp` — networking. | |
| `Np/` / `NpGameIntent/` | `libSceNp*` — NP (network play) + game intents. | |
| `Remoteplay/` | `libSceRemoteplay` — remote play. | |
| `Share/` | `libSceShare` — share features. | |
| `Voice/` | `libSceVoice` — voice chat. | |

### Audio

| Folder | Purpose | Notes |
| --- | --- | --- |
| `Audio/` | `libSceAudioOut` — audio output. | `AudioOutExports` opens a stereo PCM stream via `IHostAudioOutput`. `GuestAudioClock` reports playback position for A/V sync. PCM conversion helpers covered by `AudioPcmConversionTests`. |
| `Ajm/` | `libSceAjm` — Audio Job Manager (batched audio decode/mix). | |
| `Acm/` | `libSceAcm` — audio codec manager. | |
| `Ngs2/` | `libSceNgs2` — Next-gen Sound (audio synthesis). | |
| `Codec/` | `libSceCodec` — codec exports. | |
| (LibAtrac9) | ATRAC9 decoder — implemented in the separate `SharpEmu.LibAtrac9` project (see [`libatrac9.md`](libatrac9.md)). | |

### Video / GPU

| Folder | Purpose | Notes |
| --- | --- | --- |
| `VideoOut/` | `libSceVideoOut` — display outputs, pixel formats, present/flip. | `VideoOutExports` is the main entry; receives app metadata at boot. Defines output sets, pixel formats (`VideoOutPixelFormat`), output options, host video options. See the [present pipeline](#the-present-pipeline) below. |
| `Gpu/` | GPU command submission + the host backends. | `Gpu/` has the submission/recording layer; `Gpu/Vulkan/` and `Gpu/Metal/` are the host renderers. See [GPU backends](#gpu-backends) below. |
| `Agc/` | `libSceAgc` — Advanced Graphics Core (the PS5 GPU command API). | The central GPU library: command submission, shader stages, resource ownership, textures, predication, rect list, label producer retention, fused shaders, vertex metadata, texture transport, submit completion events. Connects to the `Gpu/` backends. |

### Media

| Folder | Purpose | Notes |
| --- | --- | --- |
| `AvPlayer/` | `libSceAvPlayer` — video player. | `AvPlayerAbi`, allocation, stream info, NV12 layout, paths. See [Media pipeline](#media-pipeline) below. |
| `Media/` | `libSceMedia` — media frame playback. | `HostMovieBridge` and the Bink 2 bridge — see [`bink2-bridge.md`](../bink2-bridge.md). `FfmpegRuntime.EnsureInitialized` loads FFmpeg from `plugins/`. |
| `PlayGo/` | `libScePlayGo` — PlayGo (progressive content delivery) scenarios. | |
| `Psml/` | PSML media. | |
| `DiscMap/` | `libSceDiscMap` — disc mapping. | |
| `Font/` | `libSceFont` — font rendering. | |

### Content / App lifecycle

| Folder | Purpose | Notes |
| --- | --- | --- |
| `AppContent/` | `libSceAppContent` — app content. | |
| `ContentExport/` | `libSceContentExport` — content export. | |
| `GameUpdate/` | `libSceGameUpdate` — game updates. | |
| `Ime/` | `libSceIme` — input method editor. | |
| `CommonDialog/` | `libSceCommonDialog` — common dialogs. | |
| `SystemGesture/` | `libSceSystemGesture` — system gestures. | |
| `Ult/` | `libSceUlt` — UI Language Toolkit? | |
| `Json/` | `libSceJson` — JSON. | |
| `Diagnostics/` | `libSceDiagnostics` — diagnostics. | |

### Profiling / misc

| Folder | Purpose | Notes |
| --- | --- | --- |
| `Ampr/` | AMPR — memory paging / streaming. | `AmprFileRegistry` (background app0 index preload), `PakDirectoryTracker`, `AmprWriteAddress`, `AprStreamingContract`. |
| `Stubs/` | Generic stub mechanism for unimplemented libraries. | Returns success/zero without state — useful for unblocking imports that are never meaningfully exercised. Use sparingly; real implementations are preferred (see [`CONTRIBUTING.md`](../../CONTRIBUTING.md)). |

## The present pipeline

`VideoOut` + `Gpu` + `Agc` together take a guest frame to the screen:

1. The guest records GPU commands (AGC submits) and resources via the `Agc/`
   library.
2. `Gpu/` replays those submits through a host backend:
   - `Gpu/Vulkan/` — the production backend on Windows and Linux. Physical
     device scoring, pipeline cache storage, guest images (CPU sync policy,
     aliasing, byte counting, host buffer pool), format conversion, depth
     attachments, present encode format.
   - `Gpu/Metal/` — the macOS backend. Compiles MSL from
     `SharpEmu.ShaderCompiler.Metal`.
3. `VideoOut` flips/presents the rendered image. The video presenter syncs to
   `GuestAudioClock` so it doesn't run ahead of what the player hears.
4. `GuestImageWriteTracker` re-arms write protection on GPU-backed surfaces
   after the video backend consumes the dirty flag, so a subsequent guest CPU
   write re-tracks.

The Vulkan/Metal backends consume shader code produced by the
`SharpEmu.ShaderCompiler` projects (see [`shader-compiler.md`](shader-compiler.md)).

## Media pipeline

`AvPlayer` + `Media` handle video playback:

1. `AvPlayer/` implements the player ABI: stream info, allocation, NV12
   layout, paths.
2. `Media/HostMovieBridge` bridges guest movie opens to a host decoder. The
   default path decodes Bink 2 via FFmpeg's C API
   (`SharpEmu.Libs/Bink/FfmpegNativeBinkFrameSource.cs`) using
   [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) against the
   custom `github.com/sharpemu/ffmpeg-core` build (LGPL-2.1) that adds a Bink
   2 decoder to FFmpeg 7.1.2. See [`bink2-bridge.md`](../bink2-bridge.md) for
   the `SHARPEMU_BINK_MODE` options.
3. `FfmpegRuntime.EnsureInitialized` sets `ffmpeg.RootPath = <baseDir>/plugins`
   and initializes the dynamically-loaded bindings exactly once. The native
   FFmpeg shared libraries are fetched at build/publish time by the CLI MSBuild
   targets (see [`cli.md`](cli.md#ffmpeg-runtime-msbuild)).

## Dependencies

- `Silk.NET.Vulkan` (+ EXT/KHR extensions) — the Vulkan backend.
- `ppy.SDL3-CS` — window/event/input (the same SDL3 used by `SdlHostAudio`).
- `FFmpeg.AutoGen` — Bink 2 / media decoding.
- `NLayer` — MP3 decoding.
- `SharpEmu.LibAtrac9` — ATRAC9 audio decoding.

## How to add a new HLE library

1. Create a subfolder `src/SharpEmu.Libs/<LibraryName>/`.
2. Add a `static` class with `[SysAbiExport(LibraryName="libSce...", Nid=...,
   ExportName=...)]` methods. Look up NIDs via
   `python scripts/aerolib_catalog.py lookup <name>`.
3. Choose handler shapes per the [SysABI guide](../architecture/hle-and-sysabi.md#handler-shapes).
4. Return real state, not just success/zero.
5. Write tests under `tests/SharpEmu.Libs.Tests/<LibraryName>/` mirroring the
   existing subsystem tests (e.g. `VideoOut/`, `Audio/`, `Agc/`).
6. Build — the analyzer validates NIDs against `scripts/ps5_names.txt`.

## Related

- [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
- [HLE module map](../reference/hle-module-map.md) — test coverage per
  subsystem.
- [SharpEmu.HLE guide](hle.md) — the host abstractions and `CpuContext`.
- [`bink2-bridge.md`](../bink2-bridge.md) — Bink 2 video.
