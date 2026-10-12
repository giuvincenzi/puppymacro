# Tests

## Unit tests: `make test`

- `tests/PuppyMacro.Tests`: xUnit v3 (`xunit.v3.mtp-v2`) on Microsoft Testing Platform
  (`global.json` sets the `dotnet test` runner). It references the app project; the app's
  `internal` types are visible through `InternalsVisibleTo`.
- `make test` runs `dotnet test` in **Release**: the code users get, and a running dev build
  (which locks `bin/Debug`) does not block it.
- They run on GitHub in the `test` workflow, job `unit_test` (`.github/workflows/test.yml`): on every pull request
  and every push to `main` (a required check for merging), and in `publish_release` before a release is built (it
  stops when they fail). The push of a release merge (it changes `PuppyMacro/Version.props`) skips them, as
  `publish_release` runs them on that commit.
- Covered: settings loading and migrations (`SettingsStore`), Export and Import (`BackupServiceTests`: every part, the
  identical copy, partial files, older files, refused files), macros from recorded events
  (`MacroBuilder`), `MacroLibrary`, macro groups and the editor's operations
  (`MacroEditList`), the single-action test macro (`MacroDefinition.ForTest`),
  the options shared by loop and macro actions (`ActionOptionsTests`: combinations, Hold for, Times, double click, fixed point, older files, Code view checks), `HotkeyConflicts` (also per app, for remap sources and for modifier sides), `HotkeyRules`, which items the overlay shows
  (`OverlayItems`, `FloatingButtons`), `ModifierTracker`, `KeyNames`, `HotkeyBinding` (`Matches`, `Overlaps`, sides), what the key
  field shows and its side items (`KeyCaptureField.DisplayParts`, `SideChoices`, `WithSide`), loop intervals, `FloatingButton` (labels,
  sanitizing, opacity, old files), the Code views' checks and schemas (`MacroJson`, `LoopJson`,
  `RemapJson`, `CodeSchema`, the groups an action can be in), `LoopEngine`'s input decisions (keys
  held by a Hold down loop, wheel hotkeys, hotkey capture, the overlay panel's Stop all, Exit, Open and Move, right
  clicks on the overlay's items, disabled items and clicks that pass through,
  hotkeys of items for a specific app and their stop when another app comes in front, through
  `OnForegroundApp`, hotkeys and remaps with modifier sides, remaps from a combination or of a modifier
  alone, AltGr's fake Ctrl, capture of a modifier alone),
  driven through `OnKey` / `OnWheel` / `OnMouseDetail` without hooks (also the AI ready keys), the History
  (`ItemHistoryTests`: created, changes, no version for no change, the version before the history, 50 kept, deleted and
  restored, actions added and removed), the AI tools (`AiToolsTests`, with `FakeAiHost` as the app and the user: access
  off, reads, saves checked like the editors and saved at once, each permission, Restore data, "waiting" and
  `wait_for_user`, the ready window before picks, settings, help, the message while PuppyMacro is closed;
  `AiTools.WaitStep` is short there; `AiJsonTests`: compact JSON read back as the same item, every JSON Patch operation and
  its errors, `SetWhere`; `AiCallLogTests`; the size limits of the activity and the History), the AI apps'
  settings files (`AiAppsTests`: Claude Desktop with its Microsoft Store copy, VS Code's link and `mcp.json`, Codex's
  `config.toml`, never replacing a file with an error), `AiActivityLog` and `AllowAiAccess` in older files, `.puppyitems`
  files (`ItemFileTests`: written and read back as new disabled items, refused files, a newer format or version, an item with
  problems, names with a number, hotkeys left out also between the file's items, remap clashes, the chosen items only, file
  names; the AI tools `export_items`, `import_items`, `open_import_window`, `open_export_window` in `AiToolsTests`), the file
  types' registry keys (`FileTypesTests`, under a test key in `HKCU\Software\PuppyMacroTests`, deleted after), and the XAML files
  (`ControlResourcesTests`: every `NumberBox` in a `SettingsCard` sets its `MinWidth`, `ui.md`).
- Use `TempFolder` for files. `ModifierTracker` is static: reset it before and after each test,
  and put the test class in `[Collection(nameof(ModifierTracker))]` so they never run at the
  same time. A unit test that starts a loop makes it press F24 only.

