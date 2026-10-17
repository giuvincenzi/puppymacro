---
paths:
  - "PuppyMacro/**"
---

# Architecture

## Stack

- .NET 10 (`win-x64`, framework-dependent), WPF, with **iNKORE.UI.WPF.Modern** for every control (`ui.md`). Its
  license asks for the attribution in `README.md`.
- The NuGet packages are in `PuppyMacro/PuppyMacro.csproj`; **a new NuGet package needs the user's approval**. The MCP
  SDK is used through its core only: no generic host. Win32 through P/Invoke in `Native/NativeMethods.cs`; DXGI, Direct3D 11
  and Display Config (screen recognition) through CsWin32, which generates them from `NativeMethods.txt` at build time
  (a build-time package only).
- **OpenCV** through OpenCvSharp, for the screen recognition on the processor; its license asks for the attribution in
  `README.md`.
- **Monaco editor** (MIT, the editor of VS Code), only the files JSON needs, in `Assets/Monaco` with its license (the
  version is in the files' headers); `Assets/CodeEditor` is the page around it. To update it, copy the same files from
  the npm package's `min/vs`. The loader's `vs` path must be an absolute address: the JSON worker (suggestions, schema
  checks, Format) resolves it from its own context, where a relative path fails silently. "No problems." shows only once
  the worker runs, so the Code view end-to-end test catches a broken worker.
- Nullable reference types on, implicit usings off (explicit `using`s).

## Repository layout

```
scripts/                        PowerShell scripts run by the Makefile
.github/workflows/              workflows (build-and-release.md)
site/                           website and user guide sources (docs.md)
tests/PuppyMacro.Tests/         unit tests;  tests/PuppyMacro.E2E/  end-to-end tests (testing.md)
PuppyMacro/                     App, MainWindow and the other windows
  Models/                       settings, loops, macros, remaps
  Services/                     input thread and hooks, LoopEngine, runners, InputSender, storage, Code view checks...
  Services/Ai/                  MCP server for AI apps
  Views/                        view models and reusable controls
  Resources/                    texts and translations (i18n.md)
  Native/                       Win32 interop
  Assets/                       app icon, sounds, Code view page and Monaco, file icons
```

## Threads

| Thread | What runs there |
|---|---|
| UI (WPF dispatcher) | Windows, view models, `SoundService`, clipboard (`ClipboardPaster`) |
| `InputThread` | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks + message loop, desktop-switch and foreground WinEvent hooks |
| `Injector` | Input sent on behalf of the hook (remap targets, menu mask key), in order |
| One per running loop row (`ActionRunner`) / per running macro (`MacroRunner`) | Timing with `PreciseTimer` (high-resolution waitable timer) and `SendInput` |
| One per monitor a macro's condition looks at (`ScreenCapture`) | Desktop Duplication: copies each new frame on the monitor's graphics card |
| Thread pool (`AiServer`) | The AI pipe's accept loop and one MCP session per connected AI app; the tools reach the UI only through `IAiHost.Ui` |

Rules:
- **Never block the input thread** and never touch WPF objects from it. The hook only
  decides (block or not) and posts work. Windows silently skips, then removes, hooks that
  are too slow.
- `LoopEngine` state is guarded by one lock. The hook reads an immutable
  `EngineSnapshot`. **After any change to settings, loops, macros or remaps, the UI must publish a new snapshot**
  (the save paths do it).
- UI notifications from the engine go through `Dispatcher.InvokeAsync`.
- Clicks on the overlay panel and on the floating buttons are matched against rectangles
  (`PanelTarget`, physical pixels) published by the UI while overlay mode is on. The hook decides from each target
  whether a left click starts it (`Startable`) and whether the click also reaches the app (`PassThrough`).
- The panel's Move handle: the hook blocks its press and release (the app keeps the focus) and never the moves (that
  would hold the cursor still).

## Features map

- Actions shared by loops and macros: `Views/ActionBox` is one view shared by a loop editor row and the macro's action
  window; its view model reads and writes both models.
