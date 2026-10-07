# Build and release

## Commands

- Requires a **.NET 10 SDK** and GNU make. The app runs only on Windows.
- `Makefile`, which runs the PowerShell scripts in `scripts/`:
  - `make dev`: closes a running PuppyMacro, builds Debug and starts it. Use it to try changes.
  - `make dev-reset`: deletes the dev data folder; the next `make dev` starts with the samples.
  - `make build`: the distribution in `dist/` (Velopack: `PuppyMacro-win-Setup.exe`, full
    package, `releases.win.json`) for the version in the csproj. Nothing is uploaded.
  - `make release`: runs the GitHub workflow `release.yml` (same `scripts/build.ps1`, then
    uploads the GitHub Release `vX.Y.Z`).
  - `make clean`.
- From WSL the Makefile calls Windows PowerShell (`powershell.exe`), so builds use the Windows
  .NET SDK and the app starts on Windows. The repository is then on a network path
  (`\\wsl.localhost\...`): that is why `vpk` is installed in `.tools/` instead of a dotnet tool
  manifest (manifests on network paths are refused).
- Always build after changes and fix all errors and warnings you introduced.

## Dev and prod builds

- Debug builds (`App.IsDevBuild`, `#if DEBUG`) show the orange "Development build" strip and
  "DEV" in the title and tray tooltip, use their own data folder `%AppData%\PuppyMacro Dev`
  (`AppPaths`), fill it with disabled samples when empty (`Services/DevSampleData.cs`, Debug
  only), never touch the Start with Windows Run entry and skip the pre-1.6 data migration.
  Release builds never do any of this. Keep the samples working when models change.
- Smart App Control (Windows 11) blocks unsigned builds it has no reputation for; development
  needs it off.

## Releases

- Version lives only in `PuppyMacro/PuppyMacro.csproj` (`<Version>`); the title bar and
  About read it at runtime (`App.DisplayTitle`). Semantic versioning: features = minor,
  fixes = patch.
- Each release: bump the version, update `CHANGELOG.md` (the release notes are its
  `## X.Y.Z` section; the workflow fails without it) and, if behavior changed, `README.md`.
  Then push and `make release`.
