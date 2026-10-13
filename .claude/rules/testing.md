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
  `AiTools.WaitStep` is short there; the language is left to the user; `AiJsonTests`: compact JSON read back as the same item, every JSON Patch operation and
  its errors, `SetWhere`; `AiCallLogTests`; the size limits of the activity and the History), the AI apps'
  settings files (`AiAppsTests`: Claude Desktop with its Microsoft Store copy, VS Code's link and `mcp.json`, Codex's
  `config.toml`, never replacing a file with an error), `AiActivityLog` (requests as phrases, older files) and `AllowAiAccess` in older files, `.puppyitems`
  files (`ItemFileTests`: written and read back as new disabled items, refused files, a newer format or version, an item with
  problems, names with a number, hotkeys left out also between the file's items, remaps coming in without a hotkey taken or
  missing, the chosen items only, file names; the AI tools `export_items`, `import_items`, `open_import_window`, `open_export_window` in `AiToolsTests`), the file
  types' registry keys (`FileTypesTests`, under a test key in `HKCU\Software\PuppyMacroTests`, deleted after; also that
  every image of the file icons is a PNG of its size), the XAML files
  (`ControlResourcesTests`: every `NumberBox` in a `SettingsCard` sets its `MinWidth`, `ui.md`), the languages (`LanguageTests`:
  the menu's order, which language and format each setting and Windows give, Gregorian dates in Arabic, every plural rule;
  `ResourcesTests`: every `Strings.<code>.resx` has every text with the same placeholders and its plural forms, the global words as they are (none of the global labels), no text written in the XAML (only resources, global words, single letters or digits, F keys), distinct sound
  names, and every key the code names exists, `i18n.md`) and the History's phrases and older files (`ItemHistoryTests`).
- The unit tests run in English with en-US numbers whatever the PC's language (`TestCulture`, a module initializer); a
  test of another language sets it itself.
- Use `TempFolder` for files. `ModifierTracker` is static: reset it before and after each test,
  and put the test class in `[Collection(nameof(ModifierTracker))]` so they never run at the
  same time. A unit test that starts a loop makes it press F24 only.

## End-to-end tests: `make e2e`

- `tests/PuppyMacro.E2E`: xUnit v3 + FlaUI (UIA3). `AppSession` closes any running PuppyMacro,
  deletes `%AppData%\PuppyMacro Dev` and starts the development build
  (`PuppyMacro/bin/Debug/...`) in English (`PUPPYMACRO_LANGUAGE`, `new AppSession(language: "ar")` for another one),
  so every test starts from the sample data and finds controls by their English texts on any PC. `new AppSession(seed => ...)`
  starts it from the data the test writes instead (`Seed`: the app's models, referenced by the
  project; no samples, global hotkeys Stop all F19, Overlay mode F24, Record F18).
- They run on GitHub in the `test` workflow, split over several machines at once (`e2e_group_1`, `e2e_group_2`, …;
  "End-to-end tests in groups" below), checked by the job `e2e_test`: on every pull request (a required check for
  merging into `main`), on every push to `main` and in `publish_release` before every release (it stops when they
  fail). The push of a release merge skips them, as `publish_release` runs them on that commit: the job
  `decide_which_tests_run` skips only when `github.workflow` is `test` (called by `publish_release` it is
  `publish_release`, the caller's github context). The runner's screen is 1024x768: do not click controls that may be off
  screen there (for example the title bar of a tall window); use UI Automation patterns
  instead (`Window.Close()`, `Invoke`).
- `make e2e` runs them locally; it builds the dev build first. On one machine tests run one at a time (one
  PuppyMacro at a time): they share its desktop, mouse, keyboard, window in front and PuppyMacro's data folder.
- Every test class has a category, `[Trait("Category", ...)]`: `UI` (`AppTests`: windows, pages,
  editors, Export's choices, Export of items with search (from the title bar's More options), a `.puppyitems` file opened
  while PuppyMacro runs (a second `PuppyMacro.exe` with its path, as File Explorer starts it; the file dialogs are never
  opened), a remap imported without its hotkey and its card, two files opened one after the other (one Import, the last
  file); dragging a file onto the window is not covered: FlaUI cannot drag from File Explorer reliably), `Macros` (`MacroActionTests`, `MacroRunTests`), `Loops`, `Remaps`, `Recording`,
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
  PuppyMacro's top-level windows) and `TopWindow` (overlay panel, floating buttons). A list's row is found by its
  accessible name (`ui.md`), never by the texts inside it: `CardButton` in the main window, `PanelRow` / `PanelRowIs`
  in the overlay panel, `HasRow` in any list.
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
- `WindowsFormsReady` (an xUnit assembly fixture, once per test process before its first test) builds the target window's
  form without showing it: the first target window of a process spent up to 7 s on GitHub preparing Windows Forms inside
  the test's 10 s wait, and sometimes did not open. Each `.uia.txt` says how long both took (`Target window:`).
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

