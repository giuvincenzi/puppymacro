using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>Read-only copy of everything the input hook needs. Replaced (never changed) by the UI.</summary>
internal sealed class EngineSnapshot
{
    public HotkeyBinding? StopAll { get; init; }
    public HotkeyBinding? OverlayMode { get; init; }
    public HotkeyBinding? Record { get; init; }
    public LoopDefinition[] Loops { get; init; } = Array.Empty<LoopDefinition>();
    public MacroDefinition[] Macros { get; init; } = Array.Empty<MacroDefinition>();
    public RemapDefinition[] Remaps { get; init; } = Array.Empty<RemapDefinition>();

    public static EngineSnapshot From(AppSettings settings, MacroLibrary macros) => new()
    {
        StopAll = settings.StopAllHotkey?.Clone(),
        OverlayMode = settings.OverlayModeHotkey?.Clone(),
        Record = settings.RecordHotkey?.Clone(),
        Loops = settings.Loops.Select(l => l.Clone()).ToArray(),
        Macros = macros.Macros.Select(m => m.Clone()).ToArray(),
        Remaps = settings.Remaps.Where(r => r.Enabled).Select(r => r.Clone()).ToArray(),
    };
}

/// <summary>A clickable row of the overlay panel, in physical screen pixels.</summary>
internal readonly record struct PanelTarget(int Left, int Top, int Right, int Bottom, Guid Id);

