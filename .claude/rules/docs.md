# Documentation and website

The website is in `docs/`, published by GitHub Pages from the `main` branch, folder `/docs`:
`https://giuvincenzi.github.io/puppymacro/`.

- `docs/index.html`: home (features, install, code signing policy).
- `docs/guide.html`: user guide, one section per feature.
- `docs/assets/style.css`, `docs/assets/img/` (screenshots, logo), `docs/favicon.ico`.
- Plain HTML and CSS, no build step. `docs/.nojekyll` keeps GitHub Pages from processing it.

## Every user-visible change updates the documentation

In the same change as the code, for a new feature, changed behavior, new or renamed setting
or UI text:

- update the matching section of `docs/guide.html` (add a section for a new feature);
- update the feature cards in `docs/index.html` when the feature list changes;
- update `CHANGELOG.md` and, if behavior changed, `README.md`;
- replace screenshots that no longer match the app.

## Accuracy

- Describe only what the code does. Check the code before writing a sentence about
  behavior, defaults or limits.
- Use the exact UI texts from the XAML (button, setting and window names).
- Screenshots: from the development build with the sample data (`make dev-reset`, then
  `make dev`), capturing only PuppyMacro's window. Never show personal data, user names or
  other windows.
- The download link is `https://github.com/giuvincenzi/puppymacro/releases/latest/download/PuppyMacro-win-Setup.exe`:
  keep the Setup file name stable.