- Loops: `Models/AppSettings.cs`, `Services/ActionRunner.cs`, `LoopEditorWindow`.
- The list pages (Loops, Macros, Remap in `MainWindow`, `MainWindow.Lists.cs`): three views (List, Table, Grid; `ui.md`).
  `Views/PageViews` holds a page's two views over the same items and filter (grouped by app, and the user's order); the
  views not shown get no items, so UI Automation sees each item once. Keys everywhere are `Views/KeyCaps`.
- Editors (loop, macro, remap) and the placement overlay open through
  `MainWindow.ShowDialogWithoutHotkeys`: hotkeys off, and the main window is hidden once the editor is
  on screen (so it opens centered on it) and shown again when it closes. The editors therefore have
  their own taskbar button. Recording and Test action hide the main window
  themselves and show it again only if it was visible before.
- Macros: `Models/MacroModels.cs`, `Services/MacroBuilder.cs`, `MacroRunner.cs`, `MacroLibrary.cs` (one JSON per
  macro), `MacroEditorWindow`. Recording: `RecordingSession.cs`.
  The editor's operations live in `Services/MacroEditList.cs`, without UI, with unit tests; the window only maps the
  list to rows and group headers. Test action: `Services/ActionTestSession.cs`.
- Form view / Code view (macro, action, loop and remap editors): `Views/CodeView` (WebView2 with `Assets/CodeEditor`
  and Monaco; the Problems panel is HTML in the same page, like VS Code's: Monaco has none of its own),
  `Services/CodeJson.cs`, `Services/CodeSchema.cs`. The checks of each item (`MacroJson`, `LoopJson`, `RemapJson`) are
  equal to the windows' checks; unit tested. Monaco's problems come first; the app's checks decide Save.
- Remap: `RemapDefinition`, `LoopEngine.FindRemap`, `RemapEditorWindow`.
- Specific app (`AppExe` on loops, macros and remaps; `Models/AppScope`): the card `Views/AppScopeEditor`, the same in
  the three editors. PuppyMacro's own windows never count as the app in front: the last other app stays. When it
  changes, the loops and macros running for another app stop. Remaps read the window in front at each key press, so a
  remap for an app never applies inside PuppyMacro's windows.
- Hotkeys: `HotkeyBinding`, `Services/HotkeyRules.cs`, `HotkeyConflicts`, `KeyNames`, `LoopEngine.OnKey` / `OnWheel`
  (input.md). `Views/KeyCaptureField` is the one field that asks the user for a key or hotkey (`ui.md`): it calls
  `LoopEngine.BeginCapture` itself and keeps a key only when its `Validate` (set by the window) accepts it.
- Overlay mode: the overlay panel (`Views/OverlayPanel`, `OverlayPanelWindow`) and the floating buttons; which items it
  shows: `Views/OverlayItems.IsShown`. A right click on a row or a floating button flips its card's switch, so a running
  item stops and it is saved.
- Floating buttons: `FloatingButton` (in `AppSettings.cs`), `Views/FloatingButtons`, `FloatingButtonWindow`,
  `Views/FloatingButtonEditor`.
- Languages: see `i18n.md`.
- Tray and startup: `Services/TrayIcon.cs` (Shell_NotifyIcon), `StartupService.cs`
  (HKCU Run key, `--tray`). Uninstall removes the Run key (Velopack hook in `App.Main`); Release builds set it
  from `StartWithWindows` at every start, so it follows the setting after an import or a reinstall. The user Run key
  cannot start a program as administrator, so Start with Windows never starts PuppyMacro elevated.
- Backup: `Services/BackupService.cs` (export and import of the data folder, `data.md`), `Views/ExportOptionsView`
  (Export's choices, in a `ContentDialog`); the Import confirmation names the file's parts.
- Sharing (`.puppyitems` files: `Services/ItemFile.cs`, `MainWindow.Share.cs`, `data.md`). Decisions:
  - Sharing and the backup are two separate features (the user's choice): sharing only adds items, the backup replaces
    the data; a backup opens only from Settings, never with a double-click in File Explorer or by dragging it onto the
    window.
  - **The last file opened wins**: a newer file replaces an older one waiting to be shown.
  - A process started with a `.puppymacro` path only shows `BackupFileWindow` and exits, before the single-instance
    mutex: nothing else starts, a running PuppyMacro is left alone, and the settings are never written. Its Open
    Settings starts `PuppyMacro.exe --backup-settings`, a new process, as `App.ApplyLanguage` runs once per process.
  - A `.puppyitems` path given to a new process while PuppyMacro runs is handed over on `Services/OpenFileChannel.cs`
    (a named pipe, current user only, one per build).
  - Windows refuses drops from File Explorer while PuppyMacro runs as administrator.
- Conditions on the screen (`Services/Screen/`): an If in a macro checks a pixel, images or a text, waits, then plays
  Then or Else (`ConditionChecker`, from `MacroRunner`). Decisions:
  - The screen is read as the conditions see it everywhere: the pickers (`ScreenPickWindow`) take pixels and images from
    the same capture, with HDR turned to SDR colors, so what is picked is what is found.
  - The capture's Direct3D and DXGI objects are created and used only on MTA threads (`ScreenCapture.Mta`): they cannot
    be marshaled between apartments, and a call from the UI thread to one made on another thread crashes the process.
  - The same search runs on the graphics card (compute shaders) or on the processor (OpenCV) with the same results
    (unit tested on WARP); `ConditionChecker` chooses by the area's size unless Settings › Screen recognition forces one.
  - While a condition looks at the screen, and a few seconds after, PuppyMacro's windows are left out of every screen
    capture (`CaptureExclusion`, Windows 10 2004 and later), so a condition never sees PuppyMacro: only then, as the
    user's own screenshots and recordings lose PuppyMacro's windows too.
  - No recognition inside apps that block screen capture or run in exclusive fullscreen: they show black (the condition window
    says so).
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs` (see `updates.md`).
- History (`Services/ItemHistory.cs`, `Views/HistoryView`): every save of a loop, macro or remap keeps a version. It is
  recorded in one place, `MainWindow.Save()` and `SaveMacro`, so every path is covered (editors, switches, the overlay's
  right click, AI apps). In the editors' History view, Restore puts the version in the fields and asks to Save.
- AI apps reach PuppyMacro only through MCP from an app installed on the PC (Claude Desktop, VS Code, Codex…):
  claude.ai, ChatGPT and other apps on the web cannot reach a program on the PC without a relay on the internet, which
  the project would have to host and secure. Not planned. PuppyMacro has no AI of its own and no API keys.
- AI apps (MCP, `Services/Ai/`):
  - An AI app starts `PuppyMacro.exe --mcp` (`AiBridge`, run in `App.Main` before Velopack and WPF). The bridge only
    copies stdin/stdout to the running PuppyMacro's named pipe.
  - The pipe (`AiPipe`) is one per Windows user and per build, current user only; elevated, it gets a medium integrity
    label so a non-elevated AI app can open it. Its first instance uses `FirstPipeInstance` and the bridge talks only to
    a server whose exe is its own.
  - The call log (`AiCallLog`) keeps times and sizes, never the content.
  - `AiTools` holds every tool without the protocol, unit tested with a fake `IAiHost`. Saves are checked with the Code
    views' checks.
  - **An AI app writes and reads every character**, so a change is a patch, not the whole item, and reads are compact.
  - **What the model must do goes in the tools' descriptions and schemas too**: MCP's server instructions are optional
    for the client, and Claude Desktop's agent mode did not follow them, while every client gives the model the tools'
    descriptions.
  - AI apps cut long tool calls: a request that waits for the user answers "waiting" and the AI calls `wait_for_user`.
  - One request that waits for the user at a time; none while the user has a PuppyMacro editor or dialog open. Request
    windows open centered on the screen and in front, and close after a timeout without input.
  - Starting and testing loops and macros are not tools on purpose (their input would go to the AI app's chat in
    front).
  - Everything is refused while `AppSettings.AllowAiAccess` is off.
  - Restore of an AI change (Recent activity, `AiActivityWindow`) puts the item back as the user's own change.
