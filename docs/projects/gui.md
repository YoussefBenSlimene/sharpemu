<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.GUI

The Avalonia-based desktop frontend. Hosted by the `SharpEmu` executable when
it is started without command-line arguments (the CLI calls `GuiLauncher.Run()`).

`src/SharpEmu.GUI/SharpEmu.GUI.csproj` is a class library (`AllowUnsafeBlocks=true`)
referenced by `SharpEmu.CLI`. It depends on `SharpEmu.LibAtrac9`,
`SharpEmu.Core`, `SharpEmu.Libs`, and `SharpEmu.Logging`. NuGet: `Avalonia`
(+ `Avalonia.Desktop`, `Avalonia.Fonts.Inter`, `Avalonia.Themes.Fluent`,
`Tmds.DBus.Protocol`). `InternalsVisibleTo` includes `SharpEmu.Libs.Tests`.

> **Games are never run in the GUI process.** Guest virtual memory is
> fixed-address and cannot be reliably reused while guest-created host threads
> are still alive, so the GUI owns launch and session controls only and spawns
> the CLI as an isolated child per game (`EmulatorProcess`).

## Entry point

`src/SharpEmu.GUI/GuiLauncher.cs:12` — `static class GuiLauncher` with `Run()`.
`BuildAvaloniaApp()` configures `App` + `UsePlatformDetect` + `WithInterFont` +
`LogToTrace`, and starts with `StartWithClassicDesktopLifetime`. Crash logs go
to `gui-crash.log` next to the executable.

## File map

### Root / launch / lifecycle

| File | Purpose |
| --- | --- |
| `App.axaml.cs` | The Avalonia `Application`. |
| `MainWindow.axaml.cs` | The main window. |
| `MainWindow.GameOptions.cs` | Partial — the game-options UI. |
| `GuiLauncher.cs` | Entry point (`Run`). |
| `EmulatorProcess.cs` | `internal sealed class` — owns an isolated emulator process. Spawns the CLI child with the same mitigation flags (`ProcThreadAttributeMitigationPolicy`, CFG/CET off), captures stdout/stderr via `OutputReceived`, kills on close via a Job object (`JobObjectLimitKillOnJobClose`). `HostStopExitCode = -2`. |
| `ConsoleWindow.cs` | The emulator output console. |
| `GuiConsoleMirror.cs` | Mirrors the child's console output. |
| `LogLine.cs` | A log line view model. |
| `Updater.cs` | Self-update logic (applied by the CLI's `Updater.TryApply`). |
| `DiscordRichPresence.cs` | Discord Rich Presence integration. |
| `SndPreviewPlayer.cs` | Sound preview player. |
| `WindowMaximizeButtonState.cs` | Custom window chrome state. |

### Game library

| File | Purpose |
| --- | --- |
| `GameEntry.cs` | A game entry view model (title, path, icon, ...). |
| `GameLibraryPath.cs` | A library folder path. |
| `GameLibraryCache.cs` | Cached library state. |
| `GameLibraryWatcher.cs` | Filesystem watcher for library folders. |
| `GameLibraryReconciler.cs` | Reconciles the cache against the filesystem. |
| `LibraryTile.cs` / `LibraryTileCollection.cs` | The library grid tiles. |
| `AddFolderTile.cs` | The "add folder" tile. |
| `GameLibraryWatcher`/`Reconciler`/`Cache` | Covered by tests: `GameLibraryWatcherTests`, `GameLibraryReconcilerTests`, `LibraryTileCollectionTests`, `GameLibraryCache`/`PerGameSettingsTests`. |

### Settings

| File | Purpose |
| --- | --- |
| `GuiSettings.cs` | Persisted GUI settings. |
| `PerGameSettings.cs` | Per-game overrides (video options, ...). Covered by `PerGameSettingsTests`. |
| `HostDisplayOptions.cs` | Host display options (window mode, resolution, refresh rate, scaling, vsync, HDR). Covered by `HostDisplayOptionsTests` + `HostDisplayOptionsTests`. |
| `SettingRow.cs` | A settings row control. |
| `Controls/Settings/` | The settings UI controls. |

### Theming / localization / assets

| File | Purpose |
| --- | --- |
| `Themes/` / `Themes/Styles/` / `Themes/Templates/` | Avalonia theming (Fluent + custom styles/templates). Window chrome covered by `WindowChromeTests`. |
| `Languages/*.json` | Localized strings (embedded as `Languages.<filename>`). Covered by `LocalizationTests`. |
| `LocalizedChoice.cs` | A localized choice view model. |
| `Localization.cs` | The localization service. |
| `Assets/Fonts/MaterialSymbolsRounded-400.ttf` | The icon font. |
| `Assets/...` (SharpEmu.ico, github.png, discord.png, update-icon.png, commit-icon.png, pic0.png) | Embedded resources. |
| `LICENSES/Apache-2.0.txt` | Copied to output for license attribution (Avalonia is MIT-licensed but ships Apache-2.0 components). |

## Public API surface

The GUI is hosted by the CLI; the only public entry is `GuiLauncher.Run()`.
`EmulatorProcess` is `internal`.

## Dependencies

- **Avalonia 12.1.0** — the UI framework. `Avalonia.Desktop` for
  platform-specific backends, `Avalonia.Fonts.Inter` for the default font,
  `Avalonia.Themes.Fluent` for the theme.
- `SharpEmu.Core`/`SharpEmu.Libs`/`SharpEmu.Logging` — to surface
  `BuildInfo` in the title bar and to read game metadata (`pic0.png`,
  `param.json`) for library tiles.
- `SharpEmu.LibAtrac9` — for sound previews (`SndPreviewPlayer`).
- **SDL3** (transitive via `SharpEmu.Libs`) — window/event/input for the
  emulator child process.

## Gotchas

- **Never run a game in-process.** Always go through `EmulatorProcess`, which
  spawns the CLI child and captures its output. The CLI child runs with the
  same mitigation flags as the bare CLI (CFG/CET off).
- **Per-game settings** are merged into `HostVideoOptions` at the CLI side
  (`LoadConfiguredVideoOptions` in `Program.cs`); the GUI hands them through
  the child's command line.
- **Localization** strings live in `Languages/*.json` (embedded). Add a
  language by adding a JSON file; the `LocalizationTests` verify the keys.
- **License attribution** (`Apache-2.0.txt`) is copied to the output directory
  for compliance; don't remove it.

## Related

- [SharpEmu.CLI guide](cli.md) — the executable that hosts this GUI.
- [SharpEmu.Logging guide](logging.md) — `BuildInfo` shown in the title bar.
- [SharpEmu.Libs guide](libs.md) — `VideoOut`/`HostDisplayOptions` consumed
  by the GUI.
