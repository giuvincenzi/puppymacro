---
paths:
  - "PuppyMacro/Services/{InputHook,InputSender,InputThread,LoopEngine,ModifierTracker,ActionRunner,MacroRunner,RecordingSession,MacroBuilder,HotkeyConflicts,HotkeyRules,KeyNames,ClipboardPaster}.cs"
  - "PuppyMacro/Native/**"
---

# Input model

- If the target app runs as administrator, PuppyMacro must run as administrator too.
- Keys and mouse buttons are Windows **virtual-key codes** everywhere, mouse buttons too. Display names: `KeyNames`.
- The scroll wheel as a hotkey key has its own codes outside the virtual-key range (keyboard hooks report 1 to 254), in
  `KeyNames`. They are only hotkeys, never keys to send.
- `HotkeyBinding` = main key + Ctrl/Alt/Shift/Win, each with a side (`ModifierSide`: `Left`, `Right`,
  or `Any` = either one, the value of files from before sides existed). Matching is **exact**
  (`HotkeyBinding.Matches`): the same key and modifiers, each held on a side the binding accepts. Conflicts use
  `Overlaps`: opposite sides do not clash.
- A remap's source (`RemapDefinition.Source`) is a binding too, matched the same way (a remap of J does
  not take Alt+J), or a modifier key alone by its left or right code (Right Alt). Its target can also be a
  modifier key alone; modifiers in a target are sent on their side (`Any`: the left one).
- **Pass extra modifiers** (`RemapDefinition.PassExtraModifiers`, `Services/RemapExtras.cs`; the user's decision), for a
  source that is a key or a button: the one exception to exact matching. Only when nothing matches the keys exactly (a
  remap, a hotkey: an exact match always wins), a remap whose source is the same key with part of the modifiers held
  (`HotkeyBinding.MatchesWithExtras`) applies: the one for the app in front first, then the one with the most of them.
  Only the source's own modifiers are released for the app; the extra ones stay down, as the user holds them, so the
  app gets them with the target (Alt+A → Left: Shift+Alt+A gives Shift+Left). Nothing is pressed for the user: a target
  modifier the user already holds as an extra is neither pressed nor released. With Keep modifiers held the hold starts
  the same way, and the source again with extra modifiers sends only the target's key.
- A source with modifiers (Alt+J): the remap releases the modifiers held before the target, and does not press them
  again while the remap lasts.
- **Keep modifiers held** (`RemapDefinition.KeepModifiersHeld`, `Services/RemapHold.cs`; the user's decision), only
  when both source and target have modifiers: the target's modifiers stay held while the user holds the source's, as
  Ctrl+Tab → Alt+Tab needs for the switcher. The source's key again sends only the target's key, whatever app is in
  front; releasing **any one** of the source's modifiers releases **all** the target's (there is no one to one match
  between them); the source's key with a modifier added (Shift+Tab) and mouse buttons pass and keep the hold. Any
  other key ends it: the target's modifiers released, the source's still held pressed again, then the key, so the app
  gets Ctrl+C, not Alt+C. This is the one case where PuppyMacro presses a modifier again for the user: everything of a
  hold goes through the injector in order, and the physical release of a modifier pressed again is blocked and sent
  after that press, so it never comes before it (the cause of the stuck keys). Off: the target's modifiers are released
  with its key, as before. The injector sends everything it has before PuppyMacro closes, releases last.
- **Modifier state comes only from physical key events** (`ModifierTracker`, updated by the
  hook). Injected events (`LLKHF_INJECTED`) and AltGr's fake left Ctrl never change it. Do not use `GetAsyncKeyState`
  for this.
- Everything PuppyMacro sends carries `InputSender.Signature` in `dwExtraInfo`; the hook
  ignores those events, so loops/macros/remaps never trigger hotkeys or recording.
- A combination (a loop's or a macro's Press key with Ctrl, Alt, Shift or Win) is sent as: held modifiers released,
  its modifiers on their side (`Any`: the left one), the key, then all released in reverse order, **also when
  stopped**. A macro's modifiers already down from a Key down are left as they are. Hold down holds a key or button
  alone, never a combination (an injected modifier held for the whole loop would reach every key the user types).
- `InputSender`'s shared lock is held only while sending, never while a key or button is held down (`Tap` sends the
  press and the release each under it and waits between them outside it), so a long press never holds up the other
  loops, macros and remaps. A combination's press is the one exception: its injected modifiers would otherwise reach the
  keys others send meanwhile.
- A paste (a loop's or a macro's Paste text), once Ctrl+V is sent, always completes: stopping does not cut the waits
  before the clipboard is restored (`PreciseTimer.WaitFully`), otherwise the target would read the restored clipboard
  or nothing; a stop skips only Enter after. Pastes take turns (`ClipboardPaster.TakeTurn`, from Enter before to the
  restore), and the user's own clipboard always comes back.
- A loop's Click with a fixed point moves there first (skipped when the point is on PuppyMacro, as macro
  clicks); without one it clicks where the pointer is, skipped over PuppyMacro. The move and the press at a point are
  one step under the lock, so another loop never moves the pointer between them. A macro's Mouse button up whose
  condition found nothing releases where the pointer is (never left held).
