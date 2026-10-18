---
paths:
  - "PuppyMacro/Models/**"
  - "PuppyMacro/Services/{SettingsStore,MacroLibrary,CodeJson,MacroJson,LoopJson,CodeSchema,AppPaths,BackupService,DevSampleData}.cs"
---

# Data and compatibility

- Data folder: `%AppData%\PuppyMacro` (`AppPaths`; `PuppyMacro Dev` for Debug builds). The Code view's WebView2
  data (`AppPaths.WebViewFolder`) is a cache, not user data: `%LocalAppData%\PuppyMacro\WebView2`, next to Velopack's
  install, so an uninstall removes it.
- Macro groups are not nested and a group's actions are consecutive (`MacroDefinition.NormalizeGroups`, on load and
  save). Playback ignores groups. Files without these fields load with no names and no groups.
- The data files' format is `CodeJson.Options` (`MacroLibrary` uses it; settings.json has the same
  options). The Code views show that exact text for a macro, an action, a loop or a remap and read
  it back strictly (`CodeJson.StrictOptions` and the checks in `MacroJson`, `LoopJson`,
  `RemapJson`): keep their checks and limits equal to the editor windows', and `CodeSchema`'s
  descriptions up to date when the models change.
- Versions before 1.6 kept the data next to the exe; `AppPaths.MigrateFromExeFolder` copies it
  to the data folder on first start (Release builds only).
- `SettingsStore.Sanitize` validates and **migrates** old files. When the settings format
  or a default changes for existing users, bump `AppSettings.CurrentSchemaVersion` and add
  the migration there, with a test on an old file. Never break existing user data. New installs get the defaults of
  `AppSettings.CreateDefault`; existing files keep theirs.
- A hotkey's modifier side missing from a file means either side, so older files keep working as before.
- The Open PuppyMacro global hotkey (the only global hotkey that can be cleared) is added to a file from before schema 8
  only if no loop, macro, remap or other global hotkey already uses it (global hotkeys come first). A file of schema 8
  without it keeps none (the user cleared it).
- A loop's Click is a `Key` row whose key is a mouse button, not a new `ActionType`: an older version reading
  `settings.json` must not reset the settings on an unknown enum value. New fields of loop and macro actions are null in
  older files and mean the old behavior; `LoopAction.Normalize` drops values that do not belong to the row.
- `AppExe` (Specific app) on loops, macros and remaps: an .exe file name or null (all apps). An empty value is saved as
  null.
- Export / Import (`BackupService`, "A backup is an identical copy" below): a `.puppymacro` zip of the data folder.
  Import refuses a file without `settings.json` or with a path outside the data folder, keeps the whole folder as
  `backup-before-import-{time}.puppymacro`, replaces the parts the file lists and restarts the app. Files from before
  1.17 have no part list: settings.json and macros, read as Data. Older versions import a new file too: they take only
  `settings.json` and `macros\*.json`.
- `.puppyitems` files (`Services/ItemFile.cs`): loops, macros and remaps to share, never in the data folder (the user
  saves them anywhere). Each item is exactly the Code view's JSON of that item. Import reads each item with the Code
  views' checks and **only adds**: a new Id, disabled, a name already used by that kind gets " (2)", " (3)"…, a hotkey
  already used (also by an item earlier in the file) is left out and a Hold item becomes Toggle (Hold needs a hotkey),
  and a remap's hotkey that clashes is left out too: the remap comes in without it. Raise `ItemFile.CurrentFormat` only
  when the layout around the items changes; a file with a higher one, or an item that fails the checks in a file made by
  a newer version, asks to update PuppyMacro. **A newer PuppyMacro must import every older `.puppyitems` file**: when
  an item field is renamed or removed, convert it in `ItemFile.Read` as `SettingsStore` does for settings.json (the Code
  views reject old fields), with a test on an old file.
- **A remap without a hotkey** exists only after an import, when its hotkey was taken. It is kept and turned off (older
  versions drop it) and can't be turned on until it gets a hotkey: the user opens Edit, chooses a free hotkey and saves.
  Everything else keeps its rules: the editors, the Code views and the AI tools' saves still refuse a remap without a
  hotkey, and a remap without one never clashes with anything.
- Not in the backup: `backup-before-import-*.puppymacro` (copies of earlier data; inside a backup they would
  grow every time), `*.tmp` (files being written), the WebView2 cache in `%LocalAppData%` (not user data), the debug
  archive (`DebugStore.Root` in `%LocalAppData%`: debugging data, kept 7 days). The
  Start with Windows Run entry is outside the data folder: Release builds set it from `StartWithWindows` at every
  start (`MainWindow`), so an import, a reinstall or another PC gets it back. So are the file types (`Services/FileTypes.cs`:
  `.puppyitems` and `.puppymacro` in `HKCU\Software\Classes`, their icons and open commands): Release
  builds write them at every start, from the exe's place, and remove them on uninstall; they hold no user data.
