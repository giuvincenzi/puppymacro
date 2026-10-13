# Documentation and website

Website: `https://giuvincenzi.github.io/puppymacro/`. Sources in `site/`, built by
`scripts/site.ps1`, published to GitHub Pages by the `publish_release` workflow (job `publish_site`).

- `site/index.html`: home (features, privacy). `{{VERSION}}` is the latest
  released version.
- `site/_header.html`, `site/_footer.html`: shared header and footer (`{{ROOT}}` is the path
  to the site root).
- `site/guide/sections.txt`: guide sections in order (`file|Title`). One file per section,
  `site/guide/<file>.html`, with the section content only; `site/guide/_page.html` is the
  page around it (sticky sidebar, version select, previous/next).
- `site/guide/img/`: guide screenshots. `site/assets/`: style, script, logo, home screenshot.

## Versions and publishing

- GitHub Pages is set to "GitHub Actions". The site is published only by releases: `publish_release` runs its job
  `publish_site` after the GitHub Release (`build-and-release.md`). Pushing to `main` does not change the published
  site.
- Each guide version is built from `site/guide/` at the tag `vX.Y.Z` of that release, so a
  released guide never changes. Home, template and style come from the latest release.
  `guide/latest/` redirects to the newest version.
- `make docs` builds a preview in `site-preview/` with the guide of the current code as
  version "next". `make docs serve=1` builds it and serves it at `http://localhost:8080/`
  (`scripts/site-serve.ps1`, .NET `HttpListener`), so links work as on the real site.
  Released versions are added only when git is available to Windows PowerShell (in WSL it
  usually is not).

## README

`README.md` holds only: the links (website, download, user guide), a short description of
the app, the download, "Development: see `.claude/rules/`" and the license with the list of
third-party components the app ships (NuGet packages in `PuppyMacro/PuppyMacro.csproj` and their
dependencies, files in `PuppyMacro/Assets/`), with the attribution their licenses ask for. Everything for
developers goes in `.claude/rules/`; how to use the app goes in the user guide.

## Every user-visible change updates the documentation

The app is translated (`i18n.md`); the website and the guide are only in English and use the English UI texts.

In the same change as the code, for a new feature, changed behavior, new or renamed setting
or UI text:

- translate every new or changed UI text into every language (`Strings.<code>.resx`, `i18n.md`);

- update the matching file in `site/guide/` (new feature: new file and a line in
  `sections.txt`);
- update the feature cards in `site/index.html` when the feature list changes;
- add a line under `## Unreleased` in `CHANGELOG.md`, in the right subsection (see
  `build-and-release.md`);
- screenshots: every release retakes all of them (below); a **new** screenshot is added to `scripts/screenshots.ps1`
  in the same change, so the next release takes it;
- update the AI tools (`PuppyMacro/Services/Ai/`, `architecture.md`), so AI apps can use the
  change too. New fields of loops, macros and remaps reach them through the Code views' checks and
  `CodeSchema` (`data.md`), and `get_help` reads the guide; the rest is not automatic:
  - a new or changed setting: `AiSettingsValues` (`get_settings` / `update_settings` give only
    the settings listed there), or the reason it stays out;
  - the `Instructions` and the tools' descriptions and schemas in `AiTools` when what the model
    must know or do changes;
  - a new tool when the feature adds something an AI app cannot do with the existing ones (a new
    kind of item, a new interaction with the user), or the reason it is left out (for example,
    starting loops and macros is not a tool on purpose);
  - `AiToolsTests` for every change above (`testing.md`).

The plan given to the user before implementing (`working-rules.md`) lists every item of this
checklist, each marked as needed or not needed with the reason: the guide file, the feature
cards in `site/index.html`, the `CHANGELOG.md` line, a new screenshot by file name and the AI
tools (settings, instructions and descriptions, new tools, tests). The plan also lists the rule changes
(`working-rules.md`).

## Accuracy

- Describe only what the code does. Check the code before writing a sentence about
  behavior, defaults or limits.
- Use the exact UI texts from the XAML (button, setting and window names).
- Screenshots: taken only by the `create_release_pr` workflow, for every release, on a GitHub Windows machine with
  its screen at 1920 x 1080 (`build-and-release.md`), and checked by the user in the release pull request: no
  pull request retakes them and there is no `make` for them. `scripts/screenshots.ps1` takes the guide's editor
  screenshots (`site/guide/img/`: `loop-editor.png`, `macro-editor.png`, `macro-editor-code.png`, `remap-editor.png`)
  and the home page's `site/assets/loops.png`, the whole main window with its title bar, from the development build
  started with `PUPPYMACRO_SCREENSHOTS=1` (`scripts/dev.ps1 -Screenshots`, `App.ShowsDevMarks`): the sample data, but no
  strip and no DEV, and the first sample loop enabled, so every screenshot looks like a release. Each window is
  captured alone (`PrintWindow`: nothing behind it shows), with its rounded corners transparent, at 100% scale, in
  Windows' default accent color. Never show personal data, user names or other windows. A new screenshot is added
  there, taken the same way.
- The download link is `https://github.com/giuvincenzi/puppymacro/releases/latest/download/PuppyMacro-win-Setup.exe`:
  keep the Setup file name stable.
