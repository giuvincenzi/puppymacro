---
paths:
  - "site/**"
  - "README.md"
  - "scripts/site.ps1"
  - "scripts/check-readme.ps1"
  - "scripts/mcp-server-json.ps1"
  - ".github/workflows/test.yml"
  - ".github/workflows/publish_release.yml"
---

# How people find PuppyMacro

## Title and description

- PuppyMacro has one **title** and one **description**, written once in `site/home/en.json` (`title`; the description is
  `summary` + " " + `free_and_open_source`) and translated in `site/home/<code>.json`:
  - title: "Auto clicker, macros and hotkeys for PC. AI-ready";
  - description: "Automate clicks, keys and text, record macros and remap keys, all on hotkeys and from an overlay. Your AI
    app can set it all up. Free and open source."
- Who reads them:
  - the home page of every language (`scripts/site.ps1`): the page title ("PuppyMacro: " + title), the description for
    search engines, the link previews, the `SoftwareApplication` structured data and, without "Free and open source.",
    the sentence under the slogan;
  - the MCP Registry entry (`scripts/mcp-server-json.ps1`): title + ". Needs the PuppyMacro app installed.";
  - `README.md`: title + ". " + description between its "Title and description" comments, checked by the test workflow
    (`readme_and_mcp_registry_test`, `scripts/check-readme.ps1`);
  - the repository's About on GitHub: title + ". " + description, set by hand
    (`gh repo edit --description "…"`) whenever they change: the workflows' token has no Administration permission, and a
    personal token with it would be a secret only for this.
- Titles and descriptions say "PC" and never name Windows versions; the requirements say "Windows 10 / 11" (the home's badge, the structured data's
  `operatingSystem`, the guide's install page).
- The slogan is not the title: it stays in English in every language (only `en.json` has it, `site.ps1` stops when a
  translation adds it).

## Search engines

- Google Search Console: a "Domain" property for `gviam.app` in the maintainer's account, verified by a TXT record in
  the domain's DNS. The `google-site-verification` meta tag in `site/index.html` verifies the "URL prefix" property of
  the old address: keep it until the "Domain" property is verified.
- Bing Webmaster Tools: the site imported from Search Console (no tag of its own).
- `scripts/site.ps1` writes, with `$SiteUrl` as the one place the site's address is written: `sitemap.xml` (submitted
  in Search Console), a canonical link on every page (a
  guide page of any version points to the same page of the newest guide), `hreflang` links between the home pages
  (English as `x-default`), link previews and structured data.

## The home page's languages

- The home page is in the app's languages: English at the site's root, the others in a folder each, listed in
  `$HomeLanguages` of `site.ps1` with their flag and writing direction. Arabic runs right to left. The guide stays in
  English: its pages have the English header and no language menu.
- Every text of the home is in `site/home/<code>.json`, the same keys as `en.json` (except the slogan): `site.ps1` stops
  when a key is missing or added. Translations follow `i18n.md`.
- The language menu in the header shows flags from flag-icons (MIT, copied in `site/assets/flags` with its license),
  the United Kingdom's for English; Arabic, spoken in many countries, shows the letter "ع".
- The English home offers the browser's preferred language in a banner (`site.js`); the choice is remembered in the
  browser. The page never changes language by itself: search engines read the English one.

## MCP Registry

- PuppyMacro's entry in the official MCP Registry, `io.github.giuvincenzi/puppymacro`, has no package (the app is
  installed with its Setup): its description says the app is needed and its link is the guide's AI page.
  `scripts/mcp-server-json.ps1` builds it from `en.json` and `PuppyMacro/Version.props`, so its version is the app's;
  nothing of it is kept in the repository.
- `publish_release`'s job `publish_mcp_registry` publishes it at every release (`build-and-release.md`): a published
  version can never be changed and the same version cannot be published twice, so each release publishes its own, with
  the texts of that moment. The registry trusts GitHub's OIDC token for the `io.github.giuvincenzi/*` names: no secret.
- `readme_and_mcp_registry_test` (`testing.md`) builds the entry and checks it with the registry's own validation in every
  pull request that can change it.
- mcpservers.org lists it too, from its form (https://mcpservers.org/submit, free plan, with the registry name), once.

## By hand, after a release

- The first release with these changes: in Search Console, Verify, submit `sitemap.xml` and request indexing of the home;
  in Bing Webmaster Tools, import the site from Search Console; send the mcpservers.org form.
- When the title or description change: the repository's About (above).
