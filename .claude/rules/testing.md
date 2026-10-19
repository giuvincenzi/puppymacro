# Tests

## Unit tests: `make test`

- `tests/PuppyMacro.Tests`: xUnit v3 on Microsoft Testing Platform (`global.json` sets the runner). It references the
  app; the app's `internal` types are visible to it.
- `make test` runs `dotnet test` in **Release**: the code users get, and a running dev build
  (which locks `bin/Debug`) does not block it.
- On GitHub they are the required check `unit_test` ("Which changes run the tests on GitHub" below), and
  `publish_release` runs them before a release is built.
- One test class per area, named `<Area>Tests`. A large area splits by aspect into classes named `<Area><Aspect>Tests`
  next to it (`ConditionCheckerEdgesTests` for the edge cases, `ComponentGridInvariantsTests` for invariants checked on
  generated cases); a class that only builds test data has no `Tests` in its name (`ComponentGenerator`). The screen recognition is tested without a screen: the graphics card's
  search on WARP (the software renderer, on every PC and on GitHub) against the processor's, and the conditions on a
  fake `IScreen`. Their speed on a real screen is measured by hand with the development build started with
  `PUPPYMACRO_BENCH=1` (a table of image and area sizes, processor and graphics card, text recognition) or
  `PUPPYMACRO_BENCH=load-gpu` / `load-cpu` (a search 30 times a second for a minute, to watch an app's frame rate):
  `Services/Screen/RecognitionBench.cs`, results in a text file it opens. The debug archive (`DebugStore.Root`, one
  folder for the process) is set to a temporary folder by the tests that write in it, which run one at a time
  (`[Collection(nameof(DebugStore))]`). The AI tools are tested with `FakeAiHost` as the app and the user
  (`AiTools.WaitStep` is short there); the file types' registry keys under a test key in `HKCU\Software\PuppyMacroTests`,
  deleted after.
- Components as functions (`Services/Components`) are tested case by case, every case that passes and every one that
  fails, in the `Component<Aspect>Tests` classes, with the builders of `ComponentTestKit` and the cases made by
  `ComponentGenerator` and `ComponentGridGenerator`. `ComponentPerformanceTests` measures the D4 macro made with
  functions and 1000 like it: the times are in the test's output.
- The unit tests run in English with en-US numbers whatever the PC's language (`TestCulture`, a module initializer); a
  test of another language sets it itself.
- Use `TempFolder` for files. A unit test that starts a loop makes it press F24 only. A paste's test gives the
  `ClipboardPaster` a clipboard of its own and the loop's paste keys of its own (`IPasteKeys`), never the PC's clipboard
  or a real Enter: the unit tests run while the user works.
- `ModifierTracker` is static, and `InputSender` reads it for every key it sends. A test class that changes it, or that
  sends keys and needs no other collection, resets it before and after each test and is in
  `[Collection(nameof(ModifierTracker))]`. That collection runs alone, after the others (`DisableParallelization` on
  its `CollectionDefinition`): a class has one collection only, so the classes that send keys from another one (those
  that write in the debug archive, `DebugStore`'s) never run while `ModifierTracker` changes.

## End-to-end tests: `make e2e`

- `tests/PuppyMacro.E2E`: xUnit v3 + FlaUI (UIA3). `AppSession` closes any running PuppyMacro and starts the development
  build on a fresh dev data folder with the samples, in English, so every test starts from the same data and finds
  controls by their English texts on any PC (another language through its constructor). With `Seed` it starts from the
  data the test writes instead, with no samples; a test that needs a global hotkey `Seed` leaves out sets it.
- On GitHub they are the required check `e2e_test` ("Which changes run the tests on GitHub" and "End-to-end tests in
  groups" below), and `publish_release` runs them before a release. The runner's screen is 1024x768: do not click
  controls that may be off screen there (for example the title bar of a tall window); use UI Automation patterns
  instead (`Window.Close()`, `Invoke`).
- `make e2e` runs them locally; it builds the dev build first. On one machine tests run one at a time (one
  PuppyMacro at a time): they share its desktop, mouse, keyboard, window in front and PuppyMacro's data folder.
- Every test class has a category, `[Trait("Category", ...)]`: `UI`, `Macros`, `Loops`, `Remaps`, `Recording`,
  `Overlay`, `AI`. The Library's (`LibraryTests`, `UI`) start from the development build's sample collections and the
  macros that link them (also "Type a word", a component with a parameter, and the macro that calls it). Dragging a file onto the window is not covered: FlaUI cannot drag from File Explorer reliably. In
  `AI` the test is the AI app: it starts `PuppyMacro.exe --mcp`, calls the tools and answers in PuppyMacro's windows
  (the ready window with Start, never Enter); its keys are F22 and F23, and F18 (the Record hotkey) ends a
  `record_macro`. The screen tools (`AiScreenTests`) look at, check, test and record on `TargetWindow`.
  `make e2e category=Macros` runs only one category: use it while working on that
  feature; before pushing, and on GitHub, all of them run.
- They take over the desktop: windows open and close, the mouse moves, keys are pressed.
  **Never run `make e2e` (or anything else that drives the UI, like `scripts/screenshots.ps1`, which
  brings PuppyMacro to the front) on a contributor's PC without asking first**: they cannot
  use the PC meanwhile, and their input can make the tests fail. Unit tests (`make test`) run
  in the background and can be run any time.
- Use the `AppSession` helpers. Find controls by their UI text (`Find`) or by `x:Name` (`FindById`). Wait with `Retry`
  and check `.Success`, never fixed sleeps. A UI Automation timeout (PuppyMacro's UI thread busy) is one more try, not
  a failure (`AppSession.NotYet`). A list's row is found by its accessible name (`ui.md`), never by the texts inside it.
  While an editor is open the main window is hidden, so editors and dialogs are searched among PuppyMacro's top-level
  windows.
- A test that sends input must be harmless: input goes only to a window of the test itself.
  The UI tests use F24 (no keyboard has it). The playback tests (`Macros`, `Loops`, `Remaps`,
  `Recording`, `Overlay`) really run loops, macros and remaps against `TargetWindow`, a topmost window of the test
  process that logs the input it receives (PuppyMacro's main window covers the whole 1024x768 screen, so it checks
  that a click point is its own). Its `Tap` / `Press` / `Click` check first that it is in front
  (`RequireForeground`), and every point a macro clicks or moves to is inside it (`TargetWindow.PointAt`, physical
  pixels). Hotkeys and sent keys are F13 to F24 (`Vk`); other keys (Ctrl+A, Enter, pasted text) only inside it. The
  overlay panel and floating buttons are placed over it, so a click PuppyMacro does not take lands
  on it. Times are checked with wide margins (`Playback.About`), as the
  GitHub machines are slow; `TargetWindow.Quiet` waits a fixed time only to check that nothing
  more happens (a stopped loop presses nothing).
- In the playback tests, a UI Automation Invoke on the main window (a card's Play, Stop all) brings
  PuppyMacro to the front, as a user's click does. `Playback.PressCard` / `InvokeInMainWindow` bring
  the target window back right after; an item started that way waits about a second before its
  first input (what it sends before goes to PuppyMacro). Hotkeys do not change the window in front.
- `WindowsFormsReady` (an xUnit assembly fixture) prepares Windows Forms before a process's first test: otherwise the
  first target window spent most of the test's wait on it on GitHub, and sometimes did not open.
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

On a pull request or a push to `main`, the `test` workflow's first job, `decide_which_tests_run`, looks at the changed
files and tells which tests can change their result: a file the build of the projects reads, a file the tests read while
running, or the workflow itself. The other tests are skipped: a job skipped by its `if` counts as passing for a
required check, and `e2e_test` passes when the end-to-end tests were not needed. Called by `publish_release` or started
by hand (`gh workflow run test.yml --ref <branch>`), every test runs. The push of a release merge skips them, as
`publish_release` runs them on that commit. A pull request whose branch is not `release/…`
fails when it changes `PuppyMacro/Version.props` (`build-and-release.md`).

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

`screenshots_test` takes the release's screenshots as `create_release_pr` does (saved only as an artifact) when a file
they depend on changes: a change that breaks them fails the pull request (`e2e_test` checks its result) instead of the
release. Not when `publish_release` calls the tests: the release's screenshots were taken by `create_release_pr`, and a
machine problem there would block the release for nothing in the app.

`readme_and_mcp_registry_test` checks the README against the home page's texts and validates the MCP Registry entry,
publishing nothing (`discoverability.md`); `e2e_test` checks its result, so it blocks the merge.

Nothing else is read by the builds or the tests (`.claude/`, the rest of `site/`, the other Markdown files,
`LICENSE`, `Makefile`, the other `scripts/`, the other workflows). When the app, a test project or a test
starts reading another file or folder, add it to this table and to the patterns of `decide_which_tests_run` in the
same pull request.

Do not filter the `test` workflow with `paths` / `paths-ignore`: a workflow that does not start leaves
its required check pending and blocks the merge (GitHub docs, "Troubleshooting required status
checks"), while a job or step skipped by `if` counts as passing.

## End-to-end tests in groups

On GitHub the end-to-end tests run on **`E2E_GROUPS` Windows machines at once**, written once at the top of
`.github/workflows/test.yml`. Every test runs in every pull request, exactly once; each machine runs its share one at a
time, as on a contributor's PC. The groups do not follow the categories: they stay even whatever the categories grow
into, and a new test needs nothing.

- Every machine lists the tests by full name, sorted the same way, numbered from 1; test n goes to the group of n
  modulo N (0 to group N). So every test is in exactly one group, and the groups differ by at most one test.
- The runner's own windows can stay in front of PuppyMacro and take a real mouse click (a test's "Add loop" click landed
  on one), so they are minimized first. Press buttons with UI Automation (`Invoke`) unless the test needs a real click.
- With `E2E_SCREENSHOTS` set (only on GitHub, never locally), `AppSession` saves at the end of every test the screen,
  the machine's state and what UI Automation sees.
- Each group saves the artifact **`e2e_group_<n>_attempt_<a>`** (`<a>`: the run's attempt, so a "Re-run failed jobs"
  does not mix with the failed one), also when tests failed.
- **`e2e_test` (the required check):** takes each group's newest attempt, then is green when the end-to-end tests were
  not needed, or when every group saw the same list, every test ran exactly once and every test and group passed.
  Otherwise red, with the missing, doubled or failed tests in the run's summary.

**When an end-to-end test fails on GitHub**, start from the test's code and the line that failed. Each group's artifact
(the run's page > Artifacts, or `gh run download <run> --name e2e_group_<n>_attempt_<a>`) holds what helps understand
why: screenshots, the UI Automation tree and the machine's state of every test, the failed ones apart, and the report.
**Its `README.txt` says what each file and folder is and how it can help**; it is written by the step
`describe_group_result` of `test.yml`, the one description of the artifact (change it there when the artifact changes).

**Changing the number of machines:** change only `E2E_GROUPS`. Nothing else: not the ruleset (it requires `e2e_test`,
not the groups), not the tests. Each group adds about a minute of fixed cost (machine, checkout, build), so more
groups help less and less.
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
