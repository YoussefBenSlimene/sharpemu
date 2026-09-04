<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Getting Started

This page walks through setting up a development machine, building the
solution, and running a game. If anything here is wrong or out of date, please
open an issue or PR — this is the first page a new contributor reads.

## Prerequisites

- **.NET SDK 10.0.103** (with `latestFeature` roll-forward). Pinned in
  [`global.json`](../../global.json).
- A C# editor — the project uses `.editorconfig` so any editor that respects
  it works; the maintainers recommend **Visual Studio Code** with the **C#**
  and **C# Dev Kit** extensions.
- A Vulkan-capable GPU and a current graphics driver.
  - On macOS, Vulkan is provided by MoltenVK (bundled in the macOS release
    archive).
  - On Windows on ARM, the `win-x64` build runs through the OS x64 emulation.
  - On Apple Silicon, the `osx-x64` build runs under Rosetta 2.
- A legally obtained PS5 game's `eboot.bin` (see the [README §Using](../../README.md#using)).
  Do **not** use the emulator for piracy — only dump games from consoles you
  own.

## Clone and restore

```bash
git clone https://github.com/sharpemu/sharpemu.git
cd sharpemu
dotnet restore SharpEmu.slnx
```

The NuGet packages are downloaded into `.packages/` (configured by
[`nuget.config`](../../nuget.config)) and `$(BaseIntermediateOutputPath)` for
the FFmpeg runtime (see below).

## Build

```bash
dotnet build SharpEmu.slnx
```

Artifacts land in `artifacts/bin/<ProjectName>/<Configuration>/<TargetFramework>/<RuntimeIdentifier>/`
and object files in `artifacts/obj/...`. See
[Build configuration](../reference/build-configuration.md) for how the
paths are computed.

### Build for a specific RID

```bash
dotnet build SharpEmu.slnx -c Release -r win-x64
dotnet build SharpEmu.slnx -c Release -r linux-x64
dotnet build SharpEmu.slnx -c Release -r osx-x64
```

The supported RIDs are `win-x64`, `linux-x64`, and `osx-x64`. (`osx-arm64`
ships via the `osx-x64` build under Rosetta 2.)

## Publish

```bash
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r win-x64 --self-contained true
```

Output lands in `artifacts/publish/SharpEmu.CLI/<Configuration>/<TargetFramework>/<RuntimeIdentifier>/`.

The publish bundles a single-file `SharpEmu.exe` (or `SharpEmu` on POSIX) plus
the FFmpeg libraries in a `plugins/` subfolder (downloaded once, cached under
`$(BaseIntermediateOutputPath)ffmpeg-runtime/`). See the
[CLI guide §FFmpeg runtime](../projects/cli.md#ffmpeg-runtime-msbuild) and
[`bink2-bridge.md`](../bink2-bridge.md).

> A plain `dotnet build`/`dotnet publish` with no `-r` defaults to the host
> machine's RID (see `Directory.Build.props:17-22`), so it fetches the
> matching `ffmpeg-core` archive and populates `plugins/` without extra flags.

## Run a game

```bash
./artifacts/bin/SharpEmu.CLI/Release/net10.0/linux-x64/SharpEmu /path/to/eboot.bin
```

Use the debug server to attach at the first frame:

```bash
./artifacts/bin/SharpEmu.CLI/Release/net10.0/linux-x64/SharpEmu \
    --debug-server "/path/to/eboot.bin"
```

Then in another terminal:

```bash
dotnet run --project src/SharpEmu.DebugClient/SharpEmu.DebugClient.csproj -- 127.0.0.1:5714
status
regs
continue
```

Or use the browser frontend:

```bash
./tools/SharpEmu.DebuggerFrontend/run.sh
```

See [Debugging guide](debugging.md) and
[`debugger-server.md`](../debugger-server.md) for more.

## Run the GUI

Launch the executable with no arguments:

```bash
./artifacts/bin/SharpEmu.CLI/Release/net10.0/linux-x64/SharpEmu
```

The GUI spawns the CLI as an isolated child per game (see the
[GUI guide](../projects/gui.md)).

## Run tests

```bash
dotnet test SharpEmu.slnx
```

There are four test projects (`tests/`); see the [Testing guide](testing.md).

## Validate shaders

CI builds SPIRV-Tools pinned to a specific commit and runs:

```bash
dotnet run --project tools/SharpEmu.Tools.ShaderDump/SharpEmu.Tools.ShaderDump.csproj -c Release -- artifacts/shader-dump
scripts/validate-synthetic-spirv.sh /path/to/spirv-val v2026.2 vulkan1.2 artifacts/shader-dump
```

See [Build configuration §CI](../reference/build-configuration.md#ci) for the
exact pins.

## Verify your environment

A quick checklist:

- [ ] `dotnet --version` reports `10.0.103` (or `latestFeature` newer).
- [ ] `dotnet build SharpEmu.slnx` produces an `artifacts/bin/.../SharpEmu` executable.
- [ ] The `plugins/` folder exists next to the executable and contains the
      FFmpeg shared libraries (`avformat-*.dll`/`.so`/`.dylib`, `avcodec-*`, ...).
- [ ] Your Vulkan driver works (`vulkaninfo` reports a physical device).
- [ ] A test launch (`SharpEmu --debug-server /path/to/eboot.bin`) binds to
      `127.0.0.1:5714` and parks at the first frame.

## Where to go next

- [Architecture overview](../architecture/overview.md) — the codebase map.
- [Contributing workflow](contributing.md) — PR expectations, coding style,
  REUSE compliance.
- [Debugging guide](debugging.md) — diagnosing issues.
