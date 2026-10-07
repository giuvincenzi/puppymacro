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
  (`MacroBuilder`), `MacroLibrary`, `HotkeyConflicts`, `ModifierTracker`, `KeyNames`,
  `HotkeyBinding`, loop intervals.
- Use `TempFolder` for files. `ModifierTracker` is static: reset it before and after each test.

## End-to-end tests: `make e2e`

- `tests/PuppyMacro.E2E`: xUnit v3 + FlaUI (UIA3). `AppSession` closes any running PuppyMacro,
  deletes `%AppData%\PuppyMacro Dev` and starts the development build
  (`PuppyMacro/bin/Debug/...`), so every test starts from the sample data.
- `make e2e` builds the dev build first. Tests run one at a time (one PuppyMacro at a time).
- They move the mouse and press keys: nobody may use the PC while they run. They run only
  locally, not on GitHub (not verified on GitHub's runners).
- Find controls by their UI text (`Find`) or by `x:Name` (`FindById`). Wait with `Retry` and
  check `.Success`, never fixed sleeps.
- A test that sends input must be harmless: use F24 (no keyboard has it), never clicks or
  keys that could reach another app.

## When to add tests

- New logic outside the UI (models, storage, migrations, input decisions): add unit tests in
  the same change. A settings migration always gets a test with an old file.
- A new screen or user flow: add or extend an end-to-end test when it can run without
  touching other apps.
- Run `make test` before every commit; run `make e2e` before a release.
