# Tests

## Unit tests: `make test`

- `tests/PuppyMacro.Tests`: xUnit v3 (`xunit.v3.mtp-v2`) on Microsoft Testing Platform
  (`global.json` sets the `dotnet test` runner). It references the app project; the app's
  `internal` types are visible through `InternalsVisibleTo`.
- `make test` runs `dotnet test` in **Release**: the code users get, and a running dev build
  (which locks `bin/Debug`) does not block it.
- They run on GitHub on every push and pull request (`.github/workflows/test.yml`), and the
  Release workflow stops when they fail.
- Covered: settings loading and migrations (`SettingsStore`), macros from recorded events
  (`MacroBuilder`), `MacroLibrary`, macro groups and the editor's operations
  (`MacroEditList`), the single-action test macro (`MacroDefinition.ForTest`),
  `HotkeyConflicts`, `HotkeyRules`, `ModifierTracker`, `KeyNames`, `HotkeyBinding`, loop intervals,
  `FloatingButton` (labels, sanitizing, opacity, old files), the macro Code view's checks and
  schema (`MacroJson`, `MacroSchema`), and `LoopEngine`'s input decisions (keys held by a Hold
  down loop, wheel hotkeys, hotkey capture), driven through `OnKey` / `OnWheel` without hooks.
- Use `TempFolder` for files. `ModifierTracker` is static: reset it before and after each test,
  and put the test class in `[Collection(nameof(ModifierTracker))]` so they never run at the
  same time. A unit test that starts a loop makes it press F24 only.

## End-to-end tests: `make e2e`

- `tests/PuppyMacro.E2E`: xUnit v3 + FlaUI (UIA3). `AppSession` closes any running PuppyMacro,
  deletes `%AppData%\PuppyMacro Dev` and starts the development build
  (`PuppyMacro/bin/Debug/...`), so every test starts from the sample data.
- They run on GitHub (`.github/workflows/e2e.yml`) on every pull request (a required check for
  merging into `main`), on every push to `main` and before every release (the Release workflow
  stops when they fail). The runner's screen is 1024x768: do not click controls that may be off
  screen there (for example the title bar of a tall window); use UI Automation patterns
  instead (`Window.Close()`, `Invoke`).
- `make e2e` runs them locally; it builds the dev build first. Tests run one at a time (one
  PuppyMacro at a time).
- They take over the desktop: windows open and close, the mouse moves, keys are pressed.
  **Never run `make e2e` (or anything else that drives the UI, like screenshot captures that
  bring PuppyMacro to the front) on a contributor's PC without asking first**: they cannot
  use the PC meanwhile, and their input can make the tests fail. Unit tests (`make test`) run
  in the background and can be run any time.
- Find controls by their UI text (`Find`) or by `x:Name` (`FindById`). Wait with `Retry` and
  check `.Success`, never fixed sleeps.
- A test that sends input must be harmless: use F24 (no keyboard has it), never clicks or
  keys that could reach another app.

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