/// <summary>
/// Routes global input to loops, macros, remaps and global hotkeys, records macros, and owns
/// the running threads. The hooks run on their own <see cref="InputThread"/>; the UI calls in
/// from its thread. All shared state is guarded by one lock; the hook only reads the
/// immutable <see cref="EngineSnapshot"/> published by the UI.
/// </summary>
internal sealed class LoopEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher;
    private readonly ClipboardPaster _paster;
    private readonly Injector _injector = new();
    private InputThread? _input;
    private volatile EngineSnapshot _snapshot;

    private readonly Dictionary<Guid, List<ActionRunner>> _running = new();
    private readonly Dictionary<Guid, MacroRunner> _runningMacros = new();

    // Main keys of hotkeys currently held down that we own (key auto-repeat, Hold release).
    private readonly HashSet<int> _heldHotkeys = new();

    // Key-up events to swallow because we blocked the matching key-down.
    private readonly HashSet<int> _swallowUp = new();

    // Remaps currently held: source key -> target sent.
    private readonly Dictionary<int, HotkeyBinding> _activeRemaps = new();

    private Action<HotkeyBinding>? _captureDone;
    private Action? _captureCancelled;
    private bool _captureAllowsPrimaryMouse;

    // ---- Recording ----
    private Action? _recordArmedStart;
    private Func<bool>? _recordMovesProvider;
    private bool _recording;
    private bool _recordMoves;
    private long _recordStart;
    private List<RawEvent> _recorded = new();
    private Action<List<RawEvent>>? _recordStopped;

    // ---- Clickable overlay panel ----
    private IReadOnlyList<PanelTarget>? _panelTargets;
    private bool _swallowLeftUp;

    // ---- Foreground app names (for app-specific remaps) ----
    private readonly Dictionary<uint, string> _processNames = new();

    private volatile bool _hotkeysSuspended;

    public LoopEngine(EngineSnapshot snapshot, Dispatcher dispatcher)
    {
        _snapshot = snapshot;
        _dispatcher = dispatcher;
        _paster = new ClipboardPaster(dispatcher);
    }

    /// <summary>Raised (asynchronously, on the UI thread) when any loop or macro starts or stops.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (asynchronously, on the UI thread) when the overlay mode hotkey is pressed.</summary>
    public event Action? OverlayModeToggleRequested;

    /// <summary>Raised (asynchronously, on the UI thread) to play a sound: name, start (true) or stop (false).</summary>
    public event Action<string, bool>? SoundRequested;

    /// <summary>Raised (on the UI thread) while recording, with the number of raw events so far.</summary>
    public event Action<int>? RecordingProgress;

    /// <summary>While true (a dialog is open), hotkeys pass through. Capture, recording and remaps still work.</summary>
    public bool HotkeysSuspended
    {
        get => _hotkeysSuspended;
        set => _hotkeysSuspended = value;
    }

    public bool AnyRunning
    {
        get { lock (_sync) return _running.Count > 0 || _runningMacros.Count > 0; }
    }

    public bool IsRecording
    {
        get { lock (_sync) return _recording; }
    }

    public bool IsRunning(Guid id)
    {
        lock (_sync)
            return _running.ContainsKey(id) || _runningMacros.ContainsKey(id);
    }

    /// <summary>Installs the hooks on the input thread.</summary>
    public void Start()
    {
        _input = new InputThread(hook =>
        {
            hook.Handler = OnKey;
        }, OnDesktopSwitch);
        _input.Start();
    }

    /// <summary>Called by the UI after any change to hotkeys, loops, macros or remaps.</summary>
    public void Publish(EngineSnapshot snapshot) => _snapshot = snapshot;

    // ================= Loops =================

    public void StartLoop(LoopDefinition loop)
    {
        lock (_sync)
        {
            if (_running.ContainsKey(loop.Id) || loop.Actions.Count == 0)
                return;
            var runners = loop.Actions.Select(action => new ActionRunner(action, _paster)).ToList();
            _running[loop.Id] = runners;
            foreach (var runner in runners)
            {
                runner.Exited += OnRunnerExited;
                runner.Start();
            }
            RequestSound(loop.SoundEnabled, loop.SoundName, start: true);
            RaiseStateChanged();
        }
    }

    public void StopLoop(Guid loopId) => StopLoop(loopId, playSound: true);

    private void StopLoop(Guid loopId, bool playSound)
    {
        lock (_sync)
        {
            if (!_running.Remove(loopId, out var runners))
                return;
            foreach (var runner in runners)
                runner.Stop();
            if (playSound)
            {
                var loop = _snapshot.Loops.FirstOrDefault(l => l.Id == loopId);
                if (loop != null)
                    RequestSound(loop.SoundEnabled, loop.SoundName, start: false);
            }
            RaiseStateChanged();
        }
    }

    public void ToggleLoop(LoopDefinition loop)
    {
        lock (_sync)
        {
            if (_running.ContainsKey(loop.Id))
                StopLoop(loop.Id);
            else
                StartLoop(loop);
        }
    }

    // ================= Macros =================

    public void StartMacro(MacroDefinition macro)
    {
        lock (_sync)
        {
            if (_runningMacros.ContainsKey(macro.Id) || macro.Actions.Count == 0)
                return;
            var runner = new MacroRunner(macro, _paster);
            runner.Exited += OnMacroExited;
            _runningMacros[macro.Id] = runner;
            runner.Start();
            RequestSound(macro.SoundEnabled, macro.SoundName, start: true);
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Plays <paramref name="action"/> once for the editor's Test (<see cref="MacroDefinition.ForTest"/>).
    /// It is not a running macro: no sound, no state change. <paramref name="finished"/> runs on
    /// the UI thread when it ends.
    /// </summary>
    public void TestMacroAction(MacroAction action, Action finished)
    {
        var runner = new MacroRunner(MacroDefinition.ForTest(action), _paster);
        runner.Exited += _ => _dispatcher.InvokeAsync(finished);
        runner.Start();
    }

    public void StopMacro(Guid macroId) => StopMacro(macroId, playSound: true);

    private void StopMacro(Guid macroId, bool playSound)
    {
        lock (_sync)
        {
            if (!_runningMacros.Remove(macroId, out var runner))
                return;
            runner.Stop();
            if (playSound)
            {
                var macro = _snapshot.Macros.FirstOrDefault(m => m.Id == macroId);
                if (macro != null)
                    RequestSound(macro.SoundEnabled, macro.SoundName, start: false);
            }
            RaiseStateChanged();
        }
    }

    public void ToggleMacro(MacroDefinition macro)
    {
        lock (_sync)
        {
            if (_runningMacros.ContainsKey(macro.Id))
                StopMacro(macro.Id);
            else
                StartMacro(macro);
        }
    }

    private void ToggleById(Guid id)
    {
        var snap = _snapshot;
        var loop = snap.Loops.FirstOrDefault(l => l.Id == id);
        if (loop != null)
        {
            ToggleLoop(loop);
            return;
        }
        var macro = snap.Macros.FirstOrDefault(m => m.Id == id);
        if (macro != null)
            ToggleMacro(macro);
    }

    public void StopAll()
    {
        lock (_sync)
        {
            if (_running.Count == 0 && _runningMacros.Count == 0)
                return;

            // One stop sound only: the first running item, in list order, that has a sound.
            var snap = _snapshot;
            var loopWithSound = snap.Loops.FirstOrDefault(l => l.SoundEnabled && _running.ContainsKey(l.Id));
            var macroWithSound = snap.Macros.FirstOrDefault(m => m.SoundEnabled && _runningMacros.ContainsKey(m.Id));
            if (loopWithSound != null)
                RequestSound(true, loopWithSound.SoundName, start: false);
            else if (macroWithSound != null)
                RequestSound(true, macroWithSound.SoundName, start: false);

            foreach (var runner in _running.Values.SelectMany(r => r))
                runner.Stop();
            _running.Clear();
            foreach (var runner in _runningMacros.Values)
                runner.Stop();
            _runningMacros.Clear();
            RaiseStateChanged();
        }
    }

    private void RequestSound(bool enabled, string name, bool start)
    {
        if (enabled)
            _dispatcher.InvokeAsync(() => SoundRequested?.Invoke(name, start));
    }

    // ================= Recording =================

    /// <summary>Waits for the record hotkey; then <paramref name="started"/> is called and recording begins.</summary>
    public void ArmRecording(Func<bool> recordMouseMovement, Action started, Action<List<RawEvent>> stopped)
    {
        CancelCapture();
        lock (_sync)
        {
            _recordMovesProvider = recordMouseMovement;
            _recordArmedStart = started;
            _recordStopped = stopped;
        }
    }

    /// <summary>Starts an armed recording right away (the "Start recording" button, after the countdown).</summary>
    public void StartRecordingNow()
    {
        lock (_sync)
        {
            if (_recordArmedStart != null && !_recording)
                BeginRecording(_recordMovesProvider?.Invoke() ?? true);
        }
    }

    /// <summary>Cancels an armed (not yet started) recording.</summary>
    public void DisarmRecording()
    {
        lock (_sync)
        {
            _recordArmedStart = null;
            if (!_recording)
                _recordStopped = null;
        }
    }

    // Called with the lock held.
    private void BeginRecording(bool recordMoves)
    {
        var started = _recordArmedStart;
        _recordArmedStart = null;
        _recordMoves = recordMoves;
        _recording = true;
        _recorded = new List<RawEvent>();
        _recordStart = Stopwatch.GetTimestamp();
        UpdateMouseDetail();
        _dispatcher.InvokeAsync(() => started?.Invoke());
    }

    /// <summary>Stops recording (record hotkey or the Stop button) and hands over the events.</summary>
    public void StopRecording()
    {
        lock (_sync)
        {
            if (!_recording)
                return;
            _recording = false;
            UpdateMouseDetail();
            var events = _recorded;
            var stopped = _recordStopped;
            _recordStopped = null;
            _recorded = new List<RawEvent>();
            _dispatcher.InvokeAsync(() => stopped?.Invoke(events));
        }
    }

    // Called with the lock held.
    private void Record(RawEventKind kind, int vk, int x = 0, int y = 0, int delta = 0)
    {
        double t = (Stopwatch.GetTimestamp() - _recordStart) * 1000.0 / Stopwatch.Frequency;
        _recorded.Add(new RawEvent(kind, vk, x, y, delta, t));
        if (_recorded.Count % 25 == 0 || kind != RawEventKind.Move)
        {
            int count = _recorded.Count;
            _dispatcher.InvokeAsync(() => RecordingProgress?.Invoke(count));
        }
    }

    // ================= Clickable panel =================

    /// <summary>Clickable rows of the visible overlay panel, or null when clicks are off.</summary>
    public void SetPanelTargets(IReadOnlyList<PanelTarget>? targets)
    {
        lock (_sync)
        {
            _panelTargets = targets;
            UpdateMouseDetail();
        }
    }

    // Called with the lock held.
    private void UpdateMouseDetail()
    {
        var hook = _input?.Hook;
        if (hook != null)
            hook.MouseDetail = _recording || _panelTargets != null ? OnMouseDetail : null;
    }

    // Runs on the input thread. Returns true to block the event.
    private bool OnMouseDetail(InputHook.MouseKind kind, int vk, int x, int y, int wheelDelta)
    {
        lock (_sync)
        {
            if (_recording)
            {
                // Clicks on PuppyMacro's own windows (e.g. the Stop button) are not recorded.
                bool own = kind != InputHook.MouseKind.Move && CursorGuard.IsOwnWindowAt(x, y);
                if (!own)
                {
                    switch (kind)
                    {
                        case InputHook.MouseKind.Move:
                            if (_recordMoves)
                                Record(RawEventKind.Move, 0, x, y);
                            break;
                        case InputHook.MouseKind.ButtonDown: Record(RawEventKind.ButtonDown, vk, x, y); break;
                        case InputHook.MouseKind.ButtonUp: Record(RawEventKind.ButtonUp, vk, x, y); break;
                        case InputHook.MouseKind.Wheel: Record(RawEventKind.Wheel, 0, x, y, wheelDelta); break;
                        case InputHook.MouseKind.HWheel: Record(RawEventKind.HWheel, 0, x, y, wheelDelta); break;
                    }
                }
                return false;
            }

            if (_panelTargets != null && vk == KeyNames.VK_LBUTTON)
            {
                if (kind == InputHook.MouseKind.ButtonUp && _swallowLeftUp)
                {
                    _swallowLeftUp = false;
                    return true;
                }
                if (kind == InputHook.MouseKind.ButtonDown && !_hotkeysSuspended)
                {
                    foreach (var target in _panelTargets)
                    {
                        if (x >= target.Left && x < target.Right && y >= target.Top && y < target.Bottom)
                        {
                            _swallowLeftUp = true;
                            ToggleById(target.Id);
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    // ================= Key capture =================

    /// <summary>
    /// Captures the next key or mouse button, with the Ctrl/Alt/Shift/Win physically held at that
    /// moment. Esc cancels. When <paramref name="allowPrimaryMouse"/> is false, left and right
    /// click pass through so the user can still click the UI.
    /// </summary>
    public void BeginCapture(Action<HotkeyBinding> done, Action cancelled, bool allowPrimaryMouse)
    {
        CancelCapture();
        lock (_sync)
        {
            _captureDone = done;
            _captureCancelled = cancelled;
            _captureAllowsPrimaryMouse = allowPrimaryMouse;
        }
    }

    public void CancelCapture()
    {
        Action? cancelled;
        lock (_sync)
        {
            cancelled = _captureDone != null ? _captureCancelled : null;
            _captureDone = null;
            _captureCancelled = null;
        }
        cancelled?.Invoke();
    }

    private static HotkeyBinding CurrentBinding(int vk) => new()
    {
        Vk = vk,
        Ctrl = ModifierTracker.Ctrl,
        Alt = ModifierTracker.Alt,
        Shift = ModifierTracker.Shift,
        Win = ModifierTracker.Win,
    };

    private static bool Matches(HotkeyBinding? hotkey, HotkeyBinding pressed) =>
        hotkey != null && hotkey.IsSet && hotkey.SameAs(pressed);

    // ================= Key routing (input thread) =================

    // Returns true to block the event.
    private bool OnKey(int vk, bool isDown, bool physicalModifierEvent)
    {
        lock (_sync)
        {
            bool isMouse = KeyNames.IsMouse(vk);

            // Modifiers always pass through. Only real key events change their state.
            if (KeyNames.IsModifier(vk))
            {
                if (physicalModifierEvent)
                    ModifierTracker.Update(vk, isDown);
                if (_recording && physicalModifierEvent && !CursorGuard.IsOwnWindowForeground())
                    Record(isDown ? RawEventKind.KeyDown : RawEventKind.KeyUp, vk);
                return false;
            }

            if (_captureDone != null)
                return HandleCapture(vk, isDown);

            var snap = _snapshot;

            // Record hotkey: starts an armed recording or stops a running one.
            if (_recordArmedStart != null || _recording)
            {
                if (!isDown && _swallowUp.Remove(vk))
                    return Block();
                if (isDown && Matches(snap.Record, CurrentBinding(vk)))
                {
                    _swallowUp.Add(vk);
                    if (_recording)
                        StopRecording();
                    else
                        BeginRecording(_recordMovesProvider?.Invoke() ?? true);
                    return Block();
                }
                if (_recording)
                {
                    // Everything else is recorded and passes through. Mouse buttons come from the mouse hook.
                    if (!isMouse && !CursorGuard.IsOwnWindowForeground())
                        Record(isDown ? RawEventKind.KeyDown : RawEventKind.KeyUp, vk);
                    return false;
                }
            }

            if (!isDown)
            {
                if (_swallowUp.Remove(vk))
                    return Block();

                if (_activeRemaps.Remove(vk, out var remapTarget))
                {
                    _injector.Post(() => SendRemapTarget(remapTarget, isDown: false));
                    return true;
                }

                if (_heldHotkeys.Remove(vk))
                {
                    foreach (var loop in snap.Loops)
                    {
                        if (loop.Mode == ActivationMode.Hold && loop.Hotkey?.Vk == vk)
                            StopLoop(loop.Id);
                    }
                    foreach (var macro in snap.Macros)
                    {
                        if (macro.Mode == ActivationMode.Hold && macro.Hotkey?.Vk == vk)
                            StopMacro(macro.Id);
                    }
                    return Block();
                }
                return false;
            }

            // Auto-repeat of a hotkey we already handled.
            if (_heldHotkeys.Contains(vk))
                return Block();

            // Auto-repeat of a remapped key: repeat the target key (not its modifiers).
            if (_activeRemaps.TryGetValue(vk, out var repeating))
            {
                if (!KeyNames.IsMouse(repeating.Vk))
                    _injector.Post(() => InputSender.Down(repeating.Vk));
                return true;
            }

            var pressed = CurrentBinding(vk);

            if (!_hotkeysSuspended)
            {
                if (Matches(snap.OverlayMode, pressed))
                {
                    _heldHotkeys.Add(vk);
                    _dispatcher.InvokeAsync(() => OverlayModeToggleRequested?.Invoke());
                    return Block();
                }

                if (Matches(snap.StopAll, pressed))
                {
                    _heldHotkeys.Add(vk);
                    StopAll();
                    return Block();
                }

                var loopTarget = snap.Loops.FirstOrDefault(l => l.Enabled && Matches(l.Hotkey, pressed));
                if (loopTarget != null)
                {
                    _heldHotkeys.Add(vk);
                    if (loopTarget.Mode == ActivationMode.Toggle)
                        ToggleLoop(loopTarget);
                    else
                        StartLoop(loopTarget);
                    return Block();
                }

                var macroTarget = snap.Macros.FirstOrDefault(m => m.Enabled && Matches(m.Hotkey, pressed));
                if (macroTarget != null)
                {
                    _heldHotkeys.Add(vk);
                    if (macroTarget.Mode == ActivationMode.Toggle)
                        ToggleMacro(macroTarget);
                    else
                        StartMacro(macroTarget);
                    return Block();
                }
            }

            // Remaps: an app-specific remap wins over an "all apps" one.
            var remap = FindRemap(snap, vk);
            if (remap?.Target is { IsSet: true } target)
            {
                _activeRemaps[vk] = target;
                _injector.Post(() => SendRemapTarget(target, isDown: true));
                return true;
            }

            return false;
        }
    }

    private RemapDefinition? FindRemap(EngineSnapshot snap, int vk)
    {
        RemapDefinition? forAllApps = null;
        string? foreground = null;
        foreach (var remap in snap.Remaps)
        {
            if (remap.SourceVk != vk)
                continue;
            if (remap.AppExe == null)
            {
                forAllApps ??= remap;
                continue;
            }
            foreground ??= ForegroundExeName() ?? "";
            if (string.Equals(foreground, remap.AppExe, StringComparison.OrdinalIgnoreCase))
                return remap;
        }
        return forAllApps;
    }

    private static void SendRemapTarget(HotkeyBinding target, bool isDown)
    {
        if (KeyNames.IsMouse(target.Vk) && !target.HasModifiers)
        {
            if (isDown)
                InputSender.Down(target.Vk);
            else
                InputSender.Up(target.Vk);
            return;
        }
        InputSender.SendBinding(target, isDown);
    }

    /// <summary>File name of the foreground window's process, e.g. "Diablo IV.exe" (cached per process).</summary>
    private string? ForegroundExeName()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
            return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (_processNames.TryGetValue(pid, out var cached))
            return cached;

        string name = "";
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new char[1024];
                uint size = (uint)buffer.Length;
                if (NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size))
                    name = Path.GetFileName(new string(buffer, 0, (int)size));
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }
        if (_processNames.Count > 256)
            _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    /// <summary>Blocks the event; with Alt or Win held, also masks it so no menu opens.</summary>
    private bool Block()
    {
        if (ModifierTracker.Alt || ModifierTracker.Win)
            _injector.Post(InputSender.SendMaskKey);
        return true;
    }

    // Called with the lock held.
    private bool HandleCapture(int vk, bool isDown)
    {
        if (!isDown)
            return _swallowUp.Remove(vk);

        if (!_captureAllowsPrimaryMouse && KeyNames.IsPrimaryMouse(vk))
            return false;

        _swallowUp.Add(vk);

        if (vk == KeyNames.VK_ESCAPE)
        {
            var cancelled = _captureCancelled;
            _captureDone = null;
            _captureCancelled = null;
            _dispatcher.InvokeAsync(() => cancelled?.Invoke());
            return Block();
        }

        var done = _captureDone!;
        var binding = CurrentBinding(vk);
        _captureDone = null;
        _captureCancelled = null;
        _dispatcher.InvokeAsync(() => done(binding));
        return Block();
    }

    // Input thread: the desktop changed (UAC, Ctrl+Alt+Del, lock). Releases may never arrive.
    private void OnDesktopSwitch()
    {
        lock (_sync)
        {
            var snap = _snapshot;
            foreach (var loop in snap.Loops)
            {
                if (loop.Mode == ActivationMode.Hold)
                    StopLoop(loop.Id, playSound: false);
            }
            foreach (var macro in snap.Macros)
            {
                if (macro.Mode == ActivationMode.Hold)
                    StopMacro(macro.Id, playSound: false);
            }
            foreach (var target in _activeRemaps.Values.ToList())
                _injector.Post(() => SendRemapTarget(target, isDown: false));
            _activeRemaps.Clear();
            _heldHotkeys.Clear();
            _swallowUp.Clear();
            _swallowLeftUp = false;
            ModifierTracker.Reset();
        }
    }

    private void OnRunnerExited(ActionRunner runner)
    {
        // Runner thread. If it ended on its own (error), stop its loop.
        if (runner.StopRequested)
            return;
        lock (_sync)
        {
            var owner = _running.FirstOrDefault(p => p.Value.Contains(runner));
            if (owner.Value != null)
                StopLoop(owner.Key, playSound: false);
        }
    }

    private void OnMacroExited(MacroRunner runner)
    {
        // Runner thread. A macro that finished on its own (Once / N times) stops here.
        if (runner.StopRequested)
            return;
        lock (_sync)
        {
            var owner = _runningMacros.FirstOrDefault(p => ReferenceEquals(p.Value, runner));
            if (owner.Value != null)
                StopMacro(owner.Key, playSound: true);
        }
    }

    private void RaiseStateChanged() => _dispatcher.InvokeAsync(() => StateChanged?.Invoke());

    public void Dispose()
    {
        StopAll();
        lock (_sync)
        {
            foreach (var target in _activeRemaps.Values)
                SendRemapTarget(target, isDown: false);
            _activeRemaps.Clear();
        }
        _input?.Dispose();
        _injector.Dispose();
    }
}
