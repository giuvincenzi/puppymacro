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
- **Velopack 1.2.161** (installer and updates), **Microsoft.Web.WebView2** (the editors'
  Code view) and **ModelContextProtocol.Core 2.2.0** (the official MCP C# SDK, Apache-2.0, its core only: no
  generic host; it brings Microsoft.Extensions.AI.Abstractions, Logging.Abstractions and
  DependencyInjection.Abstractions). No other NuGet packages. Win32 through P/Invoke in `Native/NativeMethods.cs`.
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
.github/workflows/              test.yml, create_release_pr.yml, publish_release.yml (build-and-release.md)
site/                           website and user guide sources (docs.md)
tests/PuppyMacro.Tests/         unit tests;  tests/PuppyMacro.E2E/  end-to-end tests (testing.md)
PuppyMacro/
  App.xaml(.cs)                 Main (--mcp bridge, Velopack), single instance, data migration, theme, tray start
  MainWindow.xaml(.cs)          side rail (Views/SideRail): Loops, Macros, Remap, AI, Settings; overlay mode; tray
  MainWindow.Lists.cs           the list pages' views (List, Table, Grid), a click on an item, tile and column widths
  MainWindow.Updates.cs         update bar, Settings dot and Updates card
  MainWindow.Ai.cs              AI page; the AI tools' host (IAiHost)
  MainWindow.Share.cs           Import and Export of loops, macros and remaps (.puppyitems), files opened from File Explorer
  AiRequestWindow, AiActivityWindow   what an AI app asks the user (confirm, a key); AI activity (View all)
  LoopEditorWindow, MacroEditorWindow, MacroActionWindow, RemapEditorWindow, GroupNameWindow, ItemsWindow (Import, Export)
  OverlayPanelWindow, FloatingButtonWindow, PlacementWindow, PickPointWindow, RecordPromptWindow,
  RecordingBarWindow, CountdownWindow
  Models/                       AppSettings (settings.json), loops, macros, remaps
  Services/                     input thread and hooks, LoopEngine, runners, InputSender,
                                recording, sounds, storage, Code view checks, tray, startup, updates
  Services/Ai/                  MCP server for AI apps: pipe, bridge, tools, activity, apps' settings, help
  Views/                        view models, SideRail, ItemCardActions (+ SubtleButtons.xaml),
                                KeyCaptureField, KeyCaps (every key shown as key caps), SwitchSettingsExpander, overlay panel, floating
                                buttons, Code view (CodeView, ViewSwitchBar, CodeViewSwitch),
                                MacroShortcutsView, ExportOptionsView, ImportItemsView, ExportItemsView (+ ItemFileRows),
                                SoundChoicesScroll, ValueSlider, WindowFit
  Resources/                    Strings.resx (English texts) and Strings.<code>.resx (translations, i18n.md)
  Native/                       Win32 interop
  Assets/                       app icon, built-in sounds, Code view page (CodeEditor) and Monaco, file icons (FileIcons)
  Properties/PublishProfiles/   Folder.pubxml (used by scripts/build.ps1)
