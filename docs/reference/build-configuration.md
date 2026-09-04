<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Build Configuration

This page documents the MSBuild / NuGet configuration files at the root of
the repository and the GitHub Actions workflow.

## `global.json`

```json
{
    "sdk": {
        "version": "10.0.103",
        "rollForward": "latestFeature"
    }
}
```

Pins the .NET SDK to `10.0.103` with `latestFeature` roll-forward — a
slightly newer SDK within the same feature band is acceptable.

## `nuget.config`

```xml
<configuration>
  <config>
    <add key="globalPackagesFolder" value=".packages" />
 </config>
  <packageSources>
    <clear />
    <add key="Nuget" value="https://api.nuget.org/v3/index.json" />
 </packageSources>
  <packageSourceMapping>
    <packageSource key="Nuget">
      <package pattern="*" />
   </packageSource>
 </packageSourceMapping>
</configuration>
```

Keeps all NuGet packages inside the repo (`.packages/`) — useful for offline
builds and reproducibility. Only the official nuget.org feed is allowed.

## `Directory.Build.props`

Applies to every project in the solution. Key settings:

- `<TargetFramework>net10.0</TargetFramework>`.
- `<ImplicitUsings>enable</ImplicitUsings>`,
  `<Nullable>enable</Nullable>`.
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` (off in
  Release).
- `<SharpEmuVersion>0.0.3-release.3</SharpEmuVersion>` — the only place to
  bump the version.
- `<RepoRoot>$(MSBuildThisFileDirectory</RepoRoot>` — base for all output
  paths.

### Output paths

All artifacts land under `artifacts/`:

- `artifacts/obj/<ProjectName>/` — `BaseIntermediateOutputPath`.
- `artifacts/bin/<Configuration>/<TargetFramework>/<RuntimeIdentifier>/` —
  `BaseOutputPath` + the standard `Append*` suffixes.
- `artifacts/publish/<ProjectName>/<Configuration>/<TargetFramework>/<RuntimeIdentifier>/`
  — `PublishDir`.

### Host RID default

When `dotnet build`/`dotnet publish` is invoked **without** `-r`, the CLI
defaults to the host machine's RID so the FFmpeg runtime archive matches
without extra flags:

```xml
<_HostRidOSPrefix Condition="'$(RuntimeIdentifier)' == '' And '$(MSBuildProjectName)' == 'SharpEmu.CLI' And $([MSBuild]::IsOSPlatform('Windows'))">win</_HostRidOSPrefix>
<_HostRidOSPrefix Condition="'$(_HostRidOSPrefix)' == '' And $([MSBuild]::IsOSPlatform('Linux'))">linux</_HostRidOSPrefix>
<_HostRidOSPrefix Condition="'$(_HostRidOSPrefix)' == '' And $([MSBuild]::IsOSPlatform('OSX'))">osx</_HostRidOSPrefix>
<_HostRidArch Condition="'$(_HostRidOSPrefix)' != '' And '$(ProcessArchitecture)' == 'Arm64'">arm64</_HostRidArch>
<_HostRidArch Condition="'$(_HostRidArch)' == ''">x64</_HostRidArch>
<RuntimeIdentifier Condition="'$(_HostRidOSPrefix)' != ''">$(_HostRidOSPrefix)-$(_HostRidArch</RuntimeIdentifier>
```

Passing `-r <rid>` overrides this normally. See
[CLI guide §FFmpeg runtime](../projects/cli.md#ffmpeg-runtime-msbuild) for
how this drives the FFmpeg fetch.

### Build provenance

```xml
<AssemblyMetadata Include="SharpEmu.BuildConfiguration" Value="$(Configuration)" />
<AssemblyMetadata Condition="'$(GITHUB_SHA)' != ''" Include="SharpEmu.BuildSha" Value="$(GITHUB_SHA)" />
<AssemblyMetadata Condition="'$(GITHUB_REF_NAME)' != ''" Include="SharpEmu.BuildBranch" Value="$(GITHUB_REF_NAME)" />
... (Ref, EventName, Repository, ServerUrl, RunId)
```

These `[AssemblyMetadata]` items are read at type-init time by
`BuildInfo` (`src/SharpEmu.Logging/BuildInfo.cs`) to produce the build-provenance
banner at the top of the log.

### Release configuration overrides

```xml
<GenerateDocumentationFile>false</GenerateDocumentationFile>
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
```

Release builds skip XML doc generation and PDB emission.

## `Directory.Packages.props`

Centralized NuGet version management — every project in the solution
references these packages by name only (`<PackageReference Include="Iced" />`)
without a `Version` attribute.

| Package | Version | Used by |
| --- | --- | --- |
| `Avalonia` | 12.1.0 | GUI |
| `Avalonia.Desktop` | 12.1.0 | GUI |
| `Avalonia.Fonts.Inter` | 12.1.0 | GUI |
| `Avalonia.Themes.Fluent` | 12.1.0 | GUI |
| `FFmpeg.AutoGen` | 7.1.1 | Libs (media/Bink) |
| `Iced` | 1.21.0 | Core (CPU disasm) |
| `Microsoft.Build.Framework` | 17.14.8 | SourceGenerators (task) |
| `Microsoft.CodeAnalysis.Analyzers` | 3.11.0 | SourceGenerators (analyzers) |
| `Microsoft.CodeAnalysis.CSharp` | 4.12.0 | SourceGenerators |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | All test projects |
| `NLayer` | 1.14.0 | Libs (MP3) |
| `ppy.SDL3-CS` | 2026.629.0 | Libs + HLE (SDL3 C# bindings) |
| `Silk.NET.Vulkan` | 2.23.0 | Libs (Vulkan renderer) |
| `Silk.NET.Vulkan.Extensions.EXT` | 2.23.0 | Libs |
| `Silk.NET.Vulkan.Extensions.KHR` | 2.23.0 | Libs |
| `Tmds.DBus.Protocol` | 0.94.1 | GUI (Avalonia.Desktop transitive; pinned — Avalonia 12 requires 0.94.1+) |
| `xunit` | 2.9.3 | All test projects |
| `xunit.runner.visualstudio` | 3.1.1 | All test projects |

## `SharpEmu.slnx`

The new XML-format solution file. Two folders:

- `/src/` — the 13 source projects.
- `/tests/` — the 4 test projects.

The order in `/src/` matters for the FFmpeg-runtime publish path
(`FfmpegRuntimeTag`, `FetchFfmpegRuntime`/`PublishFfmpegRuntime`/
`BuildFfmpegRuntime` targets live in `SharpEmu.CLI.csproj`).

## `SharpEmu.CLI.csproj` — notable properties

| Property | Value | Why |
| --- | --- | --- |
| `<OutputType>WinExe</OutputType>` | `WinExe` | GUI-friendly on Windows; CLI re-attaches to the parent console when launched from a terminal. |
| `<AssemblyName>SharpEmu</AssemblyName>` | `SharpEmu` | The shipped binary is just `SharpEmu.exe`/`SharpEmu`. |
| `<RuntimeIdentifiers>win-x64;linux-x64;osx-x64;osx-arm64</RuntimeIdentifiers>` | | All published targets. |
| `<SelfContained>true</SelfContained>` | | Single-file publish bundles the runtime. |
| `<PublishSingleFile>true</PublishSingleFile>` | | One executable. |
| `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>` | | Required for SDL3/MoltenVK/FFmpeg native libs. |
| `<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>` | | Smaller artifact. |
| `<TieredPGO>true</TieredPGO>` | | Profile-guided optimization. |
| `<ServerGarbageCollection>false</ServerGarbageCollection>` | | Server GC reserves a large heap per logical processor that can collide with the fixed PS5 image bases. |
| `<ConcurrentGarbageCollection Condition="$(RuntimeIdentifier.StartsWith('osx'))">false</ConcurrentGarbageCollection>` | | Background GC's `FlushProcessWriteBuffers` → `thread_get_register_pointer_values` stalls indefinitely under Rosetta 2. |

## `.editorconfig`

Encoding, indentation, file-ending rules. Most editors apply it
automatically; the maintainers recommend VS Code with **C#** + **C# Dev Kit**.

Key rules (see the file for the full set):

- **4 spaces** for indentation in C#.
- **2 spaces** for indentation in XML/YAML/`.props`/`.csproj`/`.targets`.
- Single trailing newline in every file.

## CI

`.github/workflows/workflow.yml` — the "Build and Release" workflow.

### Triggers

- `push` to any branch (paths-ignore: docs/images).
- `push` of tags matching `v*`.
- `pull_request` to `main`.
- `workflow_dispatch`.

### Jobs

1. **`init`** — checkout + compute workflow variables (short SHA, safe ref,
   version from `Directory.Build.props`, release tag/name). The version is
   parsed from `Directory.Build.props`; if missing/empty the job fails.
2. **`reuse`** — REUSE compliance (`fsfe/reuse-action@v6`).
3. **`build`** (windows-latest) — restore, build, run tests, validate
   synthetic shaders (`SharpEmu.Tools.ShaderDump`), publish `win-x64`, upload
   artifact.
4. **`build-posix`** (matrix: ubuntu-latest + macos-latest) — restore, build,
   run tests. On `linux-x64`: build SPIRV-Tools pinned to
   `SPIRV_TOOLS_COMMIT=0539c81f69a3daeb706fd3477dca61435b475156` (with
   `SPIRV_HEADERS_COMMIT=ad9184e76a66b1001c29db9b0a3e87f646c64de0`),
   `SPIRV_TOOLS_VERSION=v2026.2`, target env `vulkan1.2`; validate the
   generated SPIR-V with `spirv-val`. Publish `linux-x64` and `osx-x64`;
   stage MoltenVK next to the macOS publish (`scripts/fetch-macos-moltenvk.sh`).
5. **`release`** — runs only on tag pushes matching `v*`. Downloads the three
   artifacts (`sharpemu-win-x64-<sha>`, `sharpemu-linux-x64-<sha>`,
   `sharpemu-osx-x64-<sha>`), packages them as
   `sharpemu-<version>-<rid>.{zip|tar.gz}`, builds release notes from
   `git log` since the previous tag, and creates a GitHub Release with
   `gh release create`.

> The CI also pins the FFmpeg runtime tag (`SharpEmu.CLI.csproj`'s
> `FfmpegRuntimeTag`) — it must agree with the `FFmpeg.AutoGen` version
> (both need the same FFmpeg ABI). When bumping `FFmpeg.AutoGen` in
> `Directory.Packages.props`, update `FfmpegRuntimeTag` in the same PR.

### Other workflows

- `.github/workflows/pr-build-links.yml` — posts a PR comment with the
  Actions build URLs.
- `.github/workflows/notify-site.yml` — notifies the website on release.

### Issue / PR templates

- `.github/pull_request_template.md` — the mandatory PR template.
- `.github/ISSUE_TEMPLATE/bug_report.yml` — bug reports.
- `.github/ISSUE_TEMPLATE/game-compatibility.yml` — game compatibility
  reports.
- `.github/ISSUE_TEMPLATE/config.yml` — ties the templates together.

## Related

- [CLI guide §FFmpeg runtime](../projects/cli.md#ffmpeg-runtime-msbuild)
- [Environment variables](environment-variables.md)
- [`release-use.md`](../release-use.md)
