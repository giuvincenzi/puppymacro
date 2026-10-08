# Documentation and website

Website: `https://giuvincenzi.github.io/puppymacro/`. Sources in `site/`, built by
`scripts/site.ps1`, published to GitHub Pages by `.github/workflows/site.yml`.

- `site/index.html`: home (features, install, code signing policy). `{{VERSION}}` is the latest
  released version.
- `site/_header.html`, `site/_footer.html`: shared header and footer (`{{ROOT}}` is the path
  to the site root).
- `site/guide/sections.txt`: guide sections in order (`file|Title`). One file per section,
  `site/guide/<file>.html`, with the section content only; `site/guide/_page.html` is the
  page around it (sticky sidebar, version select, previous/next).
- `site/guide/img/`: guide screenshots. `site/assets/`: style, script, logo, home screenshot.

## Versions and publishing

- GitHub Pages is set to "GitHub Actions". The site is published only by releases, which run
  only on `main`: the Release workflow runs the Site workflow after the GitHub Release. Pushing
  to `main` does not change the published site.
- Each guide version is built from `site/guide/` at the tag `vX.Y.Z` of that release, so a
  released guide never changes. Home, template and style come from the latest release.
  `guide/latest/` redirects to the newest version.
- `make docs` builds a preview in `site-preview/` with the guide of the current code as
  version "next". `make docs-serve` builds it and serves it at `http://localhost:8080/`
  (`scripts/site-serve.ps1`, .NET `HttpListener`), so links work as on the real site.
  Released versions are added only when git is available to Windows PowerShell (in WSL it
  usually is not).

## README

`README.md` holds only: the links (website, download, user guide), a short description of
the app, the download, "Development: see `.claude/rules/`" and the license. Everything for
developers goes in `.claude/rules/`; how to use the app goes in the user guide.

## Every user-visible change updates the documentation

In the same change as the code, for a new feature, changed behavior, new or renamed setting
or UI text:

- update the matching file in `site/guide/` (new feature: new file and a line in
  `sections.txt`);
- update the feature cards in `site/index.html` when the feature list changes;
- add a line under `## Unreleased` in `CHANGELOG.md`, in the right subsection (see
  `build-and-release.md`);
- replace screenshots that no longer match the app.

The plan given to the user before implementing (`working-rules.md`) lists every item of this
checklist, each marked as needed or not needed with the reason: the guide file, the feature
cards in `site/index.html`, the `CHANGELOG.md` line and each screenshot by file name.
Screenshots need the app running on the contributor's PC, so the plan names them and asks
before capturing (`testing.md`).

## Accuracy

- Describe only what the code does. Check the code before writing a sentence about
  behavior, defaults or limits.
- Use the exact UI texts from the XAML (button, setting and window names).
- Screenshots: from the development build with the sample data (`make dev`), capturing only
  PuppyMacro's window. Never show personal data, user names or other windows.
- The download link is `https://github.com/giuvincenzi/puppymacro/releases/latest/download/PuppyMacro-win-Setup.exe`:
  keep the Setup file name stable.
