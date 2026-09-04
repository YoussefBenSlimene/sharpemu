<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.Logging

The shared logging pipeline. Every project in the solution uses this — it is
the lowest-level project in the dependency graph. Two static surfaces:
`SharpEmuLog` (configuration, sink access) and per-category
`SharpEmuLogger` instances.

`src/SharpEmu.Logging/SharpEmu.Logging.csproj` has no project references and
no NuGet packages.

## File map

| File | Purpose |
| --- | --- |
| `SharpEmuLog.cs` | `public static class` — the facade. `MinimumLevel`, `Sink`, `Configure(...)`, `Shutdown()`, `For(category) → SharpEmuLogger`, `TryParseLevel(...)`, `Write(...)`. Resolves the default level from `SHARPEMU_LOG_LEVEL` (default `Info`) and the default sink from `SHARPEMU_LOG_FILE` + `SHARPEMU_LOG_NO_COLOR`. |
| `SharpEmuLogger.cs` | `public sealed class SharpEmuLogger(string category)`. The fluent API: `Trace`/`Debug`/`Info`/`Warn`/`Warning`/`Error` (each with optional `Exception` and `[CallerFilePath]`/`[CallerLineNumber]`/`[CallerMemberName]`). |
| `LogLevel.cs` | The level enum (`Trace`/`Debug`/`Info`/`Warning`/`Error`/`Critical`/`None`). |
| `LogEntry.cs` | `readonly struct` for a single log entry (`DateTimeOffset, LogLevel, Category, Message, Exception, source file/line/member`). Passed by `in`. |
| `ISharpEmuLogSink.cs` | The sink contract: `void Write(in LogEntry entry);`. |
| `ConsoleLogSink.cs` | Console sink (with optional colors, optional timestamp). |
| `FileLogSink.cs` | File sink (append, optional timestamp). |
| `CompositeLogSink.cs` | Composes multiple sinks. |
| `HostSystemInfo.cs` | A logged block of host info (OS, runtime, CPU, GPU) shown at startup. |
| `BuildInfo.cs` | Build provenance surfaced as a touchHLE-style banner at the top of the log (commit SHA, branch, repo, workflow run URL, configuration, `IsOfficialRelease`). Populated from `[AssemblyMetadata]` injected by `Directory.Build.props` from GitHub Actions env vars (`SharpEmu.BuildSha`, `SharpEmu.BuildBranch`, `SharpEmu.BuildRepository`, `SharpEmu.BuildServerUrl`, `SharpEmu.BuildRunId`, `SharpEmu.BuildConfiguration`). |

## How it works

1. Call `SharpEmuLog.For("MyCategory")` once (cached in
   `ConcurrentDictionary<string, SharpEmuLogger>`); keep the logger.
2. Use the fluent methods. Each entry's source file/line/member name are
   captured by the C# `[CallerXxx]` attributes.
3. `Write` (`SharpEmuLog.cs:148`) constructs a `LogEntry` and dispatches it
   to the active `Sink`. Entries below `_minimumLevel` are dropped, unless a
   `SHARPEMU_LOG_FILE` sink is active (in which case the file gets every
   level and only the console is filtered — see `MinimumLevelFilterSink`).
4. Call `SharpEmuLog.Shutdown()` at process exit to flush the file sink.

## Environment variables

- `SHARPEMU_LOG_LEVEL` — default level (`Trace`/`Debug`/`Info`/`Warning`/
  `Error`/`Critical`/`None`; also accepts `warn`/`fatal`). Default `Info`.
- `SHARPEMU_LOG_FILE` — if set to a writable path, a `FileLogSink` is added
  that captures every level; the console is filtered by the minimum level.
- `SHARPEMU_LOG_NO_COLOR` — disable colored console output (`1`/`true`/`yes`
  /`on`).

## Usage conventions

- **One logger per category.** The category is typically the subsystem tag
  ("VMEM", "HLE", "Agc", "Audio", etc.). Grep for
  `SharpEmuLog.For("...")` to see the existing categories.
- **Do not use string interpolation in the hot path.** `SharpEmuLogger.Info`
  takes a `message` string; pass an already-formatted message or use a guard
  on `IsEnabled(level)` to skip the formatting cost.
- **Add the SPDX header** to every new file (per [REUSE compliance](../guides/contributing.md#reuse-compliance)).
- For diagnostics that are gated by an env var, follow the same
  "default-off, env-on" pattern as `GuestWriteWatch` / `GuestImageWriteTracker`
  / `SdlHostAudio` — the env var is read at the call site or in a static
  initializer, and the diagnostic path is bypassed when unset.

## Build provenance

`Directory.Build.props` (`Directory.Build.props:47-56`) injects
`AssemblyMetadata` items for `SharpEmu.BuildConfiguration`,
`SharpEmu.BuildSha`, `SharpEmu.BuildBranch`, `SharpEmu.BuildRef`,
`SharpEmu.BuildEventName`, `SharpEmu.BuildRepository`,
`SharpEmu.BuildServerUrl`, `SharpEmu.BuildRunId` from the matching GitHub
Actions env vars. `BuildInfo` reads them at type-init time, computes
`IsOfficialRelease` (Release configuration, canonical repo, push to `main` or
manual dispatch), and trims the SHA to the first 7 chars. The CLI calls
`BuildInfo.WriteBanner(...)` at startup to print a touchHLE-style banner
(commit/branch/repo/workflow run URL) at the top of the log.

## Related

- [Environment variables](../reference/environment-variables.md) — the full
  list of `SHARPEMU_*` knobs.
- [`guest-write-watch.md`](../guest-write-watch.md) — an example of an
  env-gated diagnostic that uses `SharpEmuLog`.