On a pull request or a push to `main`, the `test` workflow's first job, `decide_which_tests_run` (on Linux, quick),
looks once at the changed files and tells which tests can change their result: a file the build of the projects reads,
a file the tests read while running, or the workflow itself. `unit_test` and the end-to-end groups run only when
needed; otherwise they are skipped (their Windows machine never starts): a job skipped by its `if` counts as passing
for a required check, and `e2e_test` passes when the end-to-end tests were not needed. Called by `publish_release` or started by hand (`workflow_dispatch`:
`gh workflow run test.yml --ref <branch>`), every test runs. `unit_test`'s first step,
`block_version_change`, fails a pull request whose branch is not `release/…` when it changes
`PuppyMacro/Version.props` (`build-and-release.md`): that file is under `PuppyMacro/`, so such a pull request always
runs `unit_test`.

| Changed file | `unit_test` | end-to-end | Why |
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
starts reading another file or folder, add it to this table and to the patterns of `decide_which_tests_run` in the
same pull request.

Do not filter the `test` workflow with `paths` / `paths-ignore`: a workflow that does not start leaves
its required check pending and blocks the merge (GitHub docs, "Troubleshooting required status
checks"), while a job or step skipped by `if` counts as passing.

## End-to-end tests in groups

On GitHub the end-to-end tests run on **`E2E_GROUPS` Windows machines at once** (7 today), written once at the top of
`.github/workflows/test.yml`. Every test runs in every pull request, exactly once; each machine runs its share one at a
time, as on a contributor's PC. The groups do not follow the categories: they stay even whatever the categories grow
into, and a new test needs nothing.

- **`decide_which_tests_run`, step `list_e2e_groups`:** turns `E2E_GROUPS` into the list `[1, 2, …, N]` (a matrix cannot
  read `env`).
- **`e2e_group` (one job, copied by the matrix into `e2e_group_1` … `e2e_group_N`, `fail-fast: false` so every broken test
  shows):**
  - `build_dev_app`: the E2E project, with the development build it starts;
  - `list_e2e_tests`: every test by its full name (`PuppyMacro.E2E.Class.Method`, from `dotnet test --list-tests`),
    sorted the same way on every machine (`LC_ALL=C sort`), numbered from 1;
  - `pick_group_tests`: test n goes to the group of n's remainder divided by N, the remainder 0 to group N. A number has
    one remainder, so every test is in exactly one group, and the groups differ by at most one test;
  - `minimize_other_windows`: the runner's own windows (its agent's console) can stay in front of PuppyMacro and take
    a real mouse click (a test's "Add loop" click landed on it), so they are minimized first. Press buttons with UI
    Automation (`Invoke`) unless the test needs a real click;
  - `log_machine_state`: the machine before the tests (memory, CPU, windows still shown);
  - `run_group_tests`: `dotnet test --filter-method` with the group's tests and a CTRF report; with `E2E_SCREENSHOTS` set
    (only here, never locally), `AppSession` saves at the end of every test the screen, the machine's state and what UI
    Automation sees (`SaveScreenshot`, `SaveUiaTree`, `MachineState`);
  - `keep_tests_details`: those files of every test in the artifact, the failed tests' ones also apart;
  - `describe_group_result`: the artifact's `README.txt`;
  - `save_group_result`: the artifact **`e2e_group_<n>_attempt_<a>`** (`<a>`: the run's attempt, so a "Re-run failed jobs"
    does not mix with the failed one), also when tests failed, kept 7 days.
- **`e2e_test` (the required check):** takes each group's newest attempt, then is green when the end-to-end tests were
  not needed, or when (1) every group saw the same list, (2) every test ran exactly once (none missing, none in two
  groups), (3) every test and every group passed. Otherwise red, with the missing, doubled or failed tests in the run's
  summary.

**When an end-to-end test fails on GitHub**, start from the test's code and the line that failed. Each group's artifact
(the run's page > Artifacts, or `gh run download <run> --name e2e_group_<n>_attempt_<a>`) holds what helps understand
why: screenshots, the UI Automation tree and the machine's state of every test, the failed ones apart, and the report.
**Its `README.txt` says what each file and folder is and how it can help**; it is written by the step
`describe_group_result` of `test.yml`, the one description of the artifact (change it there when the artifact changes).

**Changing the number of machines:** change only `E2E_GROUPS`. Nothing else: not the ruleset (it requires `e2e_test`,
not the groups), not the tests. Each group costs about a minute of its own (machine, checkout, build) on top of its
tests (about 7 s each), so more groups help less and less: with 66 tests, 7 groups take about 2 to 2.5 minutes, 10
about 2.
GitHub runs at most 20 jobs at once for the whole account (all pull requests and workflows together): the jobs over it
wait in a queue, they do not fail. Never rename `e2e_test` without changing the ruleset in the same moment
(`build-and-release.md`).

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
