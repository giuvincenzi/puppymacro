---
paths:
  - "PuppyMacro/Services/{InputHook,InputSender,InputThread,LoopEngine,ModifierTracker,ActionRunner,MacroRunner,RecordingSession,MacroBuilder,HotkeyConflicts,HotkeyRules,KeyNames,ClipboardPaster}.cs"
  - "PuppyMacro/Native/**"
---

# Input model

- If the target app runs as administrator, PuppyMacro must run as administrator too.
- Keys and mouse buttons are Windows **virtual-key codes** everywhere (mouse: 1 LButton,
  2 RButton, 4 MButton, 5 XButton1, 6 XButton2). Display names: `KeyNames`.
- The scroll wheel as a hotkey key has its own codes outside the virtual-key range (keyboard
  hooks report 1 to 254): `KeyNames.VK_WHEEL_UP` 0x100, down 0x101, left 0x102, right 0x103.
  They are only hotkeys, never keys to send.
- `HotkeyBinding` = main key + Ctrl/Alt/Shift/Win, each with a side (`ModifierSide`: `Left`, `Right`,
  or `Any` = either one, the value of files from before sides existed). Matching is **exact**
  (`HotkeyBinding.Matches`): the same key and modifiers, each held on a side the binding accepts. The
  pressed binding (`LoopEngine.CurrentBinding`) has the side held, `Any` when both are. A capture keeps
  the side pressed; the key field's menu changes it. Conflicts use `Overlaps`: opposite sides do not clash.
- A remap's source (`RemapDefinition.Source`) is a binding too, matched the same way (a remap of J does
  not take Alt+J), or a modifier key alone by its left or right code (Right Alt). Its target can also be a
  modifier key alone; modifiers in a target are sent on their side (`Any`: the left one).
- A source with modifiers (Alt+J): the remap releases the modifiers held (`InputSender.SendBinding` with
  `releaseHeld`, mask key first) before the target, and does not press them again.
- **Modifier state comes only from physical key events** (`ModifierTracker`, updated by the
  hook). Injected events (`LLKHF_INJECTED`) and AltGr's fake left Ctrl (scan code flag
  0x200) never change it. Do not use `GetAsyncKeyState` for this.
- Everything PuppyMacro sends carries `InputSender.Signature` in `dwExtraInfo`; the hook
  ignores those events, so loops/macros/remaps never trigger hotkeys or recording.
- A combination (a loop's or a macro's Press key with Ctrl, Alt, Shift or Win) is sent by
  `InputSender.TapBinding`: under the same lock as `Tap`, held modifiers released first, its modifiers on
  their side (`Any`: the left one), the key, then all released in reverse order, also when stopped. A
  macro's modifiers already down from a Key down are left as they are. Without modifiers the old `Tap`
  runs. Hold down holds a key or button alone, never a combination (an injected modifier held for the
  whole loop would reach every key the user types).
- A paste (a loop's or a macro's Paste text), once Ctrl+V is sent, always completes: stopping does not cut the waits
  before the clipboard is restored (`PreciseTimer.WaitFully`), otherwise the target would read the restored clipboard
  or nothing; a stop skips only Enter after.
- A loop's Click with a fixed point moves there first (skipped when the point is on PuppyMacro, as macro
  clicks); without one it clicks where the pointer is, skipped over PuppyMacro, as before.
- Before a loop sends a key, physically held modifiers are released (so the target gets
  the plain key) and are **not** pressed again (re-pressing caused stuck keys).
- When a blocked key is pressed with Alt or Win held, a mask key (VK 0xE8) is sent so no
  menu / Start opens.
- Left and right click, the scroll wheel and Esc are hotkeys **only with Ctrl, Alt, Shift or
  Win** (`HotkeyRules`): alone they would block the mouse, scrolling or Esc everywhere. A hotkey
  capture (`BeginCapture(..., hotkey: true)`) takes them only while a modifier is held; alone a
  click passes (the UI stays clickable), the wheel scrolls and Esc cancels. Left and right click
  can be remap sources only for a specific app.
