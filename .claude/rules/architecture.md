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
- At start `App.KeepFullSpeed` opts PuppyMacro out of Windows' power throttling (`SetProcessInformation`,
  `ProcessPowerThrottling`: no EcoQoS, timer resolution kept while its windows are hidden): its timings must not depend
  on whether a window is visible.
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
  list to rows and group headers. Test action: `Services/ActionTestSession.cs`; it runs as a macro does (Stop all
  stops it, `AnyRunning` is true meanwhile) and the editors come back however it ends.
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
  - Each monitor has its own capture thread and its own Direct3D device, made from the adapter the monitor is connected
    to; every use of a device, the duplication's too, is under its lock. After an error the capture waits longer each
    time and gives up after a few errors in a row (`CaptureRetry`), logged in `logs\screen-capture.log`: a duplication
    made again and again without a pause, with several threads on one device, came before a blue screen on a PC with two
    monitors. A capture that gave up is replaced at once when a watch starts, even while other watches still hold it
    (its thread has ended and its duplication is closed), and released with its last watch; a working capture is never
    duplicated twice.
  - The same search runs on the graphics card (compute shaders) or on the processor (OpenCV) with the same results
    (unit tested on WARP); `ConditionChecker` chooses by the area's size unless Settings › Screen recognition forces one.
    The two share one definition of flat (`Flatness`: a one-color image is refused when taken and never found), one
    choice of candidates (`CpuMatcher.Candidates`) and one tie rule (`ImageMatch.Tie`: the top one, then the left one).
  - The editor's Now on the screen and an AI app's check_condition follow the run's rule: a place that can't be seen
    makes no condition true, a Not one either (`ConditionReport.Unseen`).
  - While a condition looks at the screen, and a few seconds after, PuppyMacro's windows are left out of every screen
    capture (`CaptureExclusion`, Windows 10 2004 and later), so a condition never sees PuppyMacro: only then, as the
    user's own screenshots and recordings lose PuppyMacro's windows too.
  - No recognition inside apps that block screen capture or run in exclusive fullscreen: they show black (the condition window
    says so).
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs` (see `updates.md`).
- The debug archive (`Services/DebugStore.cs`, `%LocalAppData%`, not the data folder): one folder per operation,
  `ai\<app>\<session>\<nnn>-<tool>[-<item>]\` for each call of an AI app (`DebugSession`, `DebugCall`: `session.json`,
  `request.json` with every argument and the app's `_meta`, `response.json`) and `user\macro\<time>-<macro>\` for a run
  the user starts with Debug on. Names have no spaces and sort by time; they are also the MCP resources' paths. One
  background thread writes everything (`DebugStore.Write`): what plays never waits for the disk. A run's log
  (`RunLog`): numbered events, each with the screen's pictures just before it named by its number, in English. The
  cleaner deletes whole operations older than `DebugStore.Life`, at start and every hour, never one in progress and never
  at close (a crash loses nothing). Debug is the user's alone (`AiTools.KeepUsersDebug` on every AI save); an AI app's
  test is always logged.
- The Library (`Services/ComponentLibrary.cs`, `Models/LibraryModels.cs`, `MainWindow.Library.cs`, `LibraryWindow`,
  `ComponentWindow`, `ComponentDetailsWindow`): collections of components that macros link. Decisions (the user's):
  - A **component** is one or more actions (a point, an If, a sequence) with a name, and a structured name in English
    the AI apps search by, `domain-app-purpose-concept`, composed from its collection's domain and app. A **collection**
    is the package for one kind of app and screen (its monitor's bounds and scale, `LibraryScreens`): shared whole,
    never split. Ids never change; a component never moves to another collection; its "Where … was found" use its own
    conditions (what comes from outside comes as a parameter).
  - **A component is a function** (the user's decision, with GitHub Actions' composite actions and Amazon States
    Language as precedents): a typed signature (`Parameters`: Point, Number with a unit, Text, Key, Color, Image, Area, Grid;
    required when it has no `Default`; one `Example`, which PuppyMacro writes into the bound fields at save), fields bound
    to parameters next to the field (`Bind` on an action or condition, by slot), calls with `Arguments` (a value,
    `{"From"}` a parameter of the component around, `{"Found"}` a place an If around the call found), components calling
    components (never in a loop, at most `ComponentSignatures.MaxDepth` levels), and `Returns` (one of its conditions):
    such a component is also a condition, `ConditionKind.Component`, checked as the condition it returns with the call's
    arguments, in the If's own wait. No expressions, no code in the JSON: a field takes a parameter's value as it is.
  - **A Grid** (the user's decision: one more type, general, not a feature apart) is cells all alike in rows: the same
    point of the first and of the last cell, the columns and the rows. A Point field (a click's, mouse button's or move's,
    a pixel's) bound to a Grid takes one of its cells, `Cell` (counted row by row from 1), fixed or bound to a Number
    (Count). The step can have decimals; the point is rounded half away from zero (`GridValue.PointOf`, the D4 inventory's
    33 slots exactly). Only points: a grid has no cell size, so no areas. Every call is checked with its values, down the
    calls, and the component with its Examples: a cell its grid doesn't have can't be saved nor played, and if one got
    there it is skipped, never played at another point.
  - **The checks and the preparation of components live only in `Services/Components/`**, and everyone calls them (the
    editors, the Code views, `MacroJson`, `SaveLibrary`, import, the AI tools, the snapshot): `ComponentSlots` (which
    field of which action or condition can take a parameter, its type, unit and limits, from `MacroJson`'s ranges),
    `ComponentValues` (each type's JSON and its rule), `ComponentSignatures` (the limits rising from the fields to the
    parameters, also through `{"From"}` down the calls; loops; depth), `ComponentChecks`, `ComponentPrograms`
    (preparation and what a frame needs), `SignatureUpdate`, `ComponentEdits` (Unlink, Save as component, rename).
  - A macro row of type `Component` links a component: it plays it as it is in the Library (one greyed line, no wait of
    its own; only its arguments change in the macro). Linking is the default; **Unlink** makes it, one level, the
    component's actions of that moment with the row's values in their fields, normal rows. Nothing else is a copy that
    remembers where it came from.
  - The engine prepares each component once in its snapshot (`EngineSnapshot.Components`, never changed after) and plays
    a call in a **frame** (`MacroRunner`): the call's arguments, which bound fields read when each action starts, and the
    places its Ifs find, so two calls never mix them up and 33 calls are 33 frames of one prepared component. Playing never
    reads the Library; Play, test_macro and the hotkeys start the snapshot's macro. A macro whose calls fail the checks
    (a missing component, a wrong argument, or a component that fails them) can't play (`EngineSnapshot.Unplayable`; its
    hotkey passes) nor be saved.
  - A change of a component that existing calls can't follow (a parameter removed, renamed, a new required one, another
    type) is a **guided update**: one value for all the calls that need it (`SignatureUpdateWindow`, an AI app's
    `call_values`), then the component and the changed macros and components saved together or not at all
    (`MacroLibrary.TrySaveTogether`); only what can't be fixed that way is refused.
  - Every change goes through `MainWindow.SaveLibrary`: refused while something runs (an import that only adds
    collections excepted), then every component and every macro's calls are checked against the Library as it would be
    (`ComponentChecks.CheckAll`; a problem the change brings refuses it), then saved with the macros it changes, recorded in
    the History (each collection and component is an item), the snapshot made again and the unused images deleted. A component called (a linked row or a Component condition) by a
    saved macro or by the open editor's can't be deleted, nor its collection (`ComponentLibrary.Called`).
  - Import compares a collection by id and fingerprint (`ComponentLibrary.Fingerprint`: the components' ids, signatures
    and actions, recomputed with the current code): a different version of a collection the Library has refuses the whole
    file. A file carries the collections its components call, down every call (`ComponentLibrary.WithDependencies`), and
    its calls are checked against the Library with the file's collections in it.
  - AI apps: `search_library` (each component's signature), `get_component` (a call to copy), `save_component` (with the
    guided update); a macro calls a component with a `Component` row or condition written like any other;
    `update_actions` leaves linked rows alone; `get_item` reads a big macro in pieces (`outline` with the calls' values,
    `path`) and lists the components a macro calls. The Instructions and the Library tools' descriptions say how
    functions work and what makes a component reusable.
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
  - **MCP as specified**: every tool added or changed follows the MCP specification of the SDK's version (2.2.0:
    [2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25), its
    [Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools) and
    [Resources](https://modelcontextprotocol.io/specification/2025-11-25/server/resources)) and Anthropic's
    [Writing effective tools for agents](https://www.anthropic.com/engineering/writing-tools-for-agents); read them
    again before changing a tool. Tools are named `puppymacro_<name>` (`AiTools.Prefix`), each with a `title` and honest
    annotations (only reads: `readOnlyHint`; can rewrite or remove what exists, not only add, or send input to the user's
    apps: `destructiveHint`; pure reads: `idempotentHint`); an input error, also an argument of the wrong type, is a tool
    error that names the argument and what to change (`isError`), never ignored or guessed; each connection is rate
    limited (`AiRateLimit`).
  - Every request of an AI app, a tool call or a `resources/read`, goes through one function (`AiServer.Debugged`): the
    connection's rate limit and its folder in the debug archive, so nothing an AI app does is missing from it.
  - Which app it is (`AiServer.KindOf`: the AI page's cards) is apart from the name people read (`FriendlyName`, the
    user's choice): a Claude app is "Claude" with the name it gives itself ("Claude (claude-ai)", the chat;
    "Claude (local-agent-mode-…)", the agent mode; "Claude (claude-code)"), so they read apart everywhere.
  - **An answer holds the result, short; large data stays in files and goes as references**: a run's log, the screen's
    pictures, a recording are in the call's debug folder, and the answer gives MCP `resource_link`s (and the path); the
    AI app reads one with `resources/read` (`AiServer.DebugResource`: only files inside the debug archive, else -32002)
    when it needs it. Whatever enters an answer stays in the AI app's context until the end of the conversation. No
    `structuredContent`: the same JSON would also be text, twice in the context.
  - The links are built in one place for every tool (`AiServer.McpTool`): each with its size and a description of what
    the file holds and, from `DebugStore.Describe`, a picture's width and height or a text's lines. Some AI apps show the
    model only a link's name and URI (Claude Desktop did), so the answer's text also ends with the list of linked files
    and the same facts (`AiServer.WithFileList`, one place for every tool). Only measured facts: no estimates and no
    limits of a particular AI app in the texts.
  - Some AI apps do not let the model read resources (MCP leaves reading them to the client; Claude Desktop's model could
    not open a link): `read_resource` reads a linked file as a tool, the pattern of FastMCP's "Resources as Tools". The
    descriptions and answers that link files say both ways; nothing in the code depends on which client is connected.
    `read_resource` and `resources/read` call the same function (`AiTools.OpenResource`, with `DebugStore.Open`), which
    resolves the URI, checks that it stays inside the debug folder and the permissions, and gives the type: they cannot
    differ on what can be read, because the code is one. A picture's part (`area`) or smaller copy (`scale`) and a text's
    lines (`offset`, `limit`) are asked by the AI app, never chosen by PuppyMacro; the files never change.
  - `AiTools` holds every tool without the protocol, unit tested with a fake `IAiHost`. Saves are checked with the Code
    views' checks.
  - **An AI app writes and reads every character**, so a change is a patch, not the whole item, and reads are compact.
  - **What the model must know goes in the tools' descriptions and schemas too**: MCP's server instructions are optional
    for the client, and Claude Desktop's agent mode did not follow them, while every client gives the model the tools'
    descriptions.
  - **The texts for the AI describe, they do not command** (the user's choice: the AI is not treated as a robot): how
    PuppyMacro works and what each choice brings (what is faster, sturdier, costlier), and the model decides. The limits
    that matter are in the code: the ready window before anything on the user's screen, `confirmed_steps`, the AI page's
    permissions, the checks of a save.
  - AI apps cut long tool calls: a request that waits for the user answers "waiting" and the AI calls `wait_for_user`.
  - One request that waits for the user at a time; none while the user has a PuppyMacro editor or dialog open. Request
    windows open centered on the screen and in front, and close after a timeout without input.
  - The screen (`AiTools.Screen.cs`): `look_at_screen` (each monitor's picture, the screen's own, linked; `take_image` and
    `pick_pixel` read it again with its `snapshot_id`), `check_condition` (the same rule as the editor's Now on the screen,
    `ConditionReport.TrueFor`), `record_macro` (PuppyMacro's recorder after the ready window: the screen at each key and
    click, an image around each click), `test_macro`, `get_last_run` and `read_resource`. Pictures need
    `AppSettings.AiSeeScreen`. When one of them is over, `AiFinishedWindow` says the user can go back to the AI app (the
    name it gave).
  - An AI app plays a macro only with `test_macro` (with `AppSettings.AiTestMacros`): after the user's Start in the ready
    window, with the app the user brought in front given the front back, so its input never goes to the AI app's chat;
    always logged, stopped after its time limit. Nothing else starts loops or macros (only `stop_all` stops them).
  - Everything is refused while `AppSettings.AllowAiAccess` is off.
  - Restore of an AI change (Recent activity, `AiActivityWindow`) puts the item back as the user's own change.
