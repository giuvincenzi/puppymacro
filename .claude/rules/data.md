---
paths:
  - "PuppyMacro/Models/**"
  - "PuppyMacro/Services/{SettingsStore,MacroLibrary,MacroJson,MacroSchema,AppPaths,BackupService,DevSampleData}.cs"
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
- The macro file's format is `MacroJson.Options` (`MacroLibrary` uses it). The editor's Code view
  shows that exact text and reads it back strictly (`MacroJson.StrictOptions` and checks): keep
  `MacroJson`'s limits equal to the editor's fields and `MacroSchema`'s descriptions up to date
  when the models change.
- Versions before 1.6 kept the data next to the exe; `AppPaths.MigrateFromExeFolder` copies it
  to the data folder on first start (Release builds only).
- `SettingsStore.Sanitize` validates and **migrates** old files. When the settings format
  or a default changes for existing users, bump `AppSettings.CurrentSchemaVersion` and add
  the migration there. Never break existing user data.
- Schema 6 renamed "game mode" to the overlay: `GameModeHotkey`, `GameModeOpacity`, `GameModeX/Y`
  (and v1.0's `GameModeHotkeyVk`) are read once through the `Legacy*` properties of
  `AppSettings` and saved as `OverlayModeHotkey`, `OverlayPanelOpacity`, `OverlayPanelX/Y`.
  `FloatingButton.Opacity` is null in older files: the first start fills it with the panel's
  opacity (loops in `SettingsStore`, macros with `MacroLibrary.FillButtonOpacity` when
  `AppSettings.LoadedSchemaVersion` < 6); afterwards null means 85%. New installs get Stop all
  Alt+Shift+S and Overlay mode Alt+Shift+W (`AppSettings.CreateDefault`); existing files keep
  theirs.
- Export / Import: `.puppymacro` zip (`BackupService`); Import keeps a backup and restarts
  the app (`--restart`).
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`). Keep
  the samples working when models change.
