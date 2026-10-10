---
paths:
  - "PuppyMacro/**"
---

# Architecture

## Stack

- .NET 10 (`net10.0-windows10.0.18362.0`, `win-x64`, framework-dependent), WPF,
  **iNKORE.UI.WPF.Modern 0.10.2.1** (with its dependency iNKORE.UI.WPF): the WinUI controls and
  the Windows Community Toolkit's `SettingsCard` / `SettingsExpander` for WPF (`ListView`,
  `CommandBar`, `ToggleSwitch`, `NumberBox`, `InfoBar`, `ContentDialog`, `FontIcon` with
  `SegoeFluentIcons` and `FluentSystemIcons`...).
  How to use them: `ui.md`. Its license asks for the attribution in `README.md`.
- **Velopack 1.2.161** (installer and updates) and **Microsoft.Web.WebView2** (the editors'
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
  MainWindow.xaml(.cs)          side rail (Views/SideRail): Loops, Macros, Remap, Settings; overlay mode; tray
  MainWindow.Updates.cs         update bar, Settings dot and Updates card
  LoopEditorWindow, MacroEditorWindow, MacroActionWindow, RemapEditorWindow, GroupNameWindow
  OverlayPanelWindow, FloatingButtonWindow, PlacementWindow, PickPointWindow, RecordPromptWindow,
  RecordingBarWindow, CountdownWindow
  Models/                       AppSettings (settings.json), loops, macros, remaps
  Services/                     input thread and hooks, LoopEngine, runners, InputSender,
                                recording, sounds, storage, Code view checks, tray, startup, updates
  Views/                        view models, SideRail, ItemCardActions (+ SubtleButtons.xaml),
                                KeyCaptureField, SwitchSettingsExpander, overlay panel, floating
                                buttons, Code view (CodeView, ViewSwitchBar, CodeViewSwitch),
                                MacroShortcutsView, SoundChoicesScroll, WindowFit
  Native/                       Win32 interop
  Assets/                       app icon, built-in sounds, Code view page (CodeEditor) and Monaco
  Properties/PublishProfiles/   Folder.pubxml (used by scripts/build.ps1)
```

## Threads

| Thread | What runs there |
|---|---|
| UI (WPF dispatcher) | Windows, view models, `SoundService`, clipboard (`ClipboardPaster`) |
| `InputThread` | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks + message loop, desktop-switch and foreground WinEvent hooks |
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
  `ClickItemsInPanel` (so are the panel's Exit, Stop all and Move buttons).
- The panel's Move handle (`PanelTarget.MoveOverlayId`): the hook blocks its press and release (the
  app keeps the focus) and never the moves (that would hold the cursor still); it reports the offset
  from the press through `LoopEngine.OverlayPanelDragged` (moves coalesced, one on its way at a
  time), and `MainWindow` moves the panel and saves `OverlayPanelX/Y` on release.

## Features map

- Loops: `Models/AppSettings.cs` (`LoopDefinition`, `LoopAction`), `Services/ActionRunner.cs`,
  `LoopEditorWindow`.
- The list cards (Loops, Macros, Remap in `MainWindow`): one view model per item
  (`LoopItemViewModel`, `MacroItemViewModel`, `RemapItemViewModel`) and the shared right side
  `Views/ItemCardActions` (Play / Stop, Edit, switch, More options; the buttons carry the item in
  their `Tag`). `CanEdit` on loops and macros is set by `MainWindow` with the running state.
- Editors (loop, macro, remap) and the placement overlay open through
  `MainWindow.ShowDialogWithoutHotkeys`: hotkeys off, and the main window is hidden once the editor is
  on screen (so it opens centered on it) and shown again when it closes. The editors therefore have
  their own taskbar button (`ShowInTaskbar="True"`). Recording and Test action hide the main window
  themselves and show it again only if it was visible before.
- Macros: `Models/MacroModels.cs`, `Services/MacroBuilder.cs` (raw events → actions, path
  simplification), `MacroRunner.cs`, `MacroLibrary.cs` (one JSON per macro),
  `MacroEditorWindow`, `MacroActionWindow`, `GroupNameWindow`, `Views/MacroShortcutsView` (the
  editor's Shortcuts dialog). Recording: `RecordingSession.cs` runs the flow (`RecordPromptWindow`,
  then a 3-second countdown in `RecordingBarWindow` and big in `CountdownWindow`, then the bar with
  Stop).
  The editor's operations (group, ungroup, copy / paste, duplicate, drag, Alt+Up / Alt+Down)
  live in `Services/MacroEditList.cs`, without UI, with unit tests; the window only maps the
  list to rows and group headers. Test action: `Services/ActionTestSession.cs` (moves the
  editor off screen, hides the main window, focuses the window behind, waits 200 ms) plays
  `MacroDefinition.ForTest` through `LoopEngine.TestMacroAction`.
- Form view / Code view (macro, action, loop and remap editors): `Views/ViewSwitchBar` (the
  switch and Format), `Views/CodeView` (WebView2 with `Assets/CodeEditor` and Monaco, served from
  disk at `https://puppymacro.editor/`, data in `AppPaths.WebViewFolder`; the Problems panel is
  HTML in the same page, like VS Code's: Monaco has none of its own) and `Views/CodeViewSwitch`
  (switching in the same window at the same size, Discard changes, Save and Form view only
  without problems).
  `Services/CodeJson.cs` reads the code strictly and finds lines and columns; the checks of each
  item are in `MacroJson` (macro, one action and the groups it can be in), `LoopJson` and
  `RemapJson` (in `LoopJson.cs`), equal to the windows' checks; unit tested.
  `Services/CodeSchema.cs` gives Monaco the JSON Schema of each item (suggestions, inline errors).
  Monaco's problems come first; the app's checks decide Save.
- Remap: `RemapDefinition`, `LoopEngine.FindRemap`, `RemapEditorWindow`.
- Specific app (`AppExe` on loops, macros and remaps; `Models/AppScope`: null = all apps, names
  compared without case): the card `Views/AppScopeEditor`, the same in the three editors (list of
  running apps, Pick, Browse…). Pick opens `PickPointWindow(pickApp: true)`: the editor moves off
  screen, the window under the cursor (`Services/AppWindows.At`, PuppyMacro's own windows skipped) is
  outlined with its .exe name. The app in front comes from the `EVENT_SYSTEM_FOREGROUND` WinEvent hook
  (`LoopEngine.OnForegroundWindow`): PuppyMacro's own windows never count, the last other app stays
  (`LoopEngine.ForegroundApp`). When it changes, the loops and macros running for another app stop (one
  stop sound) and `ForegroundAppChanged` tells the UI: the overlay panel (`OverlayPanel.SetApp`) and the
  floating buttons show only the items for all apps and for that app. `PlacementWindow` shows them all.
  Remaps read the window in front at each key press (`FindRemap`), so a remap for an app never applies
  inside PuppyMacro's windows.
- Hotkeys: `HotkeyBinding`, `Services/HotkeyRules.cs` (which keys need a modifier),
  `HotkeyConflicts`, `KeyNames` (names, wheel codes), `LoopEngine.OnKey` / `OnWheel` (input.md).
  `Views/KeyCaptureField` is the one field that asks the user for a key or hotkey (editors' Hotkey
  cards, loop key rows, remap source and target, the macro action's key, the Settings hotkeys; `ui.md`): it
  calls `LoopEngine.BeginCapture` itself (the window gives the engine once with
  `KeyCaptureField.SetEngine(this, _engine)`, inherited by every field inside) and keeps a key only
  when its `Validate` (set by the window) accepts it (otherwise `Rejected` with the reason).
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
