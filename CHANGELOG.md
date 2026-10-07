# Changelog

## Unreleased

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
