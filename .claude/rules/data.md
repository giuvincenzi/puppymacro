---
paths:
  - "PuppyMacro/Models/**"
  - "PuppyMacro/Services/{SettingsStore,MacroLibrary,AppPaths,BackupService,DevSampleData}.cs"
---

# Data and compatibility

- Data folder: `%AppData%\PuppyMacro` (`AppPaths`; `PuppyMacro Dev` for Debug builds):
  `settings.json` (settings, loops, remaps), `macros\{id}.json` (one file per macro),
  `backup-before-import-*.puppymacro` (copies made before an Import).
- A macro file has its actions in playback order; an action may have a `Name` and a
  `GroupId` pointing to one of the macro's `Groups` (name, collapsed). Groups are not nested
  and a group's actions are consecutive: `MacroDefinition.NormalizeGroups` enforces it when a
  macro is loaded (`MacroLibrary.Sanitize`) and saved by the editor. Playback ignores groups.
  Files without these fields load with no names and no groups.
- Versions before 1.6 kept the data next to the exe; `AppPaths.MigrateFromExeFolder` copies it
  to the data folder on first start (Release builds only).
- `SettingsStore.Sanitize` validates and **migrates** old files. When the settings format
  or a default changes for existing users, bump `AppSettings.CurrentSchemaVersion` and add
  the migration there. Never break existing user data.
- Export / Import: `.puppymacro` zip (`BackupService`); Import keeps a backup and restarts
  the app (`--restart`).
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`). Keep
  the samples working when models change.
