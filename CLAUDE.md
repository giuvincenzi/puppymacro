# CLAUDE.md

Guidance for Claude Code when working on PuppyMacro.

## Working rules

- **English only in the project**: code, identifiers, comments, XAML text, UI labels,
  tooltips, messages, README, CHANGELOG.
- **Ask before changing code.** Describe what you want to change and why, wait for an
  explicit OK, then write the code. This applies to small fixes and refactors too.
- **UI changes: show the UI first.** Propose the visual result (ideally with more than one
  option) and wait for a choice before implementing. Keep the Windows 11 / Fluent look
  (WPF-UI controls, Settings-style cards, side rail).
- **Fix bugs at the root cause.** No workarounds or "patch" fixes. Explain the cause first.
- **Do not guess.** If something is uncertain (an API, a Windows behavior, a library
  version), check the source or documentation before relying on it, and say what was
  verified and what was not.
- **Do not assume the machine.** Rules in this file must hold on any contributor's PC:
  no user names, absolute paths or tools that only one machine has.
- No analogies or metaphors in explanations; be direct.
- When pointing to Windows settings or menus, use their English names.
- Do not lecture about game terms of service or anti-cheat.

## Build

- Requires a **.NET 10 SDK** and GNU make. The app runs only on Windows.
- Commands (`Makefile`, which runs the PowerShell scripts in `scripts/`):
  - `make dev`: closes a running PuppyMacro, builds Debug and starts it. Use it to try changes.
  - `make dev-reset`: deletes the dev data folder; the next `make dev` starts with the samples.
  - `make build`: the distribution in `dist/` (Velopack: `PuppyMacro-win-Setup.exe`, full
    package, `releases.win.json`) for the version in the csproj. Nothing is uploaded.
  - `make release`: runs the GitHub workflow `release.yml` (same `scripts/build.ps1`, then
    uploads the GitHub Release `vX.Y.Z`).
  - `make clean`.
- From WSL the Makefile calls Windows PowerShell (`powershell.exe`), so builds use the Windows
  .NET SDK and the app starts on Windows. The repository is then on a network path
  (`\\wsl.localhost\...`): that is why `vpk` is installed in `.tools/` instead of a dotnet tool
  manifest (manifests on network paths are refused).
- Debug builds (`App.IsDevBuild`, `#if DEBUG`) show the orange "Development build" strip and
  "DEV" in the title and tray tooltip, use their own data folder `%AppData%\PuppyMacro Dev`
  (`AppPaths`), fill it with disabled samples when empty (`Services/DevSampleData.cs`, Debug
  only), never touch the Start with Windows Run entry and skip the pre-1.6 data migration.
  Release builds never do any of this. Keep the samples working when models change.
- Smart App Control (Windows 11) blocks unsigned builds it has no reputation for; development
  needs it off.
- Always build after changes and fix all errors and warnings you introduced.

## Releases

- Version lives only in `PuppyMacro/PuppyMacro.csproj` (`<Version>`); the title bar and
  About read it at runtime (`App.DisplayTitle`). Semantic versioning: features = minor,
  fixes = patch.
- Each release: bump the version, update `CHANGELOG.md` (the release notes are its
  `## X.Y.Z` section; the workflow fails without it) and, if behavior changed, `README.md`.
  Then push and `make release`.
- The installed app updates from GitHub Releases (`Services/UpdateService.cs`,
  `MainWindow.Updates.cs`): check at startup and every 12 hours, bar at the top + dot on
  Settings while an update is available, Update stops all loops and macros, downloads,
  exits normally and Velopack restarts the new version.
- Velopack: `App.Main` runs `VelopackApp.Build().Run()` before WPF starts (App.xaml is a
  Page, `StartupObject` is `PuppyMacro.App`). Keep the `Velopack` package version and
  `$VpkVersion` in `scripts/build.ps1` equal.

## Stack

- .NET 10 (`net10.0-windows`, `win-x64`, framework-dependent), WPF, **WPF-UI 4.1.0**
  (`FluentWindow`, `TitleBar`, `CardExpander`, `NumberBox`, `ToggleSwitch`, `SymbolIcon`...).
- **Velopack 1.2.161** (installer and updates). No other NuGet packages. Win32 through
  P/Invoke in `Native/NativeMethods.cs`.
- Nullable reference types on, implicit usings off (explicit `using`s).

## Architecture

