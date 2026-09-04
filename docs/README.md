<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu Developer Documentation

This folder is the reference for working on the SharpEmu codebase. It is written
for a developer who is **new to the project** — even if you have never read a
line of the source, the guides below explain how the emulator is structured, how
a game flows through it, and where to make changes.

> SharpEmu is an experimental PlayStation 5 emulator written in C#. It executes
> guest x86-64 code **natively on the host CPU** and re-implements the PS5
> system libraries in managed code (HLE). See the project root
> [`README.md`](../README.md) for the user-facing overview and supported titles.

## Where to start

If you are reading this for the first time, go in this order:

1. **[Architecture overview](architecture/overview.md)** — what the projects are, how they depend on each other, and where the big ideas live.
2. **[Runtime & boot flow](architecture/runtime-flow.md)** — how `eboot.bin` is loaded, mapped, and starts executing.
3. **[Memory & address space](architecture/memory-and-address-space.md)** — the identity-mapped guest memory model.
4. **[The SysABI / HLE export system](architecture/hle-and-sysabi.md)** — how a guest NID becomes a managed C# call.
5. **[CPU execution & fault handling](architecture/cpu-execution.md)** — the direct-execution backend, import trampolines, and `#UD`/AV recovery.
6. **[Getting started](guides/getting-started.md)** — set up your machine, build, and run a game.

After that, dive into the per-project guides and reference pages as needed.

## Index

### Architecture

- [Overview](architecture/overview.md) — project map, dependency graph, layering rules.
- [Runtime & boot flow](architecture/runtime-flow.md) — `SharpEmuRuntime.Run` step by step.
- [Memory & address space](architecture/memory-and-address-space.md) — guest VA layout, allocation, page protection.
- [SysABI / HLE export system](architecture/hle-and-sysabi.md) — NIDs, `[SysAbiExport]`, the source generator, dispatch.
- [CPU execution & fault handling](architecture/cpu-execution.md) — the native backend, TLS patching, exception recovery.

### Project guides

One page per project in `src/`. Each describes purpose, key files, public API,
and gotchas.

- [SharpEmu.Core](projects/core.md) — loader, memory, CPU, runtime facade.
- [SharpEmu.CLI](projects/cli.md) — executable entry point, argument parsing, FFmpeg runtime.
- [SharpEmu.HLE](projects/hle.md) — host abstractions, SysABI registry, guest helpers.
- [SharpEmu.Libs](projects/libs.md) — the PS5 system libraries (Kernel, VideoOut, Gpu, Audio, Agc, ...).
- [SharpEmu.ShaderCompiler](projects/shader-compiler.md) — the shared shader IR.
- [SharpEmu.ShaderCompiler.Vulkan](projects/shader-compiler.md#vulkan-spir-v-backend) — Gen5 → SPIR-V.
- [SharpEmu.ShaderCompiler.Metal](projects/shader-compiler.md#metal-msl-backend) — Gen5 → MSL.
- [SharpEmu.GUI](projects/gui.md) — the Avalonia desktop frontend.
- [SharpEmu.Debugger](projects/debugger.md) — live TCP debug server.
- [SharpEmu.DebugClient](projects/debugger.md#debugclient) — standalone CLI client.
- [SharpEmu.Logging](projects/logging.md) — the logging pipeline.
- [SharpEmu.SourceGenerators](projects/source-generators.md) — the Roslyn SysAbi generator + aerolib task.
- [SharpEmu.LibAtrac9](projects/libatrac9.md) — managed ATRAC9 audio decoder.

### Guides (how to do things)

- [Getting started](guides/getting-started.md) — prerequisites, build, run.
- [Contributing workflow](guides/contributing.md) — PR process, coding style, REUSE compliance.
- [Testing](guides/testing.md) — test layout, running tests, writing tests.
- [Debugging](guides/debugging.md) — how to debug the emulator and a running guest.

### Reference

- [Environment variables](reference/environment-variables.md) — every `SHARPEMU_*` knob.
- [Build configuration](reference/build-configuration.md) — `Directory.Build.props`, central packages, `global.json`.
- [HLE module map](reference/hle-module-map.md) — the libraries implemented in `SharpEmu.Libs`.

### Existing topic notes

These pre-existing notes cover specific subsystems in depth and are referenced
from the guides above:

- [`aerolib-catalog.md`](aerolib-catalog.md) — the PS5 NID catalog CLI.
- [`bink2-bridge.md`](bink2-bridge.md) — Bink 2 video playback bridge.
- [`debugger-server.md`](debugger-server.md) — live debug server wire protocol.
- [`guest-write-watch.md`](guest-write-watch.md) — guest memory write-watch diagnostics.
- [`release-use.md`](release-use.md) — release script usage.

## Conventions used in this documentation

- `file_path:line_number` references (e.g. `src/SharpEmu.Core/Runtime/SharpEmuRuntime.cs:132`)
  point at the exact source location. Open them in your editor.
- "guest" = the emulated PS5 process/code. "host" = the machine and the SharpEmu
  process running it.
- "HLE" = High-Level Emulation (a managed C# implementation of a PS5 OS routine).
  "LLE" = Low-Level Emulation (running the guest's own native implementation).
  SharpEmu prefers LLE when a guest target exists and falls back to HLE.
- "NID" = the 11-character Sony symbol identifier; see
  [SysABI](architecture/hle-and-sysabi.md).
- "Gen4" = PS4, "Gen5" = PS5 (see `src/SharpEmu.HLE/Generation.cs`).

## Contributing to these docs

These docs are part of the repository and follow the same rules as code: keep
them accurate, prefer specific `file:line` references over vague prose, and add
the SPDX license header to every new file. If you change behavior that a page
describes, update the page in the same PR.
