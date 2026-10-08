---
paths:
  - "PuppyMacro/Services/{InputHook,InputSender,InputThread,LoopEngine,ModifierTracker,ActionRunner,MacroRunner,RecordingSession,MacroBuilder,HotkeyConflicts,HotkeyRules,KeyNames,ClipboardPaster}.cs"
  - "PuppyMacro/Native/**"
---

# Input model

- Keys and mouse buttons are Windows **virtual-key codes** everywhere (mouse: 1 LButton,
  2 RButton, 4 MButton, 5 XButton1, 6 XButton2). Display names: `KeyNames`.
- The scroll wheel as a hotkey key has its own codes outside the virtual-key range (keyboard
  hooks report 1 to 254): `KeyNames.VK_WHEEL_UP` 0x100, down 0x101, left 0x102, right 0x103.
  They are only hotkeys, never keys to send.
- `HotkeyBinding` = main key + Ctrl/Alt/Shift/Win. Matching is **exact**.
- **Modifier state comes only from physical key events** (`ModifierTracker`, updated by the
  hook). Injected events (`LLKHF_INJECTED`) and AltGr's fake left Ctrl (scan code flag
  0x200) never change it. Do not use `GetAsyncKeyState` for this.
- Everything PuppyMacro sends carries `InputSender.Signature` in `dwExtraInfo`; the hook
  ignores those events, so loops/macros/remaps never trigger hotkeys or recording.
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
- Routing order in `LoopEngine.OnKey`: modifiers → key capture → record hotkey/recording →
  key-up handling (swallowed ups, remap ups, Hold release, keys held by a loop) → auto-repeat →
  global hotkeys, loop and macro hotkeys (`RunHotkey`, skipped while `HotkeysSuspended`) →
  keys held by a loop → remaps.
