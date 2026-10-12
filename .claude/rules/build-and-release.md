# Build and release

## Requirements

- Windows 10 or 11 (x64): the app runs only on Windows.
- **.NET 10 SDK** (`winget install --id Microsoft.DotNet.SDK.10 --exact`).
- **GNU make**: usually already in WSL; on Windows `winget install --id ezwinports.make --exact`.
- **GitHub CLI** (`gh`), signed in: pull requests, starting workflows (`create_release_pr`, the tests on a branch).

## Commands

- `Makefile`, which runs the PowerShell scripts in `scripts/`. Options are make variables, lowercase
  (`make dev keep=1`): an option written `--keep` would be taken by make itself (GNU make accepts abbreviations of its
  own options, `--keep` is `--keep-going`).
  - `make dev`: closes a running PuppyMacro, deletes the dev data folder, builds Debug and
    starts it, so every run starts from the samples. Use it to try changes. `make dev keep=1` keeps the dev data
    (to check that something stays saved).
  - `make build`: the distribution in `dist/` (Velopack: `PuppyMacro-win-Setup.exe`, full
    package, `releases.win.json`) for the version in `PuppyMacro/Version.props`. Nothing is uploaded.
  - `make test`: unit tests; `make e2e`: end-to-end tests, `make e2e category=Macros` one category (see `testing.md`).
  - `make docs`: website preview in `site-preview/`; `make docs serve=1` also serves it at `http://localhost:8080/`
    (see `docs.md`).
  - `make file-icons`: draws the icons of `.puppyitems` and `.puppymacro` files again into
    `PuppyMacro/Assets/FileIcons` (`scripts/file-icons.ps1`, WPF shapes; `preview=<folder>` also writes each size as a
    PNG). Run it after changing their shapes and commit the `.ico` files.
  - `make clean`.
- There is no `make` for releases or screenshots: GitHub workflows do them (Releases below).
- From WSL the Makefile calls Windows PowerShell (`powershell.exe`), so builds use the Windows
  .NET SDK and the app starts on Windows. The repository is then on a network path
  (`\\wsl.localhost\...`): that is why `vpk` is installed in `.tools/` instead of a dotnet tool
  manifest (manifests on network paths are refused).
- Always build after changes and fix all errors and warnings you introduced.

## Dev and prod builds

- **Prod** (`make build`, the `publish_release` workflow): built in Release and packaged with Velopack.
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
- Signing happens only in the `publish_release` workflow, on `main`, when a release is published: its
  `publish_app` job uses the `signing` environment and `-RequireSigning`, so a release is never published
  unsigned. The environment allows only the `main` branch (Settings > Environments > `signing` >
  Deployment branches and tags), so no other branch or pull request can use the secrets. To try a
  change to signing before a release, build signed locally (below).
- Code merged into `main` runs in the `publish_release` workflow with the certificate. Before merging a pull
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

## Workflows

GitHub Actions, in `.github/workflows/`. Workflow, job and step names are lowercase with underscores; each says what
it does (the workflow the phase, the job the action).

| Workflow | Starts | Jobs |
|---|---|---|
| `test` | every pull request and push to `main`; `workflow_dispatch` (`create_release_pr`, or `gh workflow run test.yml --ref <branch>`); `workflow_call` (`publish_release`) | `unit_test`, `e2e_test` (`testing.md`) |
| `create_release_pr` | only by hand (`workflow_dispatch`) on `main` | `prepare_and_open_pr` |
| `publish_release` | a push to `main` that changes `PuppyMacro/Version.props` | `run_tests` (calls `test`), `publish_app`, `publish_site` |

- Repository settings they need (Settings > Actions > General > Workflow permissions): the default token stays read
  only (each workflow declares its own `permissions`), and **Allow GitHub Actions to create and approve pull requests**
  is on, for `create_release_pr`.
- The ruleset "Protect main" requires the checks `unit_test` and `e2e_test` (`git-workflow.md`): renaming those jobs
  needs the ruleset changed in the same moment, or no pull request can be merged.

## Releases

Releases are deterministic: never edit the version number or move CHANGELOG entries by hand.
The version lives only in `PuppyMacro/Version.props` (`<Version>`, imported by the csproj); the app reads it at
runtime (`App.DisplayTitle`). It has a file of its own so that only a release changes it.

### Between releases
- `CHANGELOG.md` starts with `## Unreleased`, split into `### Added`, `### Changed`,
  `### Fixed`. Every user-visible change adds its line there, in its own pull request.
- A normal pull request never changes `PuppyMacro/Version.props`: the `test` workflow's `block_version_change` fails a
  pull request from a branch that is not `release/…` when it does.

### Making a release
1. **`create_release_pr`**, only when the user asks for a release: Actions > `create_release_pr` > Run workflow on
   `main`, or, when the user asks Claude to prepare the release pull request, `gh workflow run create_release_pr.yml
   --ref main`, then `gh run watch` and the pull request's link. On a GitHub Windows machine, its job
   `prepare_and_open_pr`:
   - `stop_if_nothing_to_release`: stops when `## Unreleased` has no entries or a release pull request is already open;
   - `compute_next_version`: the latest tag plus a minor step with Added or Changed entries, a patch step with Fixed only;
   - `write_changelog_section`: the Unreleased entries move under `## X.Y.Z`, an empty `## Unreleased` stays above;
   - `write_version_file`: X.Y.Z in `PuppyMacro/Version.props`;
   - `set_screen_to_1920x1080` (`scripts/screen-resolution.ps1`: the runner's 1024 x 768 is smaller than the editors;
     it stops when the screen stays smaller) and `take_screenshots` (`scripts/dev.ps1 -Screenshots`, then
     `scripts/screenshots.ps1`): the guide's and the home page's screenshots (`docs.md`);
   - `commit_and_open_pr`: the branch `release/X.Y.Z` and the pull request "Release X.Y.Z";
   - `start_tests_on_pr`: a pull request opened with the workflow's token starts no workflow, so it starts `test` on
     the branch (its checks are on the pull request's commit).
   The functions are in `scripts/release.ps1`.
2. The user checks the pull request (version, CHANGELOG, screenshots) and approves the merge.
3. **`publish_release`** starts with the merge: merging adds a commit to `main` (a push), and GitHub starts the
   workflow because that commit changes `PuppyMacro/Version.props` (`paths`); for any other push it does not start at
   all. Its jobs: `run_tests` (the `test` workflow), `publish_app` (`read_version_and_notes` from `Version.props` and
   the CHANGELOG section, `build_signed_installer` with `scripts/build.ps1`, `publish_github_release`: the GitHub
   Release, GitHub creates the tag `vX.Y.Z` on the merge commit), then `publish_site` (`docs.md`).

A failed job is retried with **Re-run failed jobs** on its run.
