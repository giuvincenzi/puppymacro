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
  - `make screenshots`: `make dev` started with `PUPPYMACRO_SCREENSHOTS=1` (no strip, no DEV), then retakes the guide's editor screenshots and the home
    page's screenshot (drives the UI, see `docs.md`), then closes PuppyMacro (the process, also
    after an error).
  - `make file-icons`: draws the icons of `.puppyitems` and `.puppymacro` files again into
    `PuppyMacro/Assets/FileIcons` (`scripts/file-icons.ps1`, WPF shapes; `PREVIEW=<folder>` also writes each size as a
    PNG). Run it after changing their shapes and commit the `.ico` files.
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
  "DEV" in the title and tray tooltip (not when started with `PUPPYMACRO_SCREENSHOTS=1`), use their own data folder `%AppData%\PuppyMacro Dev`
  (`AppPaths`; WebView2 data in `%LocalAppData%\PuppyMacro Dev`) and fill it with disabled
  samples when empty (`Services/DevSampleData.cs`, Debug only), enable the Code view's DevTools,
  never touch the Start with Windows Run entry nor the file types (`Services/FileTypes.cs`) and skip the pre-1.6 data
  migration. A `.puppyitems` path on the command line opens Import in both.
  Release builds never do any of this. Keep the samples working when models change.
- **Smart App Control** (Windows 11) blocks every exe and dll that is unsigned and unknown, with no
  "Run anyway" and **no exceptions** for apps or folders (Microsoft: there is no way to bypass it for
  individual apps). On a development PC it blocks `make dev`, `make test` (`PuppyMacro.Tests.exe`),
  `make e2e` and `make build` (`vpk`): **development needs it off** (Settings > Privacy & security >
  Windows Security > App & browser control > Smart App Control settings). On Windows 11 updated to
  April 2026 or later it can be turned back on without reinstalling Windows.
- Signing development builds does not solve it: tests and tools include other people's unsigned
  files, and the release certificate must not sign them. A PC that must keep application control on
  needs an App Control for Business policy with its own rules, not Smart App Control.
- Released versions are signed (Code signing below), so the installed PuppyMacro runs with Smart
  App Control on.

## Code signing

- Releases are signed with a **Certum Open Source Code Signing** certificate (SimplySign cloud, no
  hardware token) issued to Giuseppe Vincenzi as a private person, valid at most 459 days: renew it
  on Certum before it expires. The user guide's install page (`site/guide/install.html`) says releases
  are signed; the site has no code signing policy page (only SignPath asked for one).
- **The Open Source certificate is for non-commercial open source only**: Certum revokes it if it
  signs software distributed commercially. Never ship ads, paid features or paid versions signed
  with it; that needs a commercial certificate first. Voluntary donations that unlock nothing are fine.
- `scripts/build.ps1` signs through Velopack (`vpk pack --signTemplate`) with
  [ssign](https://github.com/Le-Syl21/ssign), a client of Certum's SimplySign cloud API, when
  `CERTUM_EMAIL` and `CERTUM_OTP` (or `CERTUM_TOKEN`) are set. Velopack skips files already signed
  and trusted (Microsoft's) and signs the rest: PuppyMacro's exe and dlls, `Update.exe` and Setup,
  with a timestamp from `time.certum.pl`. Then the script checks every exe and dll of the full
  package and Setup with `Get-AuthenticodeSignature` and fails on any invalid signature, on our
  files without a timestamp, or when `PuppyMacro.exe` or Setup are not signed by the publisher.
  It deletes ssign's session file afterwards. Without the variables it builds unsigned (a warning);
  with `-RequireSigning` it fails instead.
- ssign receives the signing secrets, so it is **pinned** (`$SsignVersion`, `$SsignSha256`) and
  downloaded from its GitHub release only when the SHA-256 matches. Updating it: review the new
  version's source (network calls, what it does with the seed), then change both values.
- Secrets: the GitHub environment **`signing`** (repository Settings > Environments) holds
  `CERTUM_EMAIL` (the SimplySign account) and `CERTUM_OTP` (the TOTP seed of SimplySign: the
  `secret` of the `otpauth://` QR code shown when SimplySign is activated). The seed signs as the
  certificate owner until the QR code is reissued: never put it anywhere else. To replace it,
  reissue the QR code in SimplySign and update the secret.
- Signing happens only in the Release workflow, on `main`, when a release is published: its
  release job uses the `signing` environment and `-RequireSigning`, so a release is never published
  unsigned. The environment allows only the `main` branch (Settings > Environments > `signing` >
  Deployment branches and tags), so no other branch or pull request can use the secrets. To try a
  change to signing before a release, build signed locally (below).
- Code merged into `main` runs in the Release workflow with the certificate. Before merging a pull
  request from someone else, check above all what it changes in `scripts/` and `.github/workflows/`.
  Pull requests from forks never get the secrets, and their workflows wait for approval the first
  time a contributor opens one.
- **The repository is public: never publish secrets anywhere in it**, on any branch, in any commit,
  pull request, issue, comment or workflow log: passwords, tokens, keys, the SimplySign seed, codes
  or links, certificate files, personal documents. Secrets go only in GitHub's encrypted secrets.
- Locally (`make build`): set `CERTUM_EMAIL` and `CERTUM_TOKEN` (a 6-digit code from the SimplySign
  app) in the shell to try a signed build; the code is used once to log in. In WSL the Makefile
  passes them to Windows PowerShell through `WSLENV`.
- A new certificate starts with no reputation: SmartScreen can still warn on Setup until enough
  people download signed releases.

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