- Wheel hotkeys: `InputHook.Wheel` -> `LoopEngine.OnWheel`. One notch (WHEEL_DELTA 120) is one
  press; smaller steps (touchpads) add up per direction. No release, so Toggle only: Hold loops
  and macros are skipped. When a hotkey matches, the wheel event is blocked, also below a notch.
- A key or button held by a running **Hold down** loop row (`ActionRunner.HeldVk`): its physical
  presses and releases are blocked, otherwise the release would let go of the loop's key while
  the loop still shows as running.
- Modifier events (`LoopEngine.OnModifier`): a physical one that is a remap's source is blocked, sends
  the target and never reaches `ModifierTracker`, so it does not count as a modifier (its auto-repeat
  repeats the target). AltGr's fake left Ctrl (`InputHook` passes `altGrCtrl`) is blocked while a remap of
  Right Alt applies, otherwise it passes as before. While a key field waits with `modifierAlone` (remap
  source and target), a modifier pressed and released with no other key or modifier is captured; a lone
  Alt or Win release is then blocked and sent again behind the mask key, so no menu or Start opens.
- An AI request waiting for the user to be ready (`LoopEngine.WatchReadyKeys`, set by `MainWindow` while `AiReadyWindow`
  shows): Enter and Esc pressed with no modifier, in any app, answer it once and are blocked (their release too); every
  other key passes, so the user can open the right screen first.
- Routing order in `LoopEngine.OnKey`: modifiers (`OnModifier`) → AI ready keys → key capture → record hotkey/recording →
  key-up handling (swallowed ups, remap ups, Hold release, keys held by a loop) → auto-repeat →
  global hotkeys, loop and macro hotkeys (`RunHotkey`, skipped while `HotkeysSuspended`) →
  keys held by a loop → remaps. When a remap for the app in front has the key (`FindRemap`, looked up first), `RunHotkey`
  skips the loops and macros for all apps: the one for the app wins, of any kind. For the same apps hotkeys still come
  before remaps (only data from before 1.22 can have both).
- Loops and macros with a Specific app (`AppExe`): `RunHotkey` first looks among the items for the app
  in front (`LoopEngine.ForegroundApp`, loops before macros), then among those for all apps; an item for
  another app is skipped, so its key passes to the app. Global hotkeys (Overlay mode, Stop all, Open PuppyMacro; Record
  earlier in the routing) work in every app.
  `HotkeyConflicts`: **one rule for loops, macros and remaps**: two of them can share keys only when their apps differ
  (one may be all apps: the one for the app in front wins there, whatever its kind); two for all apps or for the same app
  clash, also with one of them disabled. A global hotkey clashes with every loop, macro and remap. `Find` (a loop's, a
  macro's or a global hotkey) checks the global hotkeys, loops, macros and remaps; `FindForRemap` (a remap's source) the
  global hotkeys, loops and macros, and `AlreadyRemapped` the other remaps.
- Mouse details (`InputHook.MouseDetail` -> `LoopEngine.OnMouseDetail`, set only while recording or
  while overlay mode publishes click targets): recording (records, never blocks; clicks on
  PuppyMacro's own windows are not recorded) → a drag of the overlay panel's Move handle (moves pass,
  the left release is blocked) → left or right press on a `PanelTarget` (skipped while
  `HotkeysSuspended`): left, the Move handle starts the drag, Stop all, Exit and the two Open buttons (in the title and
  Open PuppyMacro) act, a loop or macro is
  started or stopped only when `Startable` (a disabled or Hold one does nothing); right, a loop or macro
  is enabled or disabled (`OverlayEnableToggleRequested`), on the panel's own buttons the click passes.
  The press and its release are blocked, so the app in front never gets them, unless the target has
  `PassThrough` (`architecture.md`).
