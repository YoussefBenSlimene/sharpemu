<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Architecture Overview

This page explains how the SharpEmu solution is laid out, what each project
does, and the layering rules that keep them independent. It is the map you
should keep in your head while navigating the source.

## The big idea

SharpEmu emulates a PlayStation 5. The PS5 is an x86-64 machine, so instead of
recompiling every instruction to a different ISA, SharpEmu **runs guest code
natively on the host CPU**. This is called *direct execution* (a form of LLE).
The host CPU must therefore be x86-64; Apple Silicon runs the `osx-x64` build
under Rosetta 2, and Windows on ARM runs the `win-x64` build through the OS
emulation layer.

Because guest code runs natively, the guest's virtual addresses must equal the
host's virtual addresses — this is the **identity-mapped address space** (see
[Memory & address space](memory-and-address-space.md)). The loader maps
`eboot.bin` at the exact base address the PS5 uses (`0x0000000800000000`) so
guest code that assumes its own address works unchanged.

What the PS5 *also* has is a kernel and a large set of system libraries
(`libKernel`, `libSceVideoOut`, `libSceAgc`, ...). Guest code calls into these
through import stubs. SharpEmu **re-implements the most important ones in C#**
— this is **HLE** (High-Level Emulation). When guest code calls an import, a
trap stub diverts into managed code, which models the expected behavior and
returns. Where a guest module provides its own native implementation (e.g. the
game's bundled `libc`), SharpEmu prefers bridging straight to that native code
(LLE) and only falls back to HLE.

So the architecture has three concerns:

1. **Loading & memory** — parse the executable, map it at the right address,
   resolve relocations and imports.
2. **Execution** — jump into guest code, recover from faults (`#UD` for
   AMD-specific instructions, AVs for lazy memory / write-watch), and divert
   imports to managed handlers.
3. **System libraries (HLE)** — implement the PS5 OS routines the game needs,
   backed by host abstractions (window, audio, input, GPU) that work on Windows,
   Linux, and macOS.

## Project map

The solution (`SharpEmu.slnx`) has 13 projects in `src/` and 4 test projects
in `tests/`. Plus three helper tools in `tools/`.

### Layering (bottom to top)

```
                        ┌─────────────────────────────────────────────┐
                        │  SharpEmu.CLI  (WinExe entry point)         │
                        │   + SharpEmu.Debugger  + SharpEmu.GUI        │
                        └─────────────────────────────────────────────┘
                                          │  uses
                        ┌─────────────────┴───────────────────────────┐
                        │  SharpEmu.Core  (runtime / loader / memory / CPU) │
                        └─────────────────┬───────────────────────────┘
                                          │  references
              ┌───────────────────────────┴───────────────────────┐
              │  SharpEmu.HLE   (SysABI registry + host abstractions) │
              └───────────────────────────┬───────────────────────┘
                                          │  references
   ┌──────────────────────────────────────┴──────────────────────────────┐
   │  SharpEmu.Libs  (PS5 system libraries)                                │
   │   └─ SharpEmu.ShaderCompiler(+Vulkan+Metal)  └─ SharpEmu.LibAtrac9    │
   └──────────────────────────────────────────────────────────────────────┘
                                          │
                        ┌─────────────────┴───────────────────┐
                        │  SharpEmu.Logging  (shared logging)  │
                        └─────────────────────────────────────┘
                        ┌─────────────────────────────────────┐
                        │  SharpEmu.SourceGenerators           │
                        │   (compile-time analyzer only)      │
                        └─────────────────────────────────────┘
```

The dependency arrow is **one-way, downward**: Core depends on HLE; HLE depends
only on Logging; Libs depends on HLE + Logging + the shader compilers +
LibAtrac9. The CLI sits on top and wires everything together. The SourceGenerators
project is referenced as an **analyzer** (`OutputItemType="Analyzer"`,
`ReferenceOutputAssembly="false"`), so it never ships as a runtime dependency —
it only runs during compilation.

### Project roles

| Project | Role |
| --- | --- |
| `SharpEmu.Core` | The emulation engine: ELF/SELF loader, identity-mapped memory, the native CPU backend, the runtime facade. The biggest and most central project. |
| `SharpEmu.CLI` | The `SharpEmu` executable (`OutputType=WinExe`). Parses args, configures logging/video, optionally starts the debug server, then runs `SharpEmuRuntime`. Also contains the MSBuild targets that fetch the FFmpeg runtime. |
| `SharpEmu.HLE` | The bridge to the host: the SysABI module/export registry, the `IHost*` platform abstractions (memory, threading, audio, input, fault handling), and guest-side helpers (TLS, write-watch, image-write tracking). |
| `SharpEmu.Libs` | The collection of PS5 system libraries implemented as HLE. One folder per library (`Kernel/`, `VideoOut/`, `Gpu/`, `Audio/`, `Agc/`, `AvPlayer/`, ...). This is where most "game compatibility" work happens. |
| `SharpEmu.ShaderCompiler` | Backend-neutral half of GPU shader compilation: the Gen5 (gfx10/RDNA) microcode decoder, scalar evaluator, and the shader IR every backend consumes. Has no graphics-API dependency. |
| `SharpEmu.ShaderCompiler.Vulkan` | Gen5 → SPIR-V codegen. Emits raw SPIR-V words; no Vulkan bindings. Consumed by the Vulkan renderer in `SharpEmu.Libs/Gpu/Vulkan`. |
| `SharpEmu.ShaderCompiler.Metal` | Gen5 → Metal Shading Language source. Emits MSL text; consumed by the Metal renderer in `SharpEmu.Libs/Gpu/Metal`. |
| `SharpEmu.GUI` | The Avalonia desktop frontend. Hosted by the CLI when launched without arguments. Manages a game library, per-game settings, theming, and localization. **Games are never run in the GUI process** — the GUI spawns the CLI as an isolated child. |
| `SharpEmu.Debugger` | A live TCP debug server. Implements `ICpuDebugHook`, parks the emulation thread at frame boundaries, and serves a JSON-lines protocol. |
| `SharpEmu.DebugClient` | A standalone console client for the debug server. Takes no dependency on the emulator assemblies; speaks the wire protocol directly. |
| `SharpEmu.Logging` | The logging pipeline: `SharpEmuLog` static facade, `SharpEmuLogger` per category, console/file/composite sinks, and `BuildInfo` (CI build-provenance banner). |
| `SharpEmu.SourceGenerators` | A Roslyn source generator + analyzer. Turns `[SysAbiExport]` methods into a compile-time NID→handler registry, validates NIDs against the aerolib catalog, and builds the `aerolib.bin` embedded resource. Targets `netstandard2.0`. |
| `SharpEmu.LibAtrac9` | A managed ATRAC9 audio decoder (the PS5's audio codec). Originates from the VGMToolbox project (MIT). Used by `SharpEmu.Libs` audio code. |

### Test projects (`tests/`)

| Project | What it tests |
| --- | --- |
| `SharpEmu.Libs.Tests` | The largest test project. Covers HLE subsystems (VideoOut, Audio, Agc, Ampr, AvPlayer, Cpu, Memory, GUI, ...), the SysAbi registry, and the aerolib catalog. |
| `SharpEmu.ShaderCompiler.Tests` | The shader IR and the SPIR-V backend (control flow, SSA, float16, images, flat memory). |
| `SharpEmu.ShaderCompiler.Metal.Tests` | The MSL backend (translation, golden output, float16, native Metal runtime). |
| `SharpEmu.SourceGenerators.Tests` | The `SysAbiExportGenerator` and `SysAbiExportAnalyzer` using a Roslyn test host. |

### Tools (`tools/`)

| Tool | Purpose |
| --- | --- |
| `SharpEmu.Tools.ShaderDump` | Dumps synthetic shaders; used by CI to validate generated SPIR-V against `spirv-val`. |
| `SharpEmu.Tools.GpuConformance` | GPU conformance checks. |
| `SharpEmu.DebuggerFrontend` | A dependency-free Python browser UI for the debug server. See [`debugger-server.md`](../debugger-server.md). |

## The SysABI system at a glance

This is the single most important concept for contributing to `SharpEmu.Libs`.
A full explanation is in [SysABI / HLE export system](hle-and-sysabi.md); the
short version:

- Every PS5 OS function is identified by an 11-character **NID**.
- A managed handler is a `static` method annotated with `[SysAbiExport(LibraryName=..., Nid=..., ExportName=...)]`
  (in `SharpEmu.Libs`).
- At compile time, `SharpEmu.SourceGenerators` collects every such method and
  emits a `SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation)`
  that returns the list of `ExportedFunction` records.
- At runtime, `SharpEmuRuntime.CreateDefault` calls `CreateExports(Gen4|Gen5)`
  and `ModuleManager.RegisterExports` indexes them by NID.
- When the guest calls an import, the CPU backend catches the `int3` trap the
  loader installed, looks up the NID, and (if the import isn't bridged to a
  native target) calls the managed `SysAbiFunction(CpuContext)`.

The handler reads guest registers from `CpuContext` (in `Rdi, Rsi, Rdx, Rcx,
R8, R9` — the SysV calling convention) and returns an `int` that becomes the
guest's `RAX`.

## Where things live (cheat sheet)

| If you want to... | Look in |
| --- | --- |
| Understand how a game boots | `src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs` |
| Parse an ELF/SELF | `src/SharpEmu.Core/Loader/SelfLoader.cs` |
| See how guest memory is laid out | `src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs` |
| Understand native execution | `src/SharpEmu.Core/Cpu/CpuDispatcher.cs` and `Cpu/Native/DirectExecutionBackend*.cs` |
| Add an HLE export | `src/SharpEmu.Libs/<Subsystem>/` + `[SysAbiExport]` |
| Understand the SysABI generator | `src/SharpEmu.SourceGenerators/SysAbiExportGenerator.cs` |
| Work on GPU shaders | `src/SharpEmu.ShaderCompiler/`, `.Vulkan/`, `.Metal/` |
| Work on video output / Vulkan | `src/SharpEmu.Libs/VideoOut/`, `src/SharpEmu.Libs/Gpu/Vulkan/` |
| Work on the GUI | `src/SharpEmu.GUI/` |
| Change logging | `src/SharpEmu.Logging/` |
| Build/release | `Directory.Build.props`, `scripts/release.py`, `.github/workflows/workflow.yml` |

## Next

Continue to [Runtime & boot flow](runtime-flow.md) to see how a game actually
starts.
