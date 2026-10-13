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
- Schema 7: a remap's source is `Source` (a `HotkeyBinding`); the single key of older files, `SourceVk`, is
  read once and converted to a `Source` with no modifiers (`SettingsStore.Sanitize`); the remap Code view
  rejects `SourceVk`. Hotkeys have `CtrlSide`, `AltSide`, `ShiftSide`, `WinSide`, left out when `Any`
  (either side), so older files mean either side, as before. The macro editor opens 240 px wider since
  schema 7 (120 for its right column, 120 for the list): a `MacroEditorWidth` from an older file grows by 240 once.
- Loop actions (`LoopAction`) and macro actions (`MacroAction`) share `KeyModifierFields`: `Ctrl`, `Alt`,
  `Shift`, `Win` and their sides, flat next to the key, written only when set (a macro's Press key, a loop's
  Press key). A loop's Click is a `Key` row whose `KeyVk` is a mouse button (`LoopAction.IsClick`), not a
  new `ActionType`: an older version reading the file must not reset the settings on an unknown enum value.
  A loop row's `HoldMs`, `Repeat`, `RepeatPauseMs`, `ClickCount`, `X`, `Y` are null in older files and
  mean what loops did before (30 ms held, at most half the interval; once; single; where the pointer is);
  `LoopAction.Normalize` (in `SettingsStore`) drops values that do not belong to the row. Older versions
  ignore these fields: a combination plays as its key alone there.
- `AppExe` (Specific app) on loops, macros and remaps: an .exe file name or null (all apps). Files
  without it work in all apps; an empty value is saved as null (`AppScope.Normalize`, in
  `SettingsStore`, `MacroLibrary.Sanitize` and the Code views).
- Export / Import (`BackupService`, `CLAUDE.md`: a backup is an identical copy): a `.puppymacro` zip of the data
  folder, paths relative to it, with `puppymacro-backup.json` listing its parts (`BackupParts`: Data, History,
  AiActivity, Logs; `PartOf` decides by path: `history\` History, `ai-activity.json` AI activity, `logs\` Logs,
  everything else Data, so a new file is Data). Export takes Data always and the parts chosen in
  `Views/ExportOptionsView` (Everything by default). Import refuses a file without `settings.json` or with a path
  outside the data folder, keeps the whole folder as `backup-before-import-{time}.puppymacro`, then for each part the
  file lists deletes that part's files and extracts the file's (a listed part with no files ends empty); the other
  parts stay. It restarts the app (`--restart`). Files from before 1.17 have no list: settings.json and macros, read
  as Data. Older versions import a new file too: they take only `settings.json` and `macros\*.json`.
- `.puppyitems` files (`Services/ItemFile.cs`): loops, macros and remaps to share, never in the data folder (the user
  saves them anywhere). JSON: `{"Format": 1, "App": "1.17.0", "Items": [{"Kind": "Loop" | "Macro" | "Remap", "Item": {...}}]}`,
  each `Item` exactly the Code view's JSON of that item (`CodeJson.Options`, with its Id). Import reads each item with the
  Code views' checks (`LoopJson`, `MacroJson`, `RemapJson`) and **only adds**: a new Id, `Enabled` false, a name already
  used by that kind gets " (2)", " (3)"…, a hotkey already used (`HotkeyConflicts.Find`, also by an item chosen earlier in
  the file) is left out and a Hold item becomes Toggle (Hold needs a hotkey), and so is a remap's hotkey (`Source`) when it
  clashes (`HotkeyConflicts.FindForRemap` and `AlreadyRemapped`, the remap editor's rule, also with the file's earlier
  items): the remap comes in without it. A file's remap without a `Source` (exported that way) comes in without one too
  (`RemapJson.Parse` with `withoutSource`, for Import only). Raise `ItemFile.CurrentFormat` only
  when the layout around the items changes; a file with a higher one, or an item that fails the checks in a file whose
  `App` is newer than this version, asks to update PuppyMacro. **A newer PuppyMacro must import every older `.puppyitems`
  file**: when an item field is renamed or removed, convert it in `ItemFile.Read` as `SettingsStore` does for settings.json
  (the Code views reject old fields), with a test on an old file.
- **A remap without a hotkey** (`RemapDefinition.Source` null) exists only after an import, when its hotkey was taken.
  `SettingsStore.Sanitize` keeps it and turns it off (it used to drop remaps without a source; a remap without a `Target` is
  still dropped); older versions drop it. It can't be turned on until it gets a hotkey: its card's switch is disabled (`ui.md`),
  `set_enabled` refuses it, and the engine skips it. The user opens Edit, chooses a free hotkey (the editor's usual rules,
  which refuse a remap without one) and saves; then the switch works. Everything else keeps its rules: the editors, the Code
  views and the AI tools' saves still refuse a remap without a hotkey (`RemapJson` without `withoutSource`), and a remap
  without one never clashes with anything (`HotkeyBinding.Overlaps` of null is false).
- Not in the backup: `backup-before-import-*.puppymacro` (copies of earlier data; inside a backup they would
  grow every time), `*.tmp` (files being written), the WebView2 cache in `%LocalAppData%` (not user data). The
  Start with Windows Run entry is outside the data folder: Release builds set it from `StartWithWindows` at every
  start (`MainWindow`), so an import, a reinstall or another PC gets it back. So are the file types (`Services/FileTypes.cs`:
  `.puppyitems` and `.puppymacro` in `HKCU\Software\Classes`, their icons and open commands): Release
  builds write them at every start, from the exe's place, and remove them on uninstall; they hold no user data.
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`; with `PUPPYMACRO_SCREENSHOTS=1` the first
  loop is enabled). Keep
  the samples working when models change.
