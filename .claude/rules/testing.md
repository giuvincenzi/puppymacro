# Tests

## Unit tests: `make test`

- `tests/PuppyMacro.Tests`: xUnit v3 (`xunit.v3.mtp-v2`) on Microsoft Testing Platform
  (`global.json` sets the `dotnet test` runner). It references the app project; the app's
  `internal` types are visible through `InternalsVisibleTo`.
- `make test` runs `dotnet test` in **Release**: the code users get, and a running dev build
  (which locks `bin/Debug`) does not block it.
- They run on GitHub on every pull request and every push to `main`
  (`.github/workflows/test.yml`), and the Release workflow stops when they fail.
- Covered: settings loading and migrations (`SettingsStore`), macros from recorded events
  (`MacroBuilder`), `MacroLibrary`, macro groups and the editor's operations
  (`MacroEditList`), the single-action test macro (`MacroDefinition.ForTest`),
  `HotkeyConflicts` (also per app and for remap sources), `HotkeyRules`, `ModifierTracker`, `KeyNames`, `HotkeyBinding`, what the key
  field shows (`KeyCaptureField.DisplayParts`), loop intervals, `FloatingButton` (labels,
  sanitizing, opacity, old files), the Code views' checks and schemas (`MacroJson`, `LoopJson`,
  `RemapJson`, `CodeSchema`, the groups an action can be in), `LoopEngine`'s input decisions (keys
  held by a Hold down loop, wheel hotkeys, hotkey capture, the overlay panel's Stop all and Move,
  hotkeys of items for a specific app and their stop when another app comes in front, through
  `OnForegroundApp`),
  driven through `OnKey` / `OnWheel` / `OnMouseDetail` without hooks, and the XAML files
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
- They run on GitHub (`.github/workflows/e2e.yml`) on every pull request (a required check for
  merging into `main`), on every push to `main` and before every release (the Release workflow
  stops when they fail). The runner's screen is 1024x768: do not click controls that may be off
  screen there (for example the title bar of a tall window); use UI Automation patterns
  instead (`Window.Close()`, `Invoke`).
- `make e2e` runs them locally; it builds the dev build first. Tests run one at a time (one
  PuppyMacro at a time).
- Every test class has a category, `[Trait("Category", ...)]`: `UI` (`AppTests`: windows, pages,
  editors), `Macros` (`MacroActionTests`, `MacroRunTests`), `Loops`, `Remaps`, `Recording`,
  `Overlay`. `make e2e CATEGORY=Macros` runs only one category: use it while working on that
  feature; before pushing, and on GitHub, all of them run.
- They take over the desktop: windows open and close, the mouse moves, keys are pressed.
  **Never run `make e2e` (or anything else that drives the UI, like screenshot captures that
  bring PuppyMacro to the front) on a contributor's PC without asking first**: they cannot
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

## When to add and run tests

- New logic outside the UI (models, storage, migrations, input decisions): add unit tests in
  the same change. A settings migration always gets a test with an old file. Run `make test`
  before every commit.
- **A change that touches the UI** (XAML, windows, view models, what the user sees or does):
  add or update the end-to-end tests that cover it, then run all the end-to-end tests to
  check that nothing else broke. **Ask the user where to run them:**
  - **locally** (`make e2e`): faster, but they cannot use the PC meanwhile;
  - **on the branch** (GitHub): push the branch and start the workflow on it with
    `gh workflow run e2e.yml --ref <branch>`, then `gh run watch`; the pull request also runs
    them. The PC stays free.
