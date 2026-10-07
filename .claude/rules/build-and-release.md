# Build and release

## Requirements

- Windows 10 or 11 (x64): the app runs only on Windows.
- **.NET 10 SDK** (`winget install --id Microsoft.DotNet.SDK.10 --exact`).
- **GNU make**: usually already in WSL; on Windows `winget install --id ezwinports.make --exact`.
- **GitHub CLI** (`gh`), signed in: pull requests, `make release`, end-to-end tests on a branch.

## Commands

- `Makefile`, which runs the PowerShell scripts in `scripts/`:
  - `make dev`: closes a running PuppyMacro, deletes the dev data folder, builds Debug and
    starts it, so every run starts from the samples. Use it to try changes.
  - `make dev-keep`: same, keeping the dev data (to check that something stays saved).
  - `make build`: the distribution in `dist/` (Velopack: `PuppyMacro-win-Setup.exe`, full
    package, `releases.win.json`) for the version in the csproj. Nothing is uploaded.
  - `make test`: unit tests; `make e2e`: end-to-end tests (see `testing.md`).
  - `make docs`, `make docs-serve`: website preview in `site-preview/`, served locally by
    `docs-serve` (see `docs.md`).
  - `make release`: runs the GitHub workflow `release.yml` (same `scripts/build.ps1`, then
    uploads the GitHub Release `vX.Y.Z`, then publishes the website with `site.yml`).
  - `make clean`.
- From WSL the Makefile calls Windows PowerShell (`powershell.exe`), so builds use the Windows
  .NET SDK and the app starts on Windows. The repository is then on a network path
  (`\\wsl.localhost\...`): that is why `vpk` is installed in `.tools/` instead of a dotnet tool
  manifest (manifests on network paths are refused).
- Always build after changes and fix all errors and warnings you introduced.

## Dev and prod builds

- **Prod** (`make build`, `make release`): built in Release and packaged with Velopack.
  `PuppyMacro-win-Setup.exe` installs PuppyMacro for the current user in
  `%LocalAppData%\PuppyMacro` (no administrator rights), with Start menu and desktop shortcuts
  and an entry in Settings > Apps > Installed apps, and installs the .NET 10 Desktop Runtime if
  missing. The installed app updates itself from GitHub Releases (`updates.md`).
- Debug builds (`App.IsDevBuild`, `#if DEBUG`) show the orange "Development build" strip and
  "DEV" in the title and tray tooltip, use their own data folder `%AppData%\PuppyMacro Dev`
  (`AppPaths`), fill it with disabled samples when empty (`Services/DevSampleData.cs`, Debug
  only), never touch the Start with Windows Run entry and skip the pre-1.6 data migration.
  Release builds never do any of this. Keep the samples working when models change.
- Smart App Control (Windows 11) blocks unsigned files it has no reputation for, with no "Run
  anyway". Every build is a new file, so `make dev` builds can be blocked ("Part of this app
  has been blocked"): development needs it off (Settings > Privacy & security > Windows
  Security > App & browser control > Smart App Control settings). On Windows 11 updated to
  April 2026 or later it can be turned back on without reinstalling Windows.

## Releases

- Version lives only in `PuppyMacro/PuppyMacro.csproj` (`<Version>`); the title bar and
  About read it at runtime (`App.DisplayTitle`). Semantic versioning: features = minor,
  fixes = patch.
- **Between releases**, `CHANGELOG.md` starts with a `## Unreleased` section. Every
  user-visible change adds its line there, in its own pull request. A change never touches the
  version number.
- **A release** is decided by the user. Its pull request:
  - chooses the version from what is under `## Unreleased`: patch if it holds only fixes,
    minor if it holds at least one new feature;
  - renames `## Unreleased` to `## X.Y.Z` and adds a new empty `## Unreleased` above it;
  - sets `<Version>` in the csproj.
  After the user approves and it is merged, `make release` (or Actions > Release > Run
  workflow) starts the Release workflow on `main`: `make release` only starts it, all the work
  runs on GitHub. The workflow fails if the tag already exists, if `## X.Y.Z` is missing or if
  entries are left under `## Unreleased`. Nothing is released automatically on merge.
