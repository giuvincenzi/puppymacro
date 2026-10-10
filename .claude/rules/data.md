---
paths:
  - "PuppyMacro/Models/**"
  - "PuppyMacro/Services/{SettingsStore,MacroLibrary,CodeJson,MacroJson,LoopJson,CodeSchema,AppPaths,BackupService,DevSampleData}.cs"
---

# Data and compatibility

- Data folder: `%AppData%\PuppyMacro` (`AppPaths`; `PuppyMacro Dev` for Debug builds):
  `settings.json` (settings, loops, remaps), `macros\{id}.json` (one file per macro),
  `backup-before-import-*.puppymacro` (copies made before an Import). The Code view's WebView2
  data (`AppPaths.WebViewFolder`) is a cache, not user data: `%LocalAppData%\PuppyMacro\WebView2`,
  next to Velopack's install, so an uninstall removes it.
- A macro file has its actions in playback order; an action may have a `Name` and a
  `GroupId` pointing to one of the macro's `Groups` (name, collapsed). Groups are not nested
  and a group's actions are consecutive: `MacroDefinition.NormalizeGroups` enforces it when a
  macro is loaded (`MacroLibrary.Sanitize`) and saved by the editor. Playback ignores groups.
  Files without these fields load with no names and no groups.
- The data files' format is `CodeJson.Options` (`MacroLibrary` uses it; settings.json has the same
  options). The Code views show that exact text for a macro, an action, a loop or a remap and read
  it back strictly (`CodeJson.StrictOptions` and the checks in `MacroJson`, `LoopJson`,
  `RemapJson`): keep their checks and limits equal to the editor windows', and `CodeSchema`'s
  descriptions up to date when the models change.
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
- `AppExe` (Specific app) on loops, macros and remaps: an .exe file name or null (all apps). Files
  without it work in all apps; an empty value is saved as null (`AppScope.Normalize`, in
  `SettingsStore`, `MacroLibrary.Sanitize` and the Code views).
- Export / Import: `.puppymacro` zip (`BackupService`); Import keeps a backup and restarts
  the app (`--restart`).
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`). Keep
  the samples working when models change.
