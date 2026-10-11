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
  - `make screenshots`: `make dev`, then retakes the guide's editor screenshots and the home
    page's screenshot (drives the UI, see `docs.md`), then closes PuppyMacro (the process, also
    after an error).
  - `make release-pr`, `make release`: see Releases below.
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
  and an entry in Settings > Apps > Installed apps, and installs the .NET 10 Desktop Runtime and
  the Microsoft Edge WebView2 Runtime if missing. The installed app updates itself from GitHub Releases (`updates.md`).
- Debug builds (`App.IsDevBuild`, `#if DEBUG`) show the orange "Development build" strip and
  "DEV" in the title and tray tooltip, use their own data folder `%AppData%\PuppyMacro Dev`
  (`AppPaths`; WebView2 data in `%LocalAppData%\PuppyMacro Dev`) and fill it with disabled
  samples when empty (`Services/DevSampleData.cs`, Debug only), enable the Code view's DevTools,
  never touch the Start with Windows Run entry and skip the pre-1.6 data migration.
  Release builds never do any of this. Keep the samples working when models change.
- Smart App Control (Windows 11) blocks unsigned files it has no reputation for, with no "Run
  anyway". Every build is a new file, so `make dev` builds can be blocked ("Part of this app
  has been blocked"): development needs it off (Settings > Privacy & security > Windows
  Security > App & browser control > Smart App Control settings). On Windows 11 updated to
  April 2026 or later it can be turned back on without reinstalling Windows.

## Releases

Releases are deterministic: never edit the version number or move CHANGELOG entries by hand.
The version lives only in `PuppyMacro/PuppyMacro.csproj` (`<Version>`); the app reads it at
runtime (`App.DisplayTitle`).

### Between releases
- `CHANGELOG.md` starts with `## Unreleased`, split into `### Added`, `### Changed`,
  `### Fixed`. Every user-visible change adds its line there, in its own pull request.
- A normal pull request never changes `<Version>` in `PuppyMacro/PuppyMacro.csproj`.

### Making a release
1. `make release-pr`, only when the user asks for a release: computes the next version from
   the latest tag and `## Unreleased` (minor if Added/Changed, patch if only Fixed), prepares
   the CHANGELOG and the csproj, and opens the pull request "Release X.Y.Z". Details:
   `scripts/release-pr.ps1`.
2. The user approves the merge.
3. The merge publishes it: the Release workflow tests, builds, creates the GitHub Release
   (GitHub creates the tag `vX.Y.Z`) and updates the website. Details:
   `.github/workflows/release.yml`.

`make release` only retries a release that failed; it never republishes an existing tag.
