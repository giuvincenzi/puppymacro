# Changelog

## Unreleased

### Added
- Remaps from a **combination**: **When I press** takes Ctrl, Alt, Shift or Win with a key, for example
  Alt+J → Left arrow.
- Remaps of **Ctrl, Alt, Shift or Win** alone, left and right apart (for example Right Alt → Enter), and
  Ctrl, Alt, Shift or Win alone in **Send instead**.
- **Left and right modifiers**: hotkeys, remaps and Settings' hotkeys keep the side of Ctrl, Alt, Shift
  and Win you pressed, so Left Ctrl+F6 and Right Ctrl+F6 can do different things. The arrow next to the
  keys chooses Left, Right or Left or right for each one.

### Changed
- A remap of a key alone no longer applies when Ctrl, Alt, Shift or Win is held: a remap of J leaves
  Alt+J as it is.
- Every key field has the arrow next to the keys, also in Settings and in the remap editor.
- Modifier keys are named Left Ctrl, Right Alt and so on, instead of LCtrl, RAlt.
- The overlay panel's buttons are in the order move, exit, open.

## 1.13.0

### Added
- In overlay mode, **right-click** a row of the overlay panel or a floating button to enable or disable
  that loop or macro. Disabling a running one stops it.
- **Show in overlay** in the loop and macro editors (on by default): turn it off to keep a loop or macro
  out of the overlay. Inside it, **Hide when disabled** leaves it out while it is disabled, and **Clicks
  also reach the app behind** lets the app under the overlay get the clicks too.
- The overlay panel has an **open** button next to the exit button: it leaves overlay mode and opens the
  main window.

### Changed
- The overlay shows disabled loops and macros too: a panel row with a dashed outline and the state
  **Disabled**, a floating button with a dashed ring. A click on them does nothing.
- The overlay panel shows what the clicks do under Stop all.
- Leaving overlay mode with its hotkey or the panel's exit button keeps the main window in the system
  tray, instead of putting it back as it was before. The panel's buttons are now exit, open and move, in
  this order.

## 1.12.0

### Added
- **Specific app** for loops and macros, as for remaps: a loop or macro can work in one app only. Its
  hotkey works only while that app is in front (in other apps the key reaches the app), it stops when
  another app comes in front, and in overlay mode the panel and the floating buttons show only what
  works in the app in front. Loops and macros for different apps can share a hotkey; the one for the
  app in front wins over one for all apps.
- **Pick** in Specific app: click a window of the app to choose it; the window under the mouse is
  outlined with the app's name.

### Changed
- The remap editor's **Works in** card is now **Specific app**, the same card as in the loop and macro
  editors.

### Fixed
- The buttons of the Updates card in Settings and of the update bar (Check now, Try again, Cancel)
  have the Windows 11 look again instead of the old Windows one.

## 1.11.0

### Added
- 50 new start and stop sounds for loops and macros (56 in all): instruments, percussion, game,
  water and air, and signal sounds. In the editors the sound list scrolls on its own and opens on
  the chosen sound.

## 1.10.0

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
- Overlay panel: **Stop all** at the bottom (with its hotkey, red while something runs), and at
  the top right an exit button and a move button: press and drag it to move the panel, and the
  new position is saved. Like the rows, they work with **Click items in the panel to start or
  stop them** on, and the app behind keeps the focus.
- Macro editor: right-click a row for the commands, double-click an action to edit it or a group
  to open or close it, **Shortcuts** (in ⋯) lists the shortcuts, and the list can show
  **Compact** rows.
- Settings > **About**: the version, with links to the **User guide** and **GitHub**.
- Recording: the 3, 2, 1 countdown also shows big in the middle of the screen.

### Changed
- New look with the standard Windows 11 controls: cards and groups as in Windows Settings, On /
  Off switches, confirmations in dialogs. Settings is in sections (General, Hotkeys and overlay,
  Data and updates, About). The update bar's **Later** is now its close button.
- Main window: a side rail as in Microsoft Store, with Loops, Macros and Remap at the top and
  **Settings** at the bottom (with a dot when an update is available). The window opens bigger
  and keeps its size. **Stop all** at the bottom is red while something runs and off otherwise.
- While a loop, macro or remap editor or **Position on screen** is open, the main window hides
  and comes back when it closes; the editors have their own taskbar button.
- Loops, Macros and Remap: each card shows its state (Running, Idle, Disabled; Enabled or
  Disabled for remaps) and how it starts ("Toggle with F6"); a remap's title is its keys. On the
  right: ▶ / ■ (not on remaps and Hold items), **Edit** (off while loops or macros run), the
  switch and **More options**, which no longer has Edit. **New macro** is now the main button.
- One field for every key and hotkey: **Set hotkey** / **Set key**, then the keys in the accent
  color: click them to change them; where the key can be removed, their arrow has **Change** and
  **Clear**. It replaces the Change, Clear and Choose key buttons.
- Loop and remap editors: two columns. Each key or text of a loop is a group with a summary, its
  key and Remove in the header, its Mode (Repeat / Hold down) and Every inside; **Add key** waits
  for the key right away.
- Macro editor: one command bar (**Add action**, **Record**, **Test**, **Edit**, **Copy**,
  **Paste**, **Duplicate**, **Group**, **Delete**; **Rename group** and **Ungroup** in ⋯) that
  works on the selected rows, instead of the buttons on every row and group. Each action is one
  row with its delay on the right; the delay is changed in the action's window (for a recorded
  Move path, Edit asks only for its delay).
- "Game mode" is now **Overlay mode**, and its panel the **overlay panel**: Settings >
  **Overlay** groups the panel and the floating buttons, and **Position on screen** always shows
  the whole overlay, also from a button's **Position…**.
- A new floating button appears in the middle of the main screen until it is placed.
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