- Debug builds fill an empty dev data folder with disabled samples (`DevSampleData`): keep them working when models
  change.
- Each list page's view (`AppSettings.LoopsLayout`, `MacrosLayout`, `RemapsLayout`): older files get the defaults, Loops
  List, Macros Table, Remap List (the user's choice; no migration). An unknown value (a newer version's view) gives that
  page its default instead of failing the whole file.
- `AppSettings.Language`: a code of `AppLanguages.All` or null, "Use system language" (also in older files, no
  migration: an existing user sees PuppyMacro in the Windows display language from 1.17, as decided); an unknown value
  (a newer version's language, a hand edit) is read as null (`SettingsStore.Sanitize`). Nothing in the data depends on
  the language: numbers and dates in files are invariant (`i18n.md`).
- History versions and AI requests keep their texts as a `Phrase`: see `i18n.md`, "Texts kept in files".
- AI apps: `AppSettings.AllowAiAccess` is off by default and in older files (a new field with the old behavior, no
  migration); its permissions' defaults are in `AppSettings` (the bridge reads them from `settings.json` without
  writing it). Let AI apps see the screen and Let AI apps test macros are on by default and in older files (the user's
  choice); Allow AI access stays the switch for everything. `ai-activity.json` in the data folder holds the newest AI
  requests up to `AiActivityLog.MaxBytes` (no age limit), with the JSON before a change or deletion for Restore; not user data: a missing or unreadable file is an
  empty history.
- **Each kind of data has its own keeping rule, written here; a new kind of data gets its rule decided with the user
  and written here in the same pull request.** Where a limit is a size, the oldest entries go first and the newest one
  always stays.

  | What | Where | Kept |
  |---|---|---|
  | Settings, loops, remaps, macros | data folder | until the user deletes them |
  | Images of the conditions | data folder, `images\` | while a saved macro uses them or something holds them |
  | History | data folder, `history\` | the newest versions, up to `ItemHistory.MaxBytes` per item |
  | AI activity | data folder, `ai-activity.json` | the newest requests, up to `AiActivityLog.MaxBytes` |
  | Screen capture's errors | data folder, `logs\screen-capture.log` | up to `CaptureLog.MaxBytes` |
  | Copy before an Import | data folder, `backup-before-import-*.puppymacro` | until the user deletes it |
  | Code view's cache | `%LocalAppData%`, `WebView2` | as WebView2 keeps it |
  | Debug archive (AI requests, Debug runs) | `%LocalAppData%`, `debug\` | 7 days per operation (`DebugStore.Life`); Settings › Debug data deletes it |

  `logs\` in the data folder is for debugging logs only. The debug archive holds the AI apps' requests whole (arguments,
  answers, the app's `_meta`) and the screen's pictures: on this PC only, never sent anywhere (the guide's privacy says so).
- The images of the macros' conditions: `images\{sha256}.png` in the data folder (`ImageStore`), part Data. A macro
  names them by that Id. An image no saved macro uses and nothing holds (an open editor, the editor's copied actions, an
  AI app's take: `ImageStore.Hold`) is deleted at the next save of a macro or at start; the History keeps a copy of every
  image its versions use, so Restore brings it back. `.puppyitems` files carry the images of their macros.
- History: `history\{id}.json` in the data folder, one per loop, macro or remap (`ItemHistory`, size limit
  `ItemHistory.MaxBytes` per item). Not user data: a missing or unreadable file is an empty history.
- PuppyMacro's entry in the AI apps' own settings is named `puppymacro` (`puppymacro-dev` for the development build, so
  it never replaces the installed one's) and starts `AppPaths.ExecutablePath` (Velopack's `current` folder) with `--mcp`.

## A backup is an identical copy

A user who exports with **Everything** and imports on another PC must get the same PuppyMacro: every
loop, macro, remap, setting, History, AI activity and log. So, for every change that adds or moves data:

- All user data and state lives in the data folder (`AppPaths.DataFolder`). `BackupService` exports
  the whole folder, so a new file there is in the backup with no code change: put it in the right
  part (`BackupService.PartOf`: Data, unless it is History, AI activity or logs; a new kind of
  data that users may want to leave out gets a new part and a checkbox in `Views/ExportOptionsView`).
- Anything kept outside the data folder (registry, `%LocalAppData%`, other apps' files) must be
  rebuilt from the data on start or after an import (as the Start with Windows Run entry is), or be
  listed above under "Not in the backup" with the reason (only caches and copies).
- Add a case to `BackupServiceTests` for every new file or folder.
