# PuppyMacro

General-purpose input automation for Windows 10/11: loops, recorded macros, key remaps and
an in-game panel. Built with .NET 10, WPF and [WPF-UI](https://github.com/lepoco/wpfui).

Current version: **1.6.0** (see [CHANGELOG.md](CHANGELOG.md)).

## Features

- **Loops**: one or more rows, each on its own timer (10 ms to 24 h):
  - **Key**: presses a key or mouse button, or keeps it **held down** while the loop runs.
  - **Text**: pastes a text through the clipboard (Ctrl+V), optionally with Enter before
    and/or after. The previous clipboard content is restored and kept out of Win+V history.
- **Macros**: record keys, clicks, scroll and (optionally) mouse movement, or build them by
  hand, then edit the list of actions (Press key, Key down/up, Click, Mouse button down/up,
  Move to instantly or smoothly with Pick on screen, Scroll, Paste text, recorded Move
  paths). Every action has its own delay; Press key and Click can repeat N times. Repeat
  once, in a loop or N times, at ×0.25 to ×4 speed.
- **Remap**: a key or mouse button sends another key, mouse button or combination, in all
  apps or only while a specific app (.exe) is in front.
- **Activation**: optional hotkey per loop or macro (single key, mouse button or combination
  with Ctrl, Alt, Shift, Win), **Toggle** or **Hold**; Start/Play buttons; global
  **Stop all** (F10), **Game mode** (F11) and **Record** (F8) hotkeys.
- **Game mode**: a small always-on-top, click-through panel with the enabled loops and
  macros; items can be clicked to start or stop them without the game losing focus.
  Opacity and position are configurable (Position on screen).
- **Sounds**: optional start/stop sound per loop or macro, with volume.
- **System tray**: closing keeps PuppyMacro running in the tray; **Start with Windows**.
- **Data** in `%AppData%\PuppyMacro`, with **Export / Import** of everything as one
  `.puppymacro` file.
- Light, Dark (default) or System theme.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build:
  `winget install --id Microsoft.DotNet.SDK.10 --exact`

## Build and run

```powershell
git clone <repository-url>
cd puppymacro

# Development build and run
dotnet build PuppyMacro\PuppyMacro.csproj
dotnet run --project PuppyMacro\PuppyMacro.csproj

# Local publish: exe + dll files in publish\ (works with Smart App Control on)
dotnet publish PuppyMacro\PuppyMacro.csproj -c Release -p:PublishProfile=Folder

# Release publish: one PuppyMacro.exe in release\ (for distribution / code signing)
dotnet publish PuppyMacro\PuppyMacro.csproj -c Release -p:PublishProfile=SingleFile
```

`build.cmd` and `build-release.cmd` run the two publish commands. The app is
framework-dependent: the PC needs the .NET 10 Desktop Runtime (included in the SDK).

## Project structure

```
PuppyMacro.sln
build.cmd / build-release.cmd      Publish shortcuts (Folder / SingleFile profiles)
PuppyMacro/
  App.xaml(.cs)                    Startup: single instance, data migration, theme, tray start
  MainWindow.xaml(.cs)             Side rail: Loops, Macros, Remap, Settings; game mode; tray
  LoopEditorWindow, MacroEditorWindow, MacroActionWindow, RemapEditorWindow
  GameModeWindow, PlacementWindow, PickPointWindow, RecordPromptWindow, RecordingBarWindow
  Models/      AppSettings (settings.json), LoopDefinition, MacroDefinition, RemapDefinition
  Services/    InputThread + InputHook (low-level hooks), LoopEngine (routing), runners,
               InputSender (SendInput), recording, sounds, storage, tray, startup
  Views/       View models and the shared game mode panel
  Native/      Win32 interop
  Assets/      Icon and the built-in sounds
  Properties/PublishProfiles/      Folder.pubxml, SingleFile.pubxml
```

See [CLAUDE.md](CLAUDE.md) for the architecture, threading model and conventions.

## Data

| What | Where |
|---|---|
| Settings, loops, remaps | `%AppData%\PuppyMacro\settings.json` |
| Macros (one file each) | `%AppData%\PuppyMacro\macros\{id}.json` |
| Backups made before Import | `%AppData%\PuppyMacro\backup-before-import-*.puppymacro` |

Data from versions before 1.6 (next to the exe) is copied there on first start.

## Windows Smart App Control

On Windows 11 with **Smart App Control** on, unsigned files from the internet are blocked
with no "Run anyway" option:

- Scripts (`build.cmd`, `build-release.cmd`) from a downloaded ZIP: drag them into a
  PowerShell window and press Enter, or unblock the ZIP before extracting it
  (right-click > Properties > Unblock). Builds from a `git clone` are not affected.
- The single-file exe in `release\` is usually blocked; the `publish\` build starts normally.

Smart App Control can also be turned off (Settings > Privacy & security > Windows Security >
App & browser control > Smart App Control settings); on Windows 11 updated to April 2026 or
later it can be turned back on without reinstalling Windows.

## Notes

- Fullscreen games must run in **Windowed Fullscreen / Borderless** for the game mode panel
  to be visible.
- If the target application runs as administrator, run PuppyMacro as administrator too.
- Mouse rows and macro clicks never click on PuppyMacro's own windows.
- Hotkeys and remap source keys are blocked from reaching other applications.
