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

- Until the 1.7.1 release, GitHub Pages still serves the old single-page site from `docs/`
  (branch `main`, folder `/docs`). At that release: set Pages to "GitHub Actions"
  (`gh api -X PUT repos/giuvincenzi/puppymacro/pages -f build_type=workflow`), release, then
  delete `docs/` and this note.

- The site is published only by releases: the Release workflow runs the Site workflow after
  the GitHub Release. Pushing to `main` does not change the published site.
- Each guide version is built from `site/guide/` at the tag `vX.Y.Z` of that release, so a
  released guide never changes. Home, template and style come from the latest release.
  `guide/latest/` redirects to the newest version.
- `make docs` builds a preview in `site-preview/` with the guide of the current code as
  version "next". Released versions are added only when git is available to Windows
  PowerShell (in WSL it usually is not).

## Every user-visible change updates the documentation

In the same change as the code, for a new feature, changed behavior, new or renamed setting
or UI text:

- update the matching file in `site/guide/` (new feature: new file and a line in
  `sections.txt`);
- update the feature cards in `site/index.html` when the feature list changes;
- update `CHANGELOG.md` and, if behavior changed, `README.md`;
- replace screenshots that no longer match the app.

## Accuracy

- Describe only what the code does. Check the code before writing a sentence about
  behavior, defaults or limits.
- Use the exact UI texts from the XAML (button, setting and window names).
- Screenshots: from the development build with the sample data (`make dev`), capturing only
  PuppyMacro's window. Never show personal data, user names or other windows.
- The download link is `https://github.com/giuvincenzi/puppymacro/releases/latest/download/PuppyMacro-win-Setup.exe`:
  keep the Setup file name stable.
