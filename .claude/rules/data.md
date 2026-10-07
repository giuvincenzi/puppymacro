---
paths:
  - "PuppyMacro/Models/**"
  - "PuppyMacro/Services/{SettingsStore,MacroLibrary,AppPaths,BackupService,DevSampleData}.cs"
---

# Data and compatibility

- Data folder: `%AppData%\PuppyMacro` (`AppPaths`; `PuppyMacro Dev` for Debug builds).
  `settings.json` + `macros\{id}.json`.
- `SettingsStore.Sanitize` validates and **migrates** old files. When the settings format
  or a default changes for existing users, bump `AppSettings.CurrentSchemaVersion` and add
  the migration there. Never break existing user data.
- Export / Import: `.puppymacro` zip (`BackupService`); Import keeps a backup and restarts
  the app (`--restart`).
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`). Keep
  the samples working when models change.
