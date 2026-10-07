---
paths:
  - "PuppyMacro/Services/{InputHook,InputSender,InputThread,LoopEngine,ModifierTracker,ActionRunner,MacroRunner,RecordingSession,MacroBuilder,HotkeyConflicts,KeyNames,ClipboardPaster}.cs"
  - "PuppyMacro/Native/**"
---

# Input model

- Keys and mouse buttons are Windows **virtual-key codes** everywhere (mouse: 1 LButton,
  2 RButton, 4 MButton, 5 XButton1, 6 XButton2). Display names: `KeyNames`.
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
- Left and right click can never be hotkeys (capture lets them pass so the UI stays
  clickable); they can be remap sources only for a specific app. This is a deliberate
  design decision: keep it.
- Routing order in `LoopEngine.OnKey`: modifiers → key capture → record hotkey/recording →
  key-up handling (swallowed ups, remap ups, Hold release) → auto-repeat → global hotkeys,
  loop and macro hotkeys (skipped while `HotkeysSuspended`) → remaps.