- Before a loop sends a key, physically held modifiers are released (so the target gets
  the plain key) and are **not** pressed again (re-pressing caused stuck keys).
- When a blocked key is pressed with Alt or Win held, a mask key is sent so no menu / Start opens.
- Left and right click, the scroll wheel and Esc are hotkeys **only with Ctrl, Alt, Shift or
  Win** (`HotkeyRules`): alone they would block the mouse, scrolling or Esc everywhere. A hotkey
  capture takes them only while a modifier is held; alone a
  click passes (the UI stays clickable), the wheel scrolls and Esc cancels. Left and right click
  can be remap sources only for a specific app.
- Wheel hotkeys: one notch is one press; smaller steps (touchpads) add up per direction. No release, so Toggle only:
  Hold loops and macros are skipped. When a hotkey matches, the wheel event is blocked, also below a notch.
- A key or button held by a running **Hold down** loop row: its physical
  presses and releases are blocked, otherwise the release would let go of the loop's key while
  the loop still shows as running.
- A physical modifier that is a remap's source is blocked, sends the target and never reaches `ModifierTracker`, so it
  does not count as a modifier (its auto-repeat repeats the target). AltGr's fake left Ctrl is blocked while a remap of
  Right Alt applies. While a key field waits for a modifier alone (remap source and target), a modifier pressed and
  released with no other key or modifier is captured; a lone Alt or Win release is then blocked and sent again behind
  the mask key, so no menu or Start opens.
- An AI request waiting for the user to be ready: Enter and Esc pressed with no modifier, in any app, answer it once
  and are blocked (their release too); every other key passes, so the user can open the right screen first.
- Routing order in `LoopEngine.OnKey`: modifiers → AI ready keys → key capture → record hotkey/recording → key-up
  handling → auto-repeat → global hotkeys, loop and macro hotkeys (skipped while `HotkeysSuspended`) → keys held by a
  loop → remaps. A remap for the app in front beats the loops and macros for all apps, of any kind. For the same apps
  hotkeys still come before remaps (only data from before 1.22 can have both). While hotkeys are suspended (an editor
  open) the Stop all hotkey still works when something runs (an editor's Test of an action).
- Loops and macros with a Specific app (`AppExe`): the items for the app in front come first (loops before macros),
  then those for all apps; an item for another app is skipped, so its key passes to the app. Global hotkeys work in
  every app.
  `HotkeyConflicts`: **one rule for loops, macros and remaps**: two of them can share keys only when their apps differ
  (one may be all apps: the one for the app in front wins there, whatever its kind); two for all apps or for the same app
  clash, also with one of them disabled. A global hotkey clashes with every loop, macro and remap.
- Mouse details (set only while recording or while overlay mode publishes click targets): recording never blocks and
  does not record clicks on PuppyMacro's own windows. A press on a `PanelTarget` (skipped while `HotkeysSuspended`):
  left starts or stops a loop or macro only when `Startable`; right enables or disables it, and on the panel's own
  buttons the right click passes. The press and its release are blocked, so the app in front never gets them, unless
  the target has `PassThrough` (`architecture.md`).
