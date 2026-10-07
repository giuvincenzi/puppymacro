---
paths:
  - "PuppyMacro/**"
---

# Architecture

## Stack

- .NET 10 (`net10.0-windows`, `win-x64`, framework-dependent), WPF, **WPF-UI 4.1.0**
  (`FluentWindow`, `TitleBar`, `CardExpander`, `NumberBox`, `ToggleSwitch`, `SymbolIcon`...).
- **Velopack 1.2.161** (installer and updates). No other NuGet packages. Win32 through
  P/Invoke in `Native/NativeMethods.cs`.
- Nullable reference types on, implicit usings off (explicit `using`s).

## Threads

| Thread | What runs there |
|---|---|
| UI (WPF dispatcher) | Windows, view models, `SoundService`, clipboard (`ClipboardPaster`) |
| `InputThread` | `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks + message loop, desktop-switch WinEvent hook |
| `Injector` | Input sent on behalf of the hook (remap targets, menu mask key), in order |
| One per running loop row (`ActionRunner`) / per running macro (`MacroRunner`) | Timing with `PreciseTimer` (high-resolution waitable timer) and `SendInput` |

Rules:
- **Never block the input thread** and never touch WPF objects from it. The hook only
  decides (block or not) and posts work. Windows silently skips, then removes, hooks that
  are too slow.
- `LoopEngine` state is guarded by one lock (`_sync`). The hook reads an immutable
  `EngineSnapshot` (cloned loops, macros, enabled remaps, global hotkeys). **After any change
  to settings, loops, macros or remaps, the UI must publish a new snapshot**
  (`MainWindow.Save()` and `SaveMacro()` already do it).
- UI notifications from the engine go through `Dispatcher.InvokeAsync`.
- Clicks on the game mode panel are matched against row rectangles (`PanelTarget`, physical
  pixels) published by the UI every 300 ms while game mode is on.

## Features map

- Loops: `Models/AppSettings.cs` (`LoopDefinition`, `LoopAction`), `Services/ActionRunner.cs`,
  `LoopEditorWindow`.
- Macros: `Models/MacroModels.cs`, `Services/MacroBuilder.cs` (raw events → actions, path
  simplification), `MacroRunner.cs`, `MacroLibrary.cs` (one JSON per macro),
  `RecordingSession.cs`, `MacroEditorWindow`, `MacroActionWindow`.
- Remap: `RemapDefinition`, `LoopEngine.FindRemap`, `RemapEditorWindow`.
- Game mode: `Views/GameModePanel`, `GameModeWindow` (click-through, no-activate),
  `PlacementWindow`.
- Tray and startup: `Services/TrayIcon.cs` (Shell_NotifyIcon), `StartupService.cs`
  (HKCU Run key, `--tray`). Uninstall removes the Run key (Velopack hook in `App.Main`).
- Updates: `Services/UpdateService.cs`, `MainWindow.Updates.cs` (see `updates.md`).
