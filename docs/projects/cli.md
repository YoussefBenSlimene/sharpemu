<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.CLI

The `SharpEmu` executable (`OutputType=WinExe`, `AssemblyName=SharpEmu`).
Parses command-line arguments, configures logging/video, optionally launches a
live debug server, then constructs a `SharpEmuRuntime` via
`SharpEmuRuntime.CreateDefault` and calls `runtime.Run(ebootPath)`. It also
contains the MSBuild targets that fetch the FFmpeg runtime.

`src/SharpEmu.CLI/SharpEmu.CLI.csproj` references `SharpEmu.Core`,
`SharpEmu.Debugger`, `SharpEmu.GUI`, and `SharpEmu.Logging`. It has no direct
PackageReferences — transitive via referenced projects. Notable MSBuild
properties: `SelfContained=true`, `PublishSingleFile=true`,
`IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`,
`TieredPGO=true`. **Server GC is disabled** (a per-processor heap range can
collide with the fixed PS5 image bases); **concurrent GC is disabled on macOS**
(background GC's `FlushProcessWriteBuffers` stalls indefinitely under Rosetta 2).

> Read [Runtime & boot flow §1](../architecture/runtime-flow.md#1-cli-startup-programmain)
> for the startup sequence. This page covers the CLI's interface and the FFmpeg
> wiring.

## Entry point

`src/SharpEmu.CLI/Program.cs:47` — `[STAThread] private static int Main(string[] args)`.

`Program.Run` (`Program.cs:83`):
1. `Updater.TryApply` (self-update).
2. `NormalizeInternalArguments` — strip `--sharpemu-mitigated-child` (trusted
   only when `SHARPEMU_MITIGATED_CHILD=1` is set by the parent).
3. **No args** → `GuiLauncher.Run()` (the GUI frontend).
4. **With args** → attach to console, force UTF-8 output.
5. `CheckHostArchitecture` — must be `Architecture.X64`.
6. macOS/Linux: enable `HostMainThread`, spawn emulation worker thread, pump
   main thread. macOS: configure MoltenVK defaults + preload the Vulkan loader.
7. Windows mitigated-child relaunch (unless `SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`).
8. `TryParseArguments` → eboot path + options. Merge GUI + per-game video settings.
9. `TryGetDebugServerOptions` — `--debug-server[=host:port]`; if set, create a
   `DebuggerServerHost`, start it, set `runtimeOptions.DebugHook = debugHost.Hook`.
10. `SharpEmuRuntime.CreateDefault(runtimeOptions)` → `runtime.Run(ebootPath)`;
    Ctrl-C → `VideoOutExports.NotifyHostInterrupt`.
11. Log diagnostics; exit `0` for `ORBIS_GEN2_OK`, else `4`.

## Command-line arguments

(`Program.cs:992`, `PrintUsage`)

| Argument | Purpose |
| --- | --- |
| `--strict` | Enable strict dynlib resolution. |
| `--trace-imports[=N]` | Trace up to N imports (default 32). |
| `--cpu-engine=<native\|native-only>` | Only `native`/`native-only` accepted; anything else fails. |
| `--log-level=<level>` | `Trace`/`Debug`/`Info`/`Warning`/`Error`/`Critical`/`None` (also `warn`/`fatal`). |
| `--log-file[=<path>]` | Write the full log to a file (all levels; console still filtered). |
| `--window-mode=<windowed\|borderless\|exclusive\|fullscreen>` | Window mode. |
| `--resolution=<WIDTHxHEIGHT>` | Window resolution. |
| `--display=<N>` | Display index. |
| `--refresh-rate=<HZ>` | Refresh rate. |
| `--scaling=<fit\|cover\|stretch\|integer>` | Scaling mode. |
| `--vsync=<on\|off>` | Vsync. |
| `--hdr=<auto\|on\|off>` | HDR. |
| `--debug-server[=host:port]` | Enable the live debug server. Default `127.0.0.1:5714`. |

The first positional argument is the path to a legally obtained game's
`eboot.bin`.

## Environment variables honored by Program.cs

- `SHARPEMU_MITIGATED_CHILD` (internal), `SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`.
- All the `SHARPEMU_*` flags consumed by Core (see
  [Environment variables](../reference/environment-variables.md)).

## FFmpeg runtime (MSBuild)

The CLI `.csproj` (`src/SharpEmu.CLI/SharpEmu.CLI.csproj:116-181`) defines the
FFmpeg runtime fetch. See [`bink2-bridge.md`](../bink2-bridge.md) for the
background.

- `NativeLibraryFolderName = "plugins"` — a fixed literal (runtime code in
  `SharpEmu.Libs.Media.FfmpegRuntime` uses the same
  `Path.Combine(AppContext.BaseDirectory, "plugins")`).
- `FfmpegRuntimeTag = 3b502d4` — a git release tag on
  `github.com/sharpemu/ffmpeg-core`.
- `FfmpegRuntimePackage` — per-RID zip name (`ffmpeg-windows-x64.zip`,
  `ffmpeg-linux-x64.zip`, `ffmpeg-macos-x64.zip`, `ffmpeg-macos-arm64.zip`).
- **`FetchFfmpegRuntime`** target (`BeforeTargets="Publish;Build"`):
  `DownloadFile` from the GitHub release into
  `$(BaseIntermediateOutputPath)ffmpeg-runtime/...`, then `Unzip`.
- **`BuildFfmpegRuntime`** target (`AfterTargets="Build"`): copies the
  libraries into `$(OutDir)plugins` so `dotnet build` devs get the plugins
  (otherwise AvPlayer/Bink video probing throws `NotSupportedException`).
- **`PublishFfmpegRuntime`** target (`AfterTargets="Publish"`): copies the
  libraries into `$(PublishDir)plugins`.
- `KeepLibAtrac9External` keeps `SharpEmu.LibAtrac9.dll` out of single-file
  publish (as a `plugins/` plugin).
- `RemoveNativeDebugSymbols` deletes Skia/HarfBuzz native `.pdb` files
  (>100 MB) after publish.

> A plain `dotnet build`/`dotnet publish` with no `-r` still works: it
> defaults to the host machine's RID (see `Directory.Build.props`), so it
> fetches the matching `ffmpeg-core` archive and populates `plugins` without
> extra flags. To use different FFmpeg libraries, drop them into the build or
> published `plugins` folder yourself (matching FFmpeg's naming/versioning,
> e.g. `avformat-61.dll` / `libavformat.so.61` / matching `.dylib`).

The runtime counterpart is `src/SharpEmu.Libs/Media/FfmpegRuntime.cs`
(`internal static class FfmpegRuntime`): `EnsureInitialized()` sets
`ffmpeg.RootPath = <baseDir>/plugins` and calls
`FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize()` exactly once.

## App manifest

`app.manifest` declares PerMonitorV2 DPI and Windows 10/11 OS compat (required
by Avalonia NativeControlHost). On Windows the CLI re-launches itself as a
"mitigated child" (see [CPU execution](../architecture/cpu-execution.md#the-mitigated-child-relaunch-windows-only)).

## Related

- [SharpEmu.Core guide](core.md)
- [SharpEmu.Debugger guide](debugger.md)
- [SharpEmu.GUI guide](gui.md)
- [Build configuration](../reference/build-configuration.md)