```

## Threads

| Thread | What runs there |
|---|---|
| UI (WPF dispatcher) | Windows, view models, `SoundService`, clipboard (`ClipboardPaster`) |
| `InputThread` | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks + message loop, desktop-switch and foreground WinEvent hooks |
| `Injector` | Input sent on behalf of the hook (remap targets, menu mask key), in order |
| One per running loop row (`ActionRunner`) / per running macro (`MacroRunner`) | Timing with `PreciseTimer` (high-resolution waitable timer) and `SendInput` |
| Thread pool (`AiServer`) | The AI pipe's accept loop and one MCP session per connected AI app; the tools reach the UI only through `IAiHost.Ui` |

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
  `ClickItemsInPanel` (so are the panel's Exit, Open, Move and Stop all buttons). Each target of a loop or
  macro says whether a left click starts it (`Startable`: enabled, not Hold) and whether the click also
  reaches the app (`PassThrough`).
- The panel's Move handle (`PanelTarget.MoveOverlayId`): the hook blocks its press and release (the
  app keeps the focus) and never the moves (that would hold the cursor still); it reports the offset
  from the press through `LoopEngine.OverlayPanelDragged` (moves coalesced, one on its way at a
  time), and `MainWindow` moves the panel and saves `OverlayPanelX/Y` on release.

## Features map

- Actions shared by loops and macros: `Views/ActionBox` (one action's box, DataContext
  `Views/ActionBoxViewModel`), the same in a loop editor row and in the macro's action window
  (`MacroActionWindow`: Name and Delay cards, then the box, open). The view model holds every option
  and says which rows show (`Show…`), in the order of `ActionBox.xaml`; it reads and writes both
  models (`ToLoopAction`, `ToMacroAction`). Inside: `Views/KeyCaptureField`, `Views/MouseButtonBox`
  (the mouse button list) and `Views/PositionPicker` (X, Y, Pick on screen: every point chosen).
- Loops: `Models/AppSettings.cs` (`LoopDefinition`, `LoopAction`), `Services/ActionRunner.cs`,
  `LoopEditorWindow`.
- The list pages (Loops, Macros, Remap in `MainWindow`): one view model per item
  (`LoopItemViewModel`, `MacroItemViewModel`, `RemapItemViewModel`) shown by the templates in `MainWindow.xaml`'s resources
  in three views (List, Table, Grid; `ui.md`), and the shared buttons `Views/ItemCardActions` (Play / Stop, More options,
  switch; the buttons carry the item in their `Tag`; `OnItemPlayClick` / `OnItemMoreClick` dispatch by its type).
  `Views/PageViews` holds a page's two `ListCollectionView`s over the same items and filter: `Grouped` (List and Grid,
  grouped by app with `AppOrder`) and `Table` (the user's order); `Refresh()` updates both. `MainWindow.Lists.cs` switches
  the view (`ApplyLayout`: the views not shown get no items, so UI Automation sees each item once), opens an item on a click
  (`OpenItem`) and measures the tiles' and columns' widths (`FitTiles`, `FitColumns`). `CanEdit` on loops and macros (set
  by `MainWindow` with the running state) makes their card clickable. More options (`OnMoreClick`, `OnMacroMoreClick`,
  `OnRemapMoreClick`): Edit (`EditMenuItem`), Duplicate (`DuplicateLoop`, `DuplicateMacro`, `DuplicateRemap`; a copy's name
  from `Services/ItemNames.Copy`), Export, Move up / Move down (`MoveItem`, `MoveMacro`, `MoveRemap`), Delete. Keys everywhere
  are `Views/KeyCaps` (`Size` Default or Small, `Accent`); its static sizes also measure them (`KeyCaps.MeasureWidth`).
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
  cards, the key of Press key / Key down / Key up in `ActionBox`, remap source and target, the Settings hotkeys; `ui.md`): it
  calls `LoopEngine.BeginCapture` itself (the window gives the engine once with
  `KeyCaptureField.SetEngine(this, _engine)`, inherited by every field inside) and keeps a key only
  when its `Validate` (set by the window) accepts it (otherwise `Rejected` with the reason).
- Overlay mode: the overlay is the overlay panel (`Views/OverlayPanel`, `OverlayPanelWindow`,
  click-through, no-activate, shown when `ShowOverlayPanel`) and the floating buttons.
  `PlacementWindow` (Position on screen, Position…) always shows the whole overlay as overlay
  mode shows it. Leaving it with its hotkey or the panel's Exit keeps the main window in the tray; the
  panel's open button in its title and its **Open PuppyMacro** under Stop all (both `PanelTarget.OpenMainOverlayId` →
  `LoopEngine.OverlayExitAndOpenRequested` → `MainWindow.ShowFromTray`), the Open PuppyMacro hotkey (`AppSettings.OpenMainHotkey`,
  `EngineSnapshot.OpenMain`, the same event, also outside overlay mode), the tray icon and starting PuppyMacro again leave
  it and open the window.
  Which loops and macros it shows: `Views/OverlayItems.IsShown` (`ShowInOverlay` on, and
  enabled or `HideInOverlayWhenDisabled` off); disabled ones are shown as disabled and a left click on
  them does nothing. A right click on a row or a floating button enables or disables the item
  (`LoopEngine.OverlayEnableToggleRequested` → `MainWindow` flips its card's switch, so a running one
  stops and it is saved). `OverlayClickPassesThrough`: the hook acts on the click and lets it reach the
  app behind. The editors' card: `Views/OverlayOptionsEditor` (Show in overlay, Hide when disabled,
  Clicks also reach the app behind; off, the Floating button card is off too).
- Floating buttons: `FloatingButton` (in `AppSettings.cs`, on loops and macros),
  `Views/FloatingButtons` (which items get one: shown in the overlay, Toggle, option on; those are left
  out of the panel list), `FloatingButtonWindow` (one per button, click-through, no-activate),
  `Views/FloatingButtonView` (the round button), `Views/FloatingButtonEditor` (editor card:
  label, size, opacity, Position…). A button without a saved position is in the middle of the
  main screen (`MainWindow.ButtonPositions`).
- Languages (`i18n.md`): `Services/AppLanguages.cs` (the languages, which one and which format at start),
  `Services/Plurals.cs` (CLDR plural forms), `Services/Phrase.cs` (`Phrase`, texts kept in files and shown in the language
  of the moment; `Loc`, English for AI apps), `App.ApplyLanguage`, Settings › Language in `MainWindow`.
- Tray and startup: `Services/TrayIcon.cs` (Shell_NotifyIcon), `StartupService.cs`
  (HKCU Run key, `--tray`). Uninstall removes the Run key (Velopack hook in `App.Main`); Release builds set it
  from `StartWithWindows` at every start, so it follows the setting after an import or a reinstall.
- Backup: `Services/BackupService.cs` (export and import of the data folder, `data.md`), `Views/ExportOptionsView`
  (Export's choices, in a `ContentDialog`); the Import confirmation names the file's parts.
- Sharing (not the backup, the user's choice: two separate features): `.puppyitems` files (`Services/ItemFile.cs`, `data.md`),
  `MainWindow.Share.cs`: Import and Export in the title bar's More options menu (`ItemsWindow`, a window of its own opened
  as the editors through `ShowDialogWithoutHotkeys`, the main window hidden meanwhile; for an AI app through `ShowForAi`; with
  `Views/ImportItemsView` and `Views/ExportItemsView`, rows in `Views/ItemFileRows.cs`), Export in each card's menu (`ExportOne`,
  the file dialog at once). Import reads the file again at each check box change (`ItemFile.Read` with the chosen indexes: a name or hotkey goes to
  the first chosen item that takes it) and adds the chosen items through `SaveItem`, so the History records them. File
  Explorer: `Services/FileTypes.cs` registers `.puppyitems` and `.puppymacro` (icon, open command `"PuppyMacro.exe" "%1"`)
  in `HKCU\Software\Classes`, Release builds only, at every start in `App`
  (`SHChangeNotify` when something changed), removed by the Velopack uninstall hook. A process started with a `.puppyitems`
  path while PuppyMacro runs hands it over on `Services/OpenFileChannel.cs` (a named pipe, current user only, one per build)
  and exits. A process started with a `.puppymacro` path (a backup: it opens only from Settings › Backup) only shows
  `BackupFileWindow` and exits, before the single-instance mutex: nothing else starts, a running PuppyMacro is left alone,
  and the settings are only read for the language and theme (`SettingsStore.Peek`, never written). Its Open Settings starts
  `PuppyMacro.exe --backup-settings` (`App.BackupSettingsArgument`; a new process, as `App.ApplyLanguage` runs once per
  process): it opens on Settings with Backup open (`MainWindow.ShowBackupSettings`), or, while PuppyMacro runs, signals the
  `PuppyMacro.ShowBackupSettings` event, which the running one waits on as on `PuppyMacro.ShowWindow`, and exits. A `.puppyitems` file
  dropped on the main window goes the same way (`OnFileDrop`; `OnFileDragOver` shows
  `DropOverlay`, also for a backup, which is never imported there; other files and the cards' own drags pass as before;
  Windows refuses drops from File Explorer while PuppyMacro runs as administrator). `MainWindow.OpenItemsFile`: **the last
  file opened wins**: while the user's Import is open it shows the new file in it (`_importShowFile`, as Choose another
  file…); otherwise it waits in `_fileToOpen` until no editor or dialog is open, and a newer file replaces an older one.
  When the running PuppyMacro does not answer (it runs as administrator and the new process does not), the new process only
  shows its window.
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs` (see `updates.md`).
- History (`Services/ItemHistory.cs`, `Views/HistoryView`): every save of a loop, macro or remap keeps a version in
  `history\{id}.json` (the newest up to 10 MB per item; none for a save that changes nothing), with who saved it (`MainWindow._savedBy`: "You",
  or the AI app while `SavingAs`) and what changed (`ItemHistory.Changes`, a readable diff of the JSON). It is recorded in
  one place: `MainWindow.Save()` (loops, remaps, deletions) and `SaveMacro` (macros), so every path is covered (editors,
  switches, the overlay's right click, AI apps). `InitializeHistory` seeds the current JSON at start; the first change of an
  item saved before the history existed also keeps its earlier version. The editors of a saved item get a third view,
  History (`ViewSwitchBar` / `CodeViewSwitch`, `EditorView`; `EnableHistory`): Restore puts the version in the fields
  (`HistoryRestore`) and the `NoticeBar` says to Save.
- AI apps (MCP, `Services/Ai/`): an AI app on the PC starts `PuppyMacro.exe --mcp` (`AiBridge`, run in `App.Main` before
  Velopack and WPF: no window, no update applied). The bridge only copies stdin/stdout to the running PuppyMacro's named
  pipe (`AiPipe`: one per Windows user and per build, the development build has its own; current user only; elevated, a
  medium integrity label so a non-elevated AI app can open it, without it when Windows refuses the label) and starts
  PuppyMacro in the tray when it is not running, unless `AppSettings.AiStartWhenNeeded` is off: then it answers itself (a
  small MCP server on stdio with the same tools, `AiBridge.ClosedMessage`: open PuppyMacro, and Start with Windows / Close
  to system tray if they are off). It calls `AllowSetForegroundWindow` for PuppyMacro on every message; Windows may still
  refuse, so the request windows also open topmost (`Views/AiWindow.BringToFront`, activated once shown, not on Loaded).
  The pipe's first instance uses `FirstPipeInstance` and the bridge talks only to a server whose exe is its own
  (`AppWindows.ExePath`). `AiServer` logs every tool call (`CallLogged` → `AiCallLog`, `logs\ai-calls.jsonl`: times and sizes, never the content);
  the gap between a call's end and the next one's start is the AI app's own time (the model thinking and writing), the call's
  ms is PuppyMacro's. `AiServer` (in PuppyMacro) runs one MCP session per bridge with the SDK's
  `StreamServerTransport` (`AiServer.Options`), lists the connected apps by the name they give (`FriendlyName`) and says
  why AI apps can't connect (`ListenError`, on the AI page).
  `AiTools` holds every tool without the protocol (unit tested with a fake `IAiHost`): reads; `save_loop/macro/remap`,
  checked with the Code views' `LoopJson`/`MacroJson`/`RemapJson` and the Id PuppyMacro gives, saved at once through
  `IAiHost.Save` (`MainWindow.SaveItem`: the editors' Add / ApplyEdit; a running item stops)
  (the same for `patch_item`: a JSON Patch, RFC 6902, `AiJson.Patch`, on the item's whole JSON, then `SaveItem`'s checks;
  and `update_actions`: `AiJson.SetWhere`, the same fields on every action that matches, optionally in one macro group);
  **an AI app writes and reads every character**, so a change is a patch, not the whole item (a 100 KB macro took 1.5 minutes to
  rewrite), `get_item` gives `AiJson.Compact` (one line, fields at their default left out, read back as the same item: unit
  tested) and `get_schema` the defaults (`CodeSchema` `withDefaults`); the answers of `save_*` and `patch_item` are the new Id or
  the changes, never the item. Sharing: `export_items` (a `.puppyitems` file's text, a read), `import_items` (a file's text and
  `only`, the indexes to take; `ItemFile`'s rules, then `IAiHost.Save` for each; one confirmation for all with
  `AiAskBeforeChanging`), `open_import_window` / `open_export_window` (`IAiHost.ShowImport` / `ShowExport`: the same `ItemsWindow`s, through
  `ShowForAi`: centered on the screen, in front, closed after `AnswerTimeout` without input). With `AiAskBeforeChanging`, `AiRequestWindow` lists `ItemHistory.Changes` first; `delete_item` (asks with `AiAskBeforeDeleting`, on by default),
  `set_enabled` (a change), `update_settings` (`AiAskBeforeSettings`, on by default); interactions (`pick_point`,
  `pick_points`, `pick_app`, `capture_hotkey`, `place_floating_button`) with a `title`, a `description` and `steps` the AI
  writes (`AiAsk`, no length limits; the steps shown numbered): first `AiReadyWindow` (centered, draggable, topmost, not
  activated; Enter or Esc alone from any app through `LoopEngine.WatchReadyKeys`, every other key passes; Start), then
  `PickPointWindow` (`count` points, numbered, each with the AI's `label` next to the cursor), the key window or `PlacementWindow` (`place_floating_button`: Toggle loops
  and macros only, the button and Show in overlay on, saved with its position through `SaveItem`); `open_item` opens the
  item's editor (`EditLoop` / `EditMacro` / `EditRemap` with `forAi`: centered, in front, the AI's title and description in
  its `NoticeBar`; the user's own changes); every window of an AI request opens centered on the screen (`ShowForAi`);
  the `Instructions` tell the AI to ask, when creating, only the questions that matter, to write the exact steps and wait
  for the user's OK before picks, and then offer the missing extras. **What the model must do goes in the tools'
  descriptions and schemas too** (`NewItemRule`, `AfterNewItem`, `UserWindow`; `confirmed_steps`, required by
  `pick_point`/`pick_points`): MCP's server instructions are optional for the client, and Claude Desktop's agent mode did
  not follow them, while every client gives the model the tools' descriptions. `stop_all`; `get_help` reads the guide of
  this version, embedded from `site/guide` (`AiHelp`). AI apps cut long tool calls (Claude Desktop about 1 to 4 minutes,
  Codex 60 to 300 s): a request that waits for the user answers "waiting" with a request_id (random, tied to the app that
  asked) after `AiTools.WaitStep` (40 s) and the AI calls `wait_for_user`; an answer is given once (`Pending.Given`) and
  kept until taken. `MainWindow.ShowForAi` closes a modal request window when nobody used PuppyMacro for `AnswerTimeout`
  (5 minutes); the ready window closes after 5 minutes. One such request at a time; none while the user has a PuppyMacro
  editor or dialog open (`HotkeysSuspended`, or `ShowUserDialog` for the dialogs that do not suspend hotkeys). Starting and
  testing loops and macros are not tools on purpose (their input would go to the AI app's chat in front). Everything is
  refused while `AppSettings.AllowAiAccess` is off. Every request goes to `AiActivityLog` (`ai-activity.json`, the newest 20 MB); a
  change or deletion keeps the item's JSON before it (`Before`, `ItemKind`): Restore in Recent activity and
  `AiActivityWindow` puts it back as the user's own change (`MainWindow.RestoreActivity`). The AI page (`MainWindow.Ai.cs`):
  Allow AI access with the permissions inside, the apps as `SettingsCard`s built in code (`AppCard`; `AiApps`: Claude
  Desktop's `claude_desktop_config.json`, also the Microsoft Store copy under `Packages\Claude_*`; VS Code's
  `vscode:mcp/install` link and `mcp.json`; Codex's `config.toml` with `tool_timeout_sec = 600`; Copy configuration), Recent
  activity; the status bar at the bottom of the main window (`AiStatusBar`) while an app is connected.