### Threads
| Thread | What runs there |
|---|---|
| UI (WPF dispatcher) | Windows, view models, `SoundService`, clipboard (`ClipboardPaster`) |
| `InputThread` | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks + message loop, desktop-switch WinEvent hook |
| `Injector` | Input sent on behalf of the hook (remap targets, menu mask key), in order |
| One per running loop row (`ActionRunner`) / per running macro (`MacroRunner`) | Timing with `PreciseTimer` (high-resolution waitable timer) and `SendInput` |

Rules:
- **Never block the input thread** and never touch WPF objects from it. The hook only
  decides (block or not) and posts work. Windows silently skips, then removes, hooks that
  are too slow.
- `LoopEngine` state is guarded by one lock (`_sync`). The hook reads an immutable
  `EngineSnapshot` (cloned loops, macros, enabled remaps, global hotkeys). **After any change
  to settings, loops, macros or remaps, the UI must publish a new snapshot**
  (`MainWindow.Save()` and `SaveMacro()` already do it).
- UI notifications from the engine go through `Dispatcher.InvokeAsync`.
- Clicks on the game mode panel are matched against row rectangles (`PanelTarget`, physical
  pixels) published by the UI every 300 ms while game mode is on.

### Input model
- Keys and mouse buttons are Windows **virtual-key codes** everywhere (mouse: 1 LButton,
  2 RButton, 4 MButton, 5 XButton1, 6 XButton2). Display names: `KeyNames`.
- `HotkeyBinding` = main key + Ctrl/Alt/Shift/Win. Matching is **exact**.
- **Modifier state comes only from physical key events** (`ModifierTracker`, updated by the
  hook). Injected events (`LLKHF_INJECTED`) and AltGr's fake left Ctrl (scan code flag
  0x200) never change it. Do not use `GetAsyncKeyState` for this.
- Everything PuppyMacro sends carries `InputSender.Signature` in `dwExtraInfo`; the hook
  ignores those events, so loops/macros/remaps never trigger hotkeys or recording.
- Before a loop sends a key, physically held modifiers are released (so the target gets
  the plain key) and are **not** pressed again (re-pressing caused stuck keys).
- When a blocked key is pressed with Alt or Win held, a mask key (VK 0xE8) is sent so no
  menu / Start opens.
- Left and right click can never be hotkeys (capture lets them pass so the UI stays
  clickable); they can be remap sources only for a specific app. This is a deliberate
  design decision: keep it.
- Routing order in `LoopEngine.OnKey`: modifiers → key capture → record hotkey/recording →
  key-up handling (swallowed ups, remap ups, Hold release) → auto-repeat → global hotkeys,
  loop and macro hotkeys (skipped while `HotkeysSuspended`) → remaps.

### Features map
- Loops: `Models/AppSettings.cs` (`LoopDefinition`, `LoopAction`), `Services/ActionRunner.cs`,
  `LoopEditorWindow`.
- Macros: `Models/MacroModels.cs`, `Services/MacroBuilder.cs` (raw events → actions, path
  simplification), `MacroRunner.cs`, `MacroLibrary.cs` (one JSON per macro),
  `RecordingSession.cs`, `MacroEditorWindow`, `MacroActionWindow`.
- Remap: `RemapDefinition`, `LoopEngine.FindRemap`, `RemapEditorWindow`.
- Game mode: `Views/GameModePanel`, `GameModeWindow` (click-through, no-activate),
  `PlacementWindow`.
- Tray and startup: `Services/TrayIcon.cs` (Shell_NotifyIcon), `StartupService.cs`
  (HKCU Run key, `--tray`). Uninstall removes the Run key (Velopack hook in `App.Main`).
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs`.

### Data and compatibility
- Data folder: `%AppData%\PuppyMacro` (`AppPaths`; `PuppyMacro Dev` for Debug builds).
  `settings.json` + `macros\{id}.json`.
- `SettingsStore.Sanitize` validates and **migrates** old files. When the settings format
  or a default changes for existing users, bump `AppSettings.CurrentSchemaVersion` and add
  the migration there. Never break existing user data.
- Export / Import: `.puppymacro` zip (`BackupService`); Import keeps a backup and restarts
  the app (`--restart`).

## Known limits and decisions

- No overlay inside exclusive-fullscreen games (would need DLL injection): game mode needs
  windowed fullscreen / borderless.
- If the target app runs as administrator, PuppyMacro must run as administrator too.
- Start with Windows uses the user Run key, so it cannot start elevated.
- Postponed idea: GitHub login, sync of user data (secret gist) and a public "store" of
  macros (issues in a public repository). Not started.