- `AppSettings.LoopsLayout`, `MacrosLayout`, `RemapsLayout` (`ItemsLayout`: List, Table, Grid): each list page's view.
  Older files get the defaults, Loops List, Macros Table, Remap List (the user's choice; a new field, no migration). A name
  this version does not know (a newer version's view) or a number is read by `ItemsLayoutConverter` as a value outside the
  enum instead of failing the whole file, and `SettingsStore.Sanitize` gives that page its default.
- `AppSettings.Language`: a code of `AppLanguages.All` or null, "Use system language" (also in older files, no
  migration: an existing user sees PuppyMacro in the Windows display language from 1.17, as decided); an unknown value
  (a newer version's language, a hand edit) is read as null (`SettingsStore.Sanitize`). Nothing in the data depends on
  the language: numbers and dates in files are invariant (`i18n.md`).
- History versions and AI requests keep their texts as a `Phrase` (`SummaryPhrase`, `ChangePhrases`, `RequestPhrase`)
  next to the English text older versions read (`Summary`, `Changes`, `Request`); files from before 1.17 have only the
  English text (`i18n.md`).
- AI apps: `AppSettings.AllowAiAccess` is off by default and in older files (a new field with the old behavior, no
  migration); so are its permissions' defaults in older files: `AiAskBeforeDeleting` on, `AiAskBeforeChanging` off,
  `AiAskBeforeSettings` on, `AiStartWhenNeeded` on (the bridge reads them from `settings.json` without writing it).
  `ai-activity.json` in the data folder holds the newest AI requests up to 20 MB (`AiActivityLog.MaxBytes`; no age limit), with
  the JSON before a change or deletion for Restore; not user data: a missing or unreadable file is an empty history.
- Limits are sizes, never days or counts (the user's choice): a change of a big macro is 100 KB, a remap's a few hundred bytes.
  The oldest entries go first and the newest one always stays.
- `logs\ai-calls.jsonl` in the data folder (`AiCallLog`): one JSON line per tool call of an AI app, for debugging: start, ms,
  app, tool, characters in and out, ok / error / failed, patch operations. Never the arguments or answers. The newest 5 MB.
  Not user data: a missing or unreadable file or line is skipped. `logs\` is for debugging logs only.
- History: `history\{id}.json` in the data folder, one per loop, macro or remap, the newest versions up to 10 MB per item (`ItemHistory.MaxBytes`): time,
  who, summary, changes, the item's JSON, `Deleted`. Not user data: a missing or unreadable file is an empty history.
  PuppyMacro's entry in the AI apps' own settings is named `puppymacro` (`puppymacro-dev` for the development build, so
  it never replaces the installed one's) and starts `AppPaths.ExecutablePath` (Velopack's `current` folder) with `--mcp`.
