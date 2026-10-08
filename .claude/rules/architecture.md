---
paths:
  - "PuppyMacro/**"
---

# Architecture

## Stack

- .NET 10 (`net10.0-windows`, `win-x64`, framework-dependent), WPF, **WPF-UI 4.1.0**
  (`FluentWindow`, `TitleBar`, `CardExpander`, `NumberBox`, `ToggleSwitch`, `SymbolIcon`...).
- **Velopack 1.2.161** (installer and updates) and **Microsoft.Web.WebView2** (the macro editor's
  Code view). No other NuGet packages. Win32 through P/Invoke in `Native/NativeMethods.cs`.
- **Monaco editor 0.52.2** (MIT, the editor of VS Code), only the files JSON needs, in
  `Assets/Monaco` with its license; `Assets/CodeEditor` is the page around it. To update it, copy
  the same files from the npm package's `min/vs` and keep the version here. The loader's `vs`
  path must be an absolute address: the JSON worker (suggestions, schema checks, Format) resolves
  it from its own context, where a relative path fails silently. "No problems." shows only once
  the worker runs, so the Code view end-to-end test catches a broken worker.
- Nullable reference types on, implicit usings off (explicit `using`s).

## Repository layout

```
PuppyMacro.sln, Makefile, global.json
scripts/                        PowerShell scripts run by the Makefile
.github/workflows/              test.yml, e2e.yml, release.yml, site.yml
site/                           website and user guide sources (docs.md)
tests/PuppyMacro.Tests/         unit tests;  tests/PuppyMacro.E2E/  end-to-end tests (testing.md)
PuppyMacro/
  App.xaml(.cs)                 Main (Velopack), single instance, data migration, theme, tray start
  MainWindow.xaml(.cs)          side rail: Loops, Macros, Remap, Settings; overlay mode; tray
  MainWindow.Updates.cs         update bar, Settings dot and Updates card
  LoopEditorWindow, MacroEditorWindow, MacroActionWindow, RemapEditorWindow
  OverlayPanelWindow, FloatingButtonWindow, PlacementWindow, PickPointWindow, RecordPromptWindow,
  RecordingBarWindow
  Models/                       AppSettings (settings.json), loops, macros, remaps
  Services/                     input thread and hooks, LoopEngine, runners, InputSender,
                                recording, sounds, storage, tray, startup, updates
  Views/                        view models, overlay panel, macro Code view, WindowFit
  Native/                       Win32 interop
  Assets/                       icon, built-in sounds, Code view page (CodeEditor) and Monaco
  Properties/PublishProfiles/   Folder.pubxml (used by scripts/build.ps1)
```

## Threads

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
- Clicks on the overlay panel and on the floating buttons are matched against rectangles
  (`PanelTarget`, physical pixels) published by the UI every 300 ms while overlay mode is on.
  Floating buttons are always clickable; panel rows only with `ShowOverlayPanel` and
  `ClickItemsInPanel`.

## Features map

- Loops: `Models/AppSettings.cs` (`LoopDefinition`, `LoopAction`), `Services/ActionRunner.cs`,
  `LoopEditorWindow`.
- Macros: `Models/MacroModels.cs`, `Services/MacroBuilder.cs` (raw events → actions, path
  simplification), `MacroRunner.cs`, `MacroLibrary.cs` (one JSON per macro),
  `RecordingSession.cs`, `MacroEditorWindow`, `MacroActionWindow`, `GroupNameWindow`.
  The editor's operations (group, ungroup, copy / paste, duplicate, drag, Alt+Up / Alt+Down)
  live in `Services/MacroEditList.cs`, without UI, with unit tests; the window only maps the
  list to rows and group headers. Test action: `Services/ActionTestSession.cs` (moves the
  editor off screen, hides the main window, focuses the window behind, waits 200 ms) plays
  `MacroDefinition.ForTest` through `LoopEngine.TestMacroAction`.
  Code view: `Views/MacroCodeView` (WebView2 with `Assets/CodeEditor` and Monaco, served from
  disk at `https://puppymacro.editor/`, data in `AppPaths.WebViewFolder`; the Problems panel is
  HTML in the same page, like VS Code's: Monaco has none of its own), `Services/MacroJson.cs` (the file's text, strict reading and every check, with line and
  column; unit tested) and `Services/MacroSchema.cs` (JSON Schema from the models, for Monaco's
  suggestions and inline errors). Monaco's problems come first; `MacroJson.Parse` decides Save.
- Remap: `RemapDefinition`, `LoopEngine.FindRemap`, `RemapEditorWindow`.
- Hotkeys: `HotkeyBinding`, `Services/HotkeyRules.cs` (which keys need a modifier),
  `HotkeyConflicts`, `KeyNames` (names, wheel codes), `LoopEngine.OnKey` / `OnWheel` (input.md).
- Overlay mode: the overlay is the overlay panel (`Views/OverlayPanel`, `OverlayPanelWindow`,
  click-through, no-activate, shown when `ShowOverlayPanel`) and the floating buttons.
  `PlacementWindow` (Position on screen, Position…) always shows the whole overlay as overlay
  mode shows it.
- Floating buttons: `FloatingButton` (in `AppSettings.cs`, on loops and macros),
  `Views/FloatingButtons` (which items get one: enabled, Toggle, option on; those are left out
  of the panel list), `FloatingButtonWindow` (one per button, click-through, no-activate),
  `Views/FloatingButtonView` (the round button), `Views/FloatingButtonEditor` (editor card:
  label, size, opacity, Position…). A button without a saved position is in the middle of the
  main screen (`MainWindow.ButtonPositions`).
- Tray and startup: `Services/TrayIcon.cs` (Shell_NotifyIcon), `StartupService.cs`
  (HKCU Run key, `--tray`). Uninstall removes the Run key (Velopack hook in `App.Main`).
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs` (see `updates.md`).