## End-to-end tests: `make e2e`

- `tests/PuppyMacro.E2E`: xUnit v3 + FlaUI (UIA3). `AppSession` closes any running PuppyMacro,
  deletes `%AppData%\PuppyMacro Dev` and starts the development build
  (`PuppyMacro/bin/Debug/...`), so every test starts from the sample data. `new AppSession(seed => ...)`
  starts it from the data the test writes instead (`Seed`: the app's models, referenced by the
  project; no samples, global hotkeys Stop all F19, Overlay mode F24, Record F18).
- They run on GitHub in the `test` workflow, job `e2e_test`: on every pull request (a required check for
  merging into `main`), on every push to `main` and in `publish_release` before every release (it stops when they
  fail). The push of a release merge skips them, as `publish_release` runs them on that commit: `decide_if_tests_run`
  skips only when `github.workflow` is `test` (called by `publish_release` it is `publish_release`, the caller's github
  context). The runner's screen is 1024x768: do not click controls that may be off
  screen there (for example the title bar of a tall window); use UI Automation patterns
  instead (`Window.Close()`, `Invoke`).
- `make e2e` runs them locally; it builds the dev build first. Tests run one at a time (one
  PuppyMacro at a time).
- Every test class has a category, `[Trait("Category", ...)]`: `UI` (`AppTests`: windows, pages,
  editors, Export's choices, Export of items with search, a `.puppyitems` file opened while PuppyMacro runs (a second
  `PuppyMacro.exe` with its path, as File Explorer starts it; the file dialogs are never opened)), `Macros` (`MacroActionTests`, `MacroRunTests`), `Loops`, `Remaps`, `Recording`,
  `Overlay`, `AI` (`AiTests`: the test is the AI app, it starts `PuppyMacro.exe --mcp` with the MCP SDK's
  `StdioClientTransport` as "E2E test client", calls the tools and answers in PuppyMacro's windows (the ready window with
  Start, never Enter; `import_items`, `export_items` and the Import window opened by `open_import_window`); its remaps use F22 and F23, the key it presses is F22, the points it clicks are on the picker, which
  covers the screen).
  `make e2e category=Macros` runs only one category: use it while working on that
  feature; before pushing, and on GitHub, all of them run.
- They take over the desktop: windows open and close, the mouse moves, keys are pressed.
  **Never run `make e2e` (or anything else that drives the UI, like `scripts/screenshots.ps1`, which
  brings PuppyMacro to the front) on a contributor's PC without asking first**: they cannot
  use the PC meanwhile, and their input can make the tests fail. Unit tests (`make test`) run
  in the background and can be run any time.
