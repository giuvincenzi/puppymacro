# Working rules

- **English only in the project**: code, identifiers, comments, XAML text, UI labels,
  tooltips, messages, README, CHANGELOG, rules.
- **Ask before changing code.** Describe what you want to change and why, wait for an
  explicit OK, then write the code. This applies to small fixes and refactors too.
  For a user-visible change the description includes the documentation checklist of
  `docs.md` (guide, home feature cards, CHANGELOG, screenshots), item by item.
- **UI changes: visual proposals first.** For any change to the UI, before writing code:
  1. Describe in text what would change, then ask the user whether they want visual
     proposals.
  2. If yes, make them as a claude.ai **Design** canvas artifact (Artifact tool, Design
     type): one artboard per option, more than one option when there is a real choice,
     drawn in the app's Fluent look (`ui.md`) with the sample data. Send the link.
     If no, wait for an OK on the text description.
  3. Wait for the user to choose and approve. Only then create the worktree and implement
     (`git-workflow.md`).
- **Say it first when a request cannot be done as asked.** If something cannot be done exactly as
  the user asked, or only in a different way (another control, another structure, a workaround),
  explain it and propose the options before changing anything; never implement the different way
  on your own.
- **Standard components only.** The UI uses the controls of iNKORE.UI.WPF.Modern as they are,
  laid out like Windows Settings: no custom templates, styles, colors, sizes or cursors. A custom
  part needs the user's approval for that case (`ui.md`).
- **Fix bugs at the root cause.** No workarounds or "patch" fixes. Explain the cause first.
- **Do not guess.** If something is uncertain (an API, a Windows behavior, a library
  version), check the source or documentation before relying on it, and say what was
  verified and what was not.
- **Project knowledge goes in the repository, not in personal memory.** Every rule, decision,
  convention or lesson about this project goes in `.claude/CLAUDE.md` or `.claude/rules/` (or
  the code, README, docs), so it holds for every contributor and every PC. Claude's personal
  memory on a contributor's PC is only for things that do not affect the project (for example,
  how that person likes explanations). When asked to "remember" something about the project,
  write or update a rule and commit it.
- **Do not assume the machine.** Rules in these files must hold on any contributor's PC:
  no user names, absolute paths or tools that only one machine has.
- No analogies or metaphors in explanations; be direct.
- When pointing to Windows settings or menus, use their English names.
- Do not lecture about game terms of service or anti-cheat.
