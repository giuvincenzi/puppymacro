# PuppyMacro

**[Website](https://giuvincenzi.github.io/puppymacro/)** ·
**[Download for Windows](https://github.com/giuvincenzi/puppymacro/releases/latest/download/PuppyMacro-win-Setup.exe)** ·
**[User guide](https://giuvincenzi.github.io/puppymacro/guide/latest/)**

General-purpose input automation for Windows 10/11: loops, recorded macros, key remaps and
an in-game panel. Built with .NET 10, WPF and [WPF-UI](https://github.com/lepoco/wpfui).

Current version: **1.7.1** (see [CHANGELOG.md](CHANGELOG.md)).

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
- **Install and updates**: `PuppyMacro-win-Setup.exe` from GitHub Releases; the app checks for
  new versions and updates itself (Settings > Updates).
- **Data** in `%AppData%\PuppyMacro`, with **Export / Import** of everything as one
  `.puppymacro` file.
- Light, Dark (default) or System theme.

## Requirements

- Windows 10 or 11 (x64)
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download)
  (`winget install --id Microsoft.DotNet.SDK.10 --exact`) and GNU make.
  In WSL `make` is usually installed already; on Windows: `winget install --id ezwinports.make --exact`.
- To publish a release: the [GitHub CLI](https://cli.github.com/) (`gh`), signed in.

From WSL the commands use the Windows .NET SDK (through Windows PowerShell), because the app
runs only on Windows.

## Build and run

```
make dev       Build the current code and start it with fresh sample data. Closes a running PuppyMacro first.
make dev-keep  Same, keeping the development build's data from the last run.
make test      Run the unit tests (also on GitHub at every push).
make e2e       Run the end-to-end tests locally (they also run on GitHub). Do not use the PC meanwhile.
make build     Create the distribution in dist/: PuppyMacro-win-Setup.exe and update packages.
make docs      Build the website in site-preview/ with the guide of the current code.
make release   Publish the version in PuppyMacro.csproj to GitHub Releases.
make clean     Remove build output.
```

- **Dev** (`make dev`): a Debug build started from `PuppyMacro/bin/Debug/...`, not installed.
  An orange **Development build** strip and **DEV** in the title mark it. It keeps its own data
  in `%AppData%\PuppyMacro Dev` and never reads or changes the installed app's data: `make dev`
  recreates it with sample loops, macros and remaps, all disabled. It does not update itself
  and cannot turn on Start with Windows.
- **Prod** (`make build`, `make release`): the same code built in Release and packaged with
  [Velopack](https://velopack.io). `PuppyMacro-win-Setup.exe` installs PuppyMacro for the current user in
  `%LocalAppData%\PuppyMacro` (no administrator rights), with Start menu and desktop shortcuts and
  an entry in Settings > Apps > Installed apps. It installs the .NET 10 Desktop Runtime if missing.
  The installed app checks GitHub Releases for new versions and updates itself.

### Release

1. Bump `<Version>` in `PuppyMacro/PuppyMacro.csproj` and add its section to `CHANGELOG.md`.
2. Commit and push to `main`.
3. `make release`: the Release workflow on GitHub runs `scripts/build.ps1` (the same as
   `make build`) and creates the release `vX.Y.Z` with the CHANGELOG section as notes. Then it
   publishes the website, with the user guide of the new version added to the older ones.

## Project structure

```
PuppyMacro.sln
Makefile                           make dev / build / release / clean
scripts/                           PowerShell scripts run by the Makefile
tests/PuppyMacro.Tests/            Unit tests (xUnit)
tests/PuppyMacro.E2E/              End-to-end tests (xUnit + FlaUI)
.github/workflows/release.yml      Release workflow (make release)
.github/workflows/test.yml         Unit tests on every push
.github/workflows/e2e.yml          End-to-end tests on every push and before a release
.github/workflows/site.yml         Website publishing (after a release)
site/                              Website and user guide sources (make docs, published on release)
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
  Properties/PublishProfiles/      Folder.pubxml (used by make build)
```

See [.claude/CLAUDE.md](.claude/CLAUDE.md) and [.claude/rules/](.claude/rules/) for the architecture, threading model and conventions.

## Data

| What | Where |
|---|---|
| Settings, loops, remaps | `%AppData%\PuppyMacro\settings.json` |
| Macros (one file each) | `%AppData%\PuppyMacro\macros\{id}.json` |
| Backups made before Import | `%AppData%\PuppyMacro\backup-before-import-*.puppymacro` |
| Development build (`make dev`) | `%AppData%\PuppyMacro Dev` (same layout) |

Data from versions before 1.6 (next to the exe) is copied there on first start.

## Windows Smart App Control

On Windows 11 with **Smart App Control** on, files that are not signed and have no
reputation yet are blocked with no "Run anyway" option. Every new build is a new file, so
`make dev` builds can be blocked ("Part of this app has been blocked"). Development needs
Smart App Control off: Settings > Privacy & security > Windows Security > App & browser
control > Smart App Control settings. On Windows 11 updated to April 2026 or later it can be
turned back on without reinstalling Windows.

## Notes

- Fullscreen games must run in **Windowed Fullscreen / Borderless** for the game mode panel
  to be visible.
- If the target application runs as administrator, run PuppyMacro as administrator too.
- Mouse rows and macro clicks never click on PuppyMacro's own windows.
- Hotkeys and remap source keys are blocked from reaching other applications.

## License

[MIT](LICENSE). Third-party components: [WPF-UI](https://github.com/lepoco/wpfui) (MIT),
[Velopack](https://github.com/velopack/velopack) (MIT).