- Find controls by their UI text (`Find`) or by `x:Name` (`FindById`). Wait with `Retry` and
  check `.Success`, never fixed sleeps. Other `AppSession` helpers: `GoTo` (a side rail page),
  `Expand` (a Settings `SettingsExpander` by `x:Name`, through its `ExpanderToggleButton`: it has
  no ExpandCollapse pattern), `CardButton` / `EditItem` (a list card's buttons), `Dialog` (an
  editor or dialog: while an editor is open the main window is hidden, so it is searched among
  PuppyMacro's top-level windows) and `TopWindow` (overlay panel, floating buttons).
- A test that sends input must be harmless: input goes only to a window of the test itself.
  The UI tests use F24 (no keyboard has it). The playback tests (`Macros`, `Loops`, `Remaps`,
  `Recording`, `Overlay`) really run loops, macros and remaps against `TargetWindow`: a topmost
  window of the test process at a fixed place inside the 1024x768 screen, which logs every key,
  button, wheel and move it receives with the time, and has a text box for pasted text. Its
  `Tap` / `Press` / `Click` check first that it is in front (`RequireForeground`), and every point
  a macro clicks or moves to is inside it (`TargetWindow.PointAt`, physical pixels). Hotkeys and
  sent keys are F13 to F24 (`Vk`); other keys (Ctrl+A, Enter, pasted text) only inside it. The
  overlay panel and floating buttons are placed over it, so a click PuppyMacro does not take lands
  on it. Times are checked with wide margins (`Playback.About`: 60% to 160% plus 250 ms), as the
  GitHub machines are slow; `TargetWindow.Quiet` waits a fixed time only to check that nothing
  more happens (a stopped loop presses nothing).
- In the playback tests, a UI Automation Invoke on the main window (a card's Play, Stop all) brings
  PuppyMacro to the front, as a user's click does. `Playback.PressCard` / `InvokeInMainWindow` bring
  the target window back right after; an item started that way waits about a second before its
  first input (what it sends before goes to PuppyMacro). Hotkeys do not change the window in front.
- FlaUI's `Mouse.MoveTo` sets the cursor position, which the low-level mouse hook never sees: moves
  that PuppyMacro must notice (recording) use `TargetWindow.MoveMouse` (SendInput). A floating
  button is clicked on its label: its window also holds the hotkey badge, so the window's middle
  is not the circle's. The recording bar's buttons have no name: find its one shown button.
- A test cannot press a hotkey with Ctrl, Alt, Shift or Win: PuppyMacro reads modifiers only from
  real key presses (`input.md`), and the key would reach the app in front. Set the hotkey to F24
  first (for example `SetOverlayModeHotkeyToF24` in `AppTests`), as the default ones have modifiers.
- `AppSession` takes the main window again until it shows its content: an element taken right
  after the window appears can keep showing no children.

## Which changes run the tests on GitHub

On a pull request or a push to `main`, the `test` workflow's jobs `unit_test` and `e2e_test` always start (they are
required checks), and their step `decide_if_tests_run` runs the tests only when a changed file can change their
result: a file the build of the projects reads, a file the tests read while running, or the
workflow itself. Otherwise the steps are skipped and the check passes. Called by `publish_release` or started by
hand (`workflow_dispatch`: `create_release_pr` on the release pull request, `gh workflow run test.yml --ref <branch>`),
they always run. Before it, `unit_test`'s `block_version_change` fails a pull request whose branch is not `release/…`
when it changes `PuppyMacro/Version.props` (`build-and-release.md`).

| Changed file | `unit_test` | `e2e_test` | Why |
|---|---|---|---|
| `PuppyMacro/**` | yes | yes | Both test projects reference the app; `ControlResourcesTests` reads its XAML files |
| `site/guide/*.html` (not `_page.html`), `site/guide/sections.txt` | yes | yes | Embedded in the app (`PuppyMacro.csproj`), checked through `get_help` (`AiToolsTests`, E2E `AiTests`) |
| `tests/PuppyMacro.Tests/**` | yes | no | The unit tests |
| `tests/PuppyMacro.E2E/**` | no | yes | The end-to-end tests |
| `global.json` | yes | yes | The `dotnet test` runner |
| `PuppyMacro.sln` | no | yes | `AppSession` finds the repository root by it |
| `.gitattributes` | yes | yes | Line endings of `.cs`, `.xaml`, `.csproj` at checkout |
| `.github/workflows/test.yml` | yes | yes | The workflow |

Nothing else is read by the builds or the tests (`.claude/`, the rest of `site/`, Markdown files,
`LICENSE`, `Makefile`, `scripts/`, the other workflows). When the app, a test project or a test
starts reading another file or folder, add it to this table and to the pattern of the job's
`decide_if_tests_run` step in the same pull request.

Do not filter the `test` workflow with `paths` / `paths-ignore`: a workflow that does not start leaves
its required check pending and blocks the merge (GitHub docs, "Troubleshooting required status
checks"), while a job or step skipped by `if` counts as passing.

## When to add and run tests

- New logic outside the UI (models, storage, migrations, input decisions): add unit tests in
  the same change. A settings migration always gets a test with an old file. Run `make test`
  before every commit.
- **A change that touches the UI** (XAML, windows, view models, what the user sees or does):
  add or update the end-to-end tests that cover it, then run all the end-to-end tests to
  check that nothing else broke. **Ask the user where to run them:**
  - **locally** (`make e2e`): faster, but they cannot use the PC meanwhile;
  - **on the branch** (GitHub): push the branch and start the `test` workflow on it with
    `gh workflow run test.yml --ref <branch>`, then `gh run watch`; the pull request also runs
    them. The PC stays free.
