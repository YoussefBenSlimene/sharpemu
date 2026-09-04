<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# HLE Module Map

This page maps the PS5 system libraries implemented in
`src/SharpEmu.Libs/<Subfolder>/` to the test coverage in
`tests/SharpEmu.Libs.Tests/`. The test class names are the most reliable
signal of how complete each subsystem is — a subsystem with many tests has a
real implementation; a subsystem with no tests is likely still stubbed.

> The pattern for adding a library is in the
> [Libs guide](../projects/libs.md#how-to-add-a-new-hle-library) and the
> [SysABI reference](../architecture/hle-and-sysabi.md).

## Root files (no subfolder)

| File | Purpose | Tests |
| --- | --- | --- |
| `HostTiming.cs` | Host timing helpers. | — |
| `HostFsPath.cs` | Host filesystem path helpers. | — |
| `LibcStdioExports.cs` | `libc` stdio (`printf`-family). | — |
| `LibcInternalExports.cs` | `libc` internal exports. | — |
| `CxxAbiExports.cs` | C++ ABI runtime (`__cxa_*`). | — |

## Subsystems

| Subfolder | Purpose | Test coverage (subset) |
| --- | --- | --- |
| `Kernel/` | libKernel — threads (`scePthread*`), mutexes, semaphores, events, condvars, process/module queries, memory mapping, dynlib. The central library. | `Tls/`, `Cpu/` (threading, guest-thread block waiter, context transfer), `KernelMemoryCompat` (via `KernelMemoryCompatExports`) |
| `SystemService/` | libSceSystemService — system state, lifecycle. | `SystemService/SystemServiceExportsTests` |
| `UserService/` | libSceUserService — user accounts. | — |
| `Pad/` | libScePad — controller input. | — |
| `Mouse/` | libSceMouse — mouse input. | — |
| `SaveData/` | libSceSaveData — save data. | `SaveDataStorageTests` |
| `Rtc/` | libSceRtc — real-time clock. | `Rtc/RtcExportsTests` |
| `Random/` | libSceRandom — RNG. | — |
| `Fiber/` | libSceFiber — fibers. | `Fiber/FiberExportsTests` |
| `Network/` | libSceNet / libSceHttp — networking. | `Network/NetExportsTests` |
| `Np/` | libSceNp* — NP. | — |
| `NpGameIntent/` | NP game intents. | — |
| `Remoteplay/` | libSceRemoteplay. | — |
| `Share/` | libSceShare. | — |
| `Voice/` | libSceVoice — voice chat. | — |
| `Audio/` | libSceAudioOut — audio output. | `Audio/AudioOutExportsTests`, `Audio/AudioOut2PortGetStateExportsTests`, `Audio/AudioOut2SpeakerArrayExportsTests`, `Audio/AudioPcmConversionTests` |
| `Ajm/` | libSceAjm — Audio Job Manager. | `Audio/AjmExportsTests` |
| `Acm/` | libSceAcm — audio codec manager. | `Audio/AcmExportsTests` |
| `Ngs2/` | libSceNgs2 — Next-gen Sound. | — |
| `Codec/` | libSceCodec. | — |
| `VideoOut/` | libSceVideoOut — display outputs, pixel formats, present/flip. | `VideoOut/*Tests` (many — pixel format, output support/options, Vulkan present encode format, pipeline cache, physical device scoring, host buffer pool, guest image type/sync/byte count/alias, format conversion, depth attachment, PNG splash loader, host video options) |
| `Gpu/` | GPU command submission + host backends. | `VideoOut/Vulkan*` tests exercise the Vulkan renderer |
| `Agc/` | libSceAgc — the PS5 GPU command API. | `Agc/*Tests` (many — vertex metadata, texture transport, submit completion event, shader stage register, resource owner, rect list index helpers, prim state hull variant, predication, label producer retention, fused shaders) |
| `AvPlayer/` | libSceAvPlayer — video player. | `AvPlayer/*Tests` (stream info, path, NV12 layout, allocation, ABI) |
| `Media/` | libSceMedia — media frame playback + the Bink 2 host bridge. | `Media/MediaFramePlaybackTests`, `Media/HostMovieBridgeTests` |
| `PlayGo/` | libScePlayGo — PlayGo scenarios. | — |
| `Psml/` | PSML media. | — |
| `DiscMap/` | libSceDiscMap. | — |
| `Font/` | libSceFont — fonts. | `Font/FontExportsTests` |
| `AppContent/` | libSceAppContent. | — |
| `ContentExport/` | libSceContentExport. | — |
| `GameUpdate/` | libSceGameUpdate. | — |
| `Ime/` | libSceIme — input method editor. | — |
| `CommonDialog/` | libSceCommonDialog. | — |
| `SystemGesture/` | libSceSystemGesture. | — |
| `Ult/` | libSceUlt. | — |
| `Json/` | libSceJson. | — |
| `Diagnostics/` | libSceDiagnostics. | — |
| `Ampr/` | AMPR — memory paging / streaming. | `Ampr/*Tests` (PakDirectoryTracker, AprStreamingContract, AmprWriteAddress, AmprFileRegistry) |
| `Stubs/` | Generic stubs for unimplemented libraries. | — |

## Other test areas

- `tests/SharpEmu.Libs.Tests/Memory/` — `VirtualMemoryTests`,
  `PosixHostMemoryTests`, `PhysicalVirtualMemoryTests`,
  `GuestMemoryAllocatorTests`, `GuestImageWriteTrackerTests`.
- `tests/SharpEmu.Libs.Tests/Cpu/` — TLS load patch boundary, SSE4a POSIX
  signal recovery, SSE4a bit-field emulator, memcpy HLE routing, JIT stubs,
  import trampoline ABI, guest-thread block waiter, guest context transfer,
  Gen5 native return smoke, direct-execution backend LLE preference, BMI
  instruction emulator.
- `tests/SharpEmu.Libs.Tests/Logging/SharpEmuLogTests.cs` — the logging
  pipeline.
- `tests/SharpEmu.Libs.Tests/SysAbiRegistryTests.cs` — the SysAbi registry.
- `tests/SharpEmu.Libs.Tests/AerolibCatalogTests.cs` — the aerolib catalog.
- `tests/SharpEmu.Libs.Tests/FakeCpuMemory.cs` — the shared memory fake.
- `tests/SharpEmu.Libs.Tests/Sse4aExtrqBlendPatchTests.cs` — the SSE4a
  load-time patch.

## Related

- [SharpEmu.Libs guide](../projects/libs.md)
- [The SysABI / HLE export system](../architecture/hle-and-sysabi.md)
- [Testing guide](../guides/testing.md)
