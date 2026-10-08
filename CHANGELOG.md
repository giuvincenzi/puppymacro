# Changelog

## Unreleased

### Added
- **Code view**, next to **Form view**, in the macro, action, loop and remap editors: the item's
  JSON, as it is saved, in a code editor like VS Code's, with Ctrl+Space suggestions and
  descriptions, **Format**, errors underlined as you type and listed in a **Problems** panel
  (click one to go to its line). Save and Form view stay off while there are problems;
  **Discard changes** drops the code's changes and goes back to Form view. Ids cannot be changed
  (Ctrl+Space suggests them again); an action's GroupId can move it to a group next to it. The
  installer adds the Microsoft Edge WebView2 Runtime when Windows does not have it.
- Overlay: **Show the overlay panel** (Settings > Overlay) can be turned off, so overlay mode
  shows only the floating buttons.
- Every floating button has its own **Opacity**, in the loop and macro editors. Existing buttons
  keep the opacity of the panel they had before.
- Hotkeys: with Ctrl, Alt, Shift or Win held, left and right click, the scroll wheel (up, down,
  left, right) and Esc can be hotkeys too. A scroll wheel hotkey counts each notch as one press
  and works with Toggle only.

### Changed
- "Game mode" is now **Overlay mode**, and its panel the **overlay panel**: Settings >
  **Overlay** groups the panel and the floating buttons, and **Position on screen** always shows
  the whole overlay, also from a button's **Position…**.
- A new floating button appears in the middle of the main screen until it is placed.
- **Settings** is at the bottom of the side rail.
- Leaving overlay mode puts the main window back as it was: open, minimized or in the system tray
  (before, it always opened).
- New installs: **Stop all** is Alt+Shift+S and **Overlay mode** is Alt+Shift+W. Existing
  settings keep their hotkeys.

### Fixed
- A loop row set to **Hold down** stopped holding its key or mouse button after you pressed and
  released that same key or button yourself, while the loop still showed as running.

## 1.9.0

### Added
- Macro editor: **Test action** (▶ on every action, and **Test** in the action's window, also
  before saving) plays one action once: PuppyMacro hides, the window behind it gets the focus
  and the action plays there, then PuppyMacro comes back.

### Changed
- The macro editor has two columns: the list of actions takes the whole height on the left,
  Repeat, Speed, Activation, Hotkey, Floating button and Sound are on the right. It opens wider;
  a size kept by an older version is reset once.
- The selection and shortcut tips of the macro editor are in a tooltip, on the blue "i" next to
  the selection bar's text.

## 1.8.0

### Added
- **Floating buttons**: a loop or macro can have its own round button in game mode, placed
  anywhere on the screen, that starts and stops it with a click without the game losing focus.
  Turn it on in the editor with **Floating button** and choose its **Label** and **Size** (S,
  M, L); the hotkey is shown on its edge. A loop or macro with a floating button is not listed
  in the game mode panel. **Position…** in the editor and **Position on screen** in Settings
  place the panel and all floating buttons together.
- Macro editor: select more actions with Ctrl+click or Shift+click, then group, duplicate,
  copy (also to paste into another macro) or delete them together, from the new bar above the
  list or with Ctrl+G, Ctrl+D, Ctrl+C / Ctrl+V and Delete. Dragging and Alt+Up / Alt+Down move
  the whole selection.
- Macro groups: named groups of actions that can be collapsed, renamed, duplicated, ungrouped
  or removed. Actions can be dragged into, out of and between groups. Playback is unchanged.
- Every macro action can have a name (Name (optional) in its edit window), shown in bold in
  the list.

### Changed
- The macro editor can be resized: the list of actions grows with the window, and the size is
  kept for the next time.

## 1.7.2

### Fixed
- Small screens: the main window and the editors open inside the visible part of the screen
  (above the taskbar), shorter when needed, so the title bar and the Save button are always
  reachable. On a 1024x768 screen the main window used to open with its title bar off screen.

## 1.7.1
- Loop editor: a key row shows Repeat / Hold down on the first line and the interval on the
  second, so the options are no longer cut off.
- Starting PuppyMacro while it is already running opens its window (also from the system tray
  or game mode) instead of showing "PuppyMacro is already running".

## 1.7.0
- **Installer and automatic updates**: PuppyMacro is now installed with
  `PuppyMacro-win-Setup.exe` from the GitHub Releases (per user, no administrator rights; it
  installs the .NET 10 Desktop Runtime if missing). The app checks for new versions at startup
  and every 12 hours: a bar at the top and a dot on Settings show an available update, and
  **Update** downloads it and restarts PuppyMacro. Settings > Updates, with
  **Check for updates automatically**.
- Uninstalling (Settings > Apps > Installed apps) also removes the Start with Windows entry.
- Coming from 1.6: install with Setup, then delete the old 1.6 folder. Loops, macros, remaps
  and settings stay in `%AppData%\PuppyMacro` and are kept; Start with Windows moves to the
  installed app at its first start.

## 1.6.0
- Loops: Key rows can be **held down** while the loop runs.
- **System tray**: Close to system tray (on by default), tray menu (Open, Stop all, Exit),
  **Start with Windows**.
- **Remap** section: key/mouse button to key, mouse button or combination; all apps or a
  specific app; left/right click only for a specific app.
- Data moved to `%AppData%\PuppyMacro` (automatic migration); **Backup**: Export / Import /
  Open data folder.
- Input rework: low-level hooks on a dedicated thread; modifier state only from physical
  key events; exact hotkey matching (removed the "loose" matching added in 1.2.1).

## 1.5.0
- Recording: window hidden while recording, **Start recording** with a 3-second countdown,
  New macro opens focused after stopping.
- Hotkeys optional for loops and macros (Hold still needs one); Clear button.
- Press key and Click repeat N times with a pause; Duplicate action (Ctrl+D).
- Click items in the game mode panel on by default.

## 1.4.0
- **Macros** section: recording, action editor, Pick on screen, repeat and speed.
- Clickable game mode panel (optional), Record hotkey, Dark theme by default.
- Position on screen shows the real panel.

## 1.3.0
- Game mode title with name and version, drag and drop reordering, built-in sounds with
  volume, game mode position (X/Y, Position on screen), Settings in expandable groups.

## 1.2.x
- 1.2.1: modifiers no longer re-pressed after sending keys (stuck Alt).
- 1.2.0: wider interval field and value binding fix, hints at the bottom of the game mode
  panel, game mode opacity, hotkeys paused while dialogs are open.

## 1.1.x
- 1.1.1: Folder and SingleFile publish profiles.
- 1.1.0: side rail (Loops / Settings), multiple Key/Text rows per loop with their own
  interval, hotkeys with modifiers, search and filter, version in the title bar.

## 1.0.0
- First native version (C#, WPF, WPF-UI): loops, Toggle/Hold, global hotkeys, game mode,
  System/Light/Dark theme. Replaced the earlier AutoHotkey v2 script.
