# Working rules

- **English only in the project**: code, identifiers, comments, XAML text, UI labels,
  tooltips, messages, README, CHANGELOG, rules.
- **Ask before changing code.** Describe what you want to change and why, wait for an
  explicit OK, then write the code. This applies to small fixes and refactors too.
- **UI changes: show the UI first.** Propose the visual result (ideally with more than one
  option) and wait for a choice before implementing. Details of the look: `ui.md`.
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
