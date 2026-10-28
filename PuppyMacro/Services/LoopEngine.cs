using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Services;

/// <summary>Read-only copy of everything the input hook needs. Replaced (never changed) by the UI.</summary>
internal sealed class EngineSnapshot
{
    public HotkeyBinding? StopAll { get; init; }
    public HotkeyBinding? OverlayMode { get; init; }
    public HotkeyBinding? Record { get; init; }
    public HotkeyBinding? OpenMain { get; init; }
    public LoopDefinition[] Loops { get; init; } = Array.Empty<LoopDefinition>();
    public MacroDefinition[] Macros { get; init; } = Array.Empty<MacroDefinition>();
    public RemapDefinition[] Remaps { get; init; } = Array.Empty<RemapDefinition>();

    /// <summary>The images of the macros' conditions, and where they are searched (Settings › Screen recognition).</summary>
    public ImageStore? Images { get; init; }
    public RecognitionDevice RecognitionDevice { get; init; }

    /// <summary>
    /// The macros whose calls to components fail the checks (a component the Library doesn't have, a wrong argument): they
    /// can't play (their hotkey passes to the app).
    /// </summary>
    public IReadOnlySet<Guid> Unplayable { get; init; } = new HashSet<Guid>();

    /// <summary>The Library's components, prepared once: what the macros' linked rows and Component conditions play.</summary>
    public ComponentPrograms Components { get; init; } = ComponentPrograms.Empty;

    /// <summary>
    /// The snapshot of the data now: copies of the macros and the Library's components prepared once, here on the UI thread,
    /// so playing never reads the Library.
    /// </summary>
    public static EngineSnapshot From(AppSettings settings, MacroLibrary macros)
    {
        var copies = macros.Macros.Select(m => m.Clone()).ToList();
        var signatures = new ComponentSignatures(macros.Library.AllComponents);
        return new EngineSnapshot
        {
            StopAll = settings.StopAllHotkey?.Clone(),
            OverlayMode = settings.OverlayModeHotkey?.Clone(),
            Record = settings.RecordHotkey?.Clone(),
            OpenMain = settings.OpenMainHotkey?.Clone(),
            Loops = settings.Loops.Select(l => l.Clone()).ToArray(),
            Macros = copies.ToArray(),
            Remaps = settings.Remaps.Where(r => r.Enabled).Select(r => r.Clone()).ToArray(),
            Images = macros.Images,
            RecognitionDevice = settings.RecognitionDevice,
            Unplayable = ComponentChecks.Unplayable(signatures, copies),
            Components = ComponentPrograms.Prepare(signatures.All),
        };
    }
}

/// <summary>
/// A clickable part of the overlay (a panel row, a floating button, the panel's Stop all, exit, open or
/// move button), in physical screen pixels. <see cref="Id"/> is the loop's or macro's, or
/// <see cref="StopAllId"/>, <see cref="ExitOverlayId"/>, <see cref="OpenMainOverlayId"/> or <see cref="MoveOverlayId"/>.
/// On a loop or macro a left click starts or stops it when <see cref="Startable"/> (enabled, not Hold) and a
/// right click enables or disables it; with <see cref="PassThrough"/> the click also reaches the app behind.
/// </summary>
internal readonly record struct PanelTarget(int Left, int Top, int Right, int Bottom, Guid Id,
    bool Startable = true, bool PassThrough = false)
{
    /// <summary>A loop or macro (not one of the panel's own buttons).</summary>
    public bool IsItem => Id != StopAllId && Id != ExitOverlayId && Id != OpenMainOverlayId && Id != MoveOverlayId;

    /// <summary>The overlay panel's Stop all button.</summary>
    public static readonly Guid StopAllId = new("5c1f0a3e-0b6e-4d0c-9a37-5f1e7c2b9d01");

    /// <summary>The overlay panel's exit button: leaves overlay mode, as the Overlay mode hotkey.</summary>
    public static readonly Guid ExitOverlayId = new("5c1f0a3e-0b6e-4d0c-9a37-5f1e7c2b9d02");

    /// <summary>The overlay panel's open buttons (in its title and Open PuppyMacro): leave overlay mode and open the main window.</summary>
    public static readonly Guid OpenMainOverlayId = new("5c1f0a3e-0b6e-4d0c-9a37-5f1e7c2b9d04");

    /// <summary>The overlay panel's move handle: pressed and dragged, it moves the panel.</summary>
    public static readonly Guid MoveOverlayId = new("5c1f0a3e-0b6e-4d0c-9a37-5f1e7c2b9d03");
}

/// <summary>A drag of the overlay panel's move handle: pressed, moving, released.</summary>
internal enum PanelDragPhase { Started, Moved, Ended }

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

    // Remaps currently held: source key (the main key, or the modifier key of a modifier alone) -> target sent.
    private readonly Dictionary<int, HotkeyBinding> _activeRemaps = new();

    // The remap that keeps its target's modifiers held while the source's are (Keep modifiers held).
    private readonly RemapHold _hold = new();

    // Modifiers the hold pressed again when another key ended it: their physical release is blocked and sent after that
    // press, in order, so it never comes before it (a modifier left down).
    private readonly HashSet<int> _pressedAgain = new();

    // The keys of a hold's targets held down: source key -> target key, released with the source key (also after the hold).
    private readonly Dictionary<int, int> _holdKeysDown = new();

    // Remaps held that a source with extra modifiers started (Pass extra modifiers): source key -> what releases its target.
    private readonly Dictionary<int, (int TargetVk, List<int> Modifiers)> _extraKeysDown = new();

    // Where the keys of a hold or of extra modifiers go: null, SendInput on the injector; the unit tests keep them (they
    // never send modifiers).
    private readonly Action<IReadOnlyList<KeyStep>>? _remapKeysForTests;

    private Action<HotkeyBinding>? _captureDone;
    private Action? _captureCancelled;
    private bool _captureAllowsPrimaryMouse;
    private bool _captureHotkey;
    private bool _captureModifierAlone;

    // While a capture takes a modifier alone: the modifier key pressed with no other modifier held, captured if it is
    // released before any other key; -1 once another modifier was pressed with it (until all are released).
    private int _captureLoneModifier;

    // ---- Wheel hotkeys: one notch (WHEEL_DELTA) is one press; smaller steps (touchpads) add up ----
    private int _wheelVk;
    private int _wheelAccumulated;

    // ---- Recording ----
    private Action? _recordArmedStart;
    private Func<bool>? _recordMovesProvider;
    private bool _recording;
    private bool _recordMoves;
    private long _recordStart;
    private List<RawEvent> _recorded = new();

    /// <summary>The modifiers whose press the recording has, so a release whose press came before it is left out.</summary>
    private readonly HashSet<int> _recordedDown = new();
    private Action<List<RawEvent>>? _recordStopped;

    // ---- Clickable overlay panel ----
    private IReadOnlyList<PanelTarget>? _panelTargets;

    // ---- Dragging the overlay panel by its move handle ----
    private bool _panelDragging;
    private int _panelDragX, _panelDragY;   // where the handle was pressed (physical pixels)
    private int _panelDragDx, _panelDragDy; // how far the mouse is from there
    private bool _panelDragPosted;          // a Moved is already on its way to the UI thread

    // ---- Process .exe names, cached per process (app-specific remaps, loops and macros) ----
    private readonly Dictionary<uint, string> _processNames = new();

    // ---- The last app in front other than PuppyMacro (app-specific loops, macros and the overlay) ----
    private string? _foregroundApp;
    private IntPtr _lastOtherWindow;

    // The window in front belongs to an app run as administrator (PuppyMacro not): the hooks get none of its keys.
    private bool _adminWindowInFront;

    private volatile bool _hotkeysSuspended;

    public LoopEngine(EngineSnapshot snapshot, Dispatcher dispatcher)
    {
        _snapshot = snapshot;
        _dispatcher = dispatcher;
        _paster = new ClipboardPaster(dispatcher);
    }

    /// <summary>The unit tests' engine: the keys a remap's hold sends go to <paramref name="holdKeys"/> instead.</summary>
    internal LoopEngine(EngineSnapshot snapshot, Dispatcher dispatcher, Action<IReadOnlyList<KeyStep>> holdKeys)
        : this(snapshot, dispatcher)
    {
        _remapKeysForTests = holdKeys;
    }

    /// <summary>Raised (asynchronously, on the UI thread) when any loop or macro starts or stops.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (asynchronously, on the UI thread) when the overlay mode hotkey or the overlay panel's exit button is pressed.</summary>
    public event Action? OverlayModeToggleRequested;

    /// <summary>
    /// Raised (asynchronously, on the UI thread) when the Open PuppyMacro hotkey or one of the overlay panel's open buttons
    /// is pressed: the UI leaves overlay mode, if on, and shows the main window.
    /// </summary>
    public event Action? OverlayExitAndOpenRequested;

    /// <summary>
    /// Raised (asynchronously, on the UI thread) on a right click on a loop or macro in the overlay: the UI
    /// enables or disables it (its id).
    /// </summary>
    public event Action<Guid>? OverlayEnableToggleRequested;

    /// <summary>
    /// Raised (asynchronously, on the UI thread) while the overlay panel's move handle is dragged: the phase and
    /// how far the mouse is from where it was pressed, in physical pixels. Moves are coalesced: the UI gets the
    /// latest offset, never a queue of them.
    /// </summary>
    internal event Action<PanelDragPhase, int, int>? OverlayPanelDragged;

    /// <summary>Raised (asynchronously, on the UI thread) to play a sound: name, start (true) or stop (false).</summary>
    public event Action<string, bool>? SoundRequested;

    /// <summary>Raised (on the UI thread) while recording, with the number of raw events so far.</summary>
    public event Action<int>? RecordingProgress;

    /// <summary>
    /// A key or button pressed while recording (the kind, its virtual-key code, X, Y, ms from the start), raised on a
    /// thread-pool thread so the hook never waits: an AI app's recording takes the screen there.
    /// </summary>
    public event Action<RawEventKind, int, int, int, double>? RecordedInput;

    /// <summary>
    /// Raised (asynchronously, on the UI thread) when another app comes in front, with its .exe name ("" when it
    /// cannot be read). PuppyMacro's own windows never count: the last other app stays.
    /// </summary>
    public event Action<string>? ForegroundAppChanged;

    /// <summary>The .exe name of the last app in front other than PuppyMacro; null until one is known.</summary>
    /// <summary>The last window of another app that was in front (PuppyMacro's own windows never count).</summary>
    public IntPtr LastOtherWindow
    {
        get { lock (_sync) return _lastOtherWindow; }
    }

    public string? ForegroundApp
    {
        get { lock (_sync) return _foregroundApp; }
    }

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
            hook.Wheel = OnWheel;
        }, OnDesktopSwitch, OnForegroundWindow);
        _input.Start();
        OnForegroundWindow(NativeMethods.GetForegroundWindow());
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

    /// <summary>Starts a macro as the snapshot has it (with its components), whatever copy the caller holds.</summary>
    public void StartMacro(MacroDefinition macro)
    {
        lock (_sync)
        {
            if (Playable(macro.Id) is not { } snapped)
                return;
            macro = snapped;
            if (_runningMacros.ContainsKey(macro.Id) || macro.Actions.Count == 0)
                return;
            // Debug on: the run writes its log (RunLog only queues, so this does not slow the hook that may call it).
            var log = macro.Debug ? new RunLog(DebugStore.NewUserMacroRun(macro.Name), macro, "a run started by the user") : null;
            var runner = new MacroRunner(macro, _paster, _snapshot.Images, _snapshot.RecognitionDevice, log: log, programs: _snapshot.Components);
            runner.Exited += OnMacroExited;
            _runningMacros[macro.Id] = runner;
            runner.Start();
            RequestSound(macro.SoundEnabled, macro.SoundName, start: true);
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Plays a saved macro for an AI app's test (test_macro): a running macro as Play makes it (Stop all and its hotkey stop
    /// it), always with its Debug log, saying <paramref name="why"/>. <paramref name="exited"/> runs on the macro's thread
    /// when it ends. Null when it can't start (no actions, already running).
    /// </summary>
    public MacroRunner? StartMacroTest(MacroDefinition macro, string folder, string why, Action<MacroRunner> exited)
    {
        lock (_sync)
        {
            if (Playable(macro.Id) is not { } snapped)
                return null;
            macro = snapped;
            if (_runningMacros.ContainsKey(macro.Id) || macro.Actions.Count == 0)
                return null;
            var runner = new MacroRunner(macro, _paster, _snapshot.Images, _snapshot.RecognitionDevice, log: new RunLog(folder, macro, why),
                programs: _snapshot.Components);
            runner.Exited += OnMacroExited;
            runner.Exited += exited;
            _runningMacros[macro.Id] = runner;
            runner.Start();
            RequestSound(macro.SoundEnabled, macro.SoundName, start: true);
            RaiseStateChanged();
            return runner;
        }
    }

    /// <summary>
    /// Plays <paramref name="action"/> once for the editor's Test (<see cref="MacroDefinition.ForTest"/>), with the
    /// snapshot's components (a linked row plays its component). It runs as a macro of its own (no sound), so Stop all
    /// stops it like any other. <paramref name="finished"/> runs on the UI thread when it ends, also when it is stopped.
    /// </summary>
    /// <param name="parameters">Inside a component's editor: its parameters, whose Examples the action's calls pass on.</param>
    public void TestMacroAction(MacroAction action, Action finished, IEnumerable<ComponentParameter>? parameters = null)
    {
        lock (_sync)
        {
            var test = MacroDefinition.ForTest(action);
            var runner = new MacroRunner(test, _paster, _snapshot.Images, _snapshot.RecognitionDevice,
                programs: _snapshot.Components, arguments: parameters == null ? null : FrameArguments.Examples(parameters));
            runner.Exited += OnMacroExited;
            runner.Exited += _ => _dispatcher.InvokeAsync(finished);
            _runningMacros[test.Id] = runner;
            runner.Start();
            RaiseStateChanged();
        }
    }

    /// <summary>The snapshot's macro with this id, or null when it is not there or links a component the Library doesn't have.</summary>
    private MacroDefinition? Playable(Guid id) =>
        _snapshot.Unplayable.Contains(id) ? null : _snapshot.Macros.FirstOrDefault(m => m.Id == id);

    public void StopMacro(Guid macroId) => StopMacro(macroId, playSound: true);

    /// <summary>Stops a macro, saying why in its Debug log (an AI app's test that reached its time limit).</summary>
    public void StopMacro(Guid macroId, string why) => StopMacro(macroId, playSound: true, why);

    private void StopMacro(Guid macroId, bool playSound, string? why = null)
    {
        lock (_sync)
        {
            if (!_runningMacros.Remove(macroId, out var runner))
                return;
            runner.Stop(why);
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
        if (id == PanelTarget.StopAllId)
        {
            StopAll();
            return;
        }
        if (id == PanelTarget.ExitOverlayId)
        {
            _dispatcher.InvokeAsync(() => OverlayModeToggleRequested?.Invoke());
            return;
        }
        if (id == PanelTarget.OpenMainOverlayId)
        {
            _dispatcher.InvokeAsync(() => OverlayExitAndOpenRequested?.Invoke());
            return;
        }
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
        _recordedDown.Clear();
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
        if (kind is RawEventKind.ButtonDown or RawEventKind.KeyDown && RecordedInput is { } recorded)
            System.Threading.ThreadPool.QueueUserWorkItem(_ => recorded(kind, vk, x, y, t));
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
            if (targets == null)
                _panelDragging = false; // overlay mode ended: no release will come
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
    internal bool OnMouseDetail(InputHook.MouseKind kind, int vk, int x, int y, int wheelDelta)
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

            if (_panelDragging)
            {
                // Moves are never blocked (that would hold the cursor still): they move the panel.
                if (kind == InputHook.MouseKind.Move)
                {
                    _panelDragDx = x - _panelDragX;
                    _panelDragDy = y - _panelDragY;
                    PostPanelDrag();
                    return false;
                }
                if (kind == InputHook.MouseKind.ButtonUp && vk == KeyNames.VK_LBUTTON)
                {
                    _panelDragging = false;
                    int dx = x - _panelDragX, dy = y - _panelDragY;
                    _dispatcher.InvokeAsync(() => OverlayPanelDragged?.Invoke(PanelDragPhase.Ended, dx, dy));
                    return true;
                }
            }

            if (_panelTargets != null && vk is KeyNames.VK_LBUTTON or KeyNames.VK_RBUTTON)
            {
                bool left = vk == KeyNames.VK_LBUTTON;
                // The release of a press on the overlay: here, or in OnKey when overlay mode ended before it (its exit button).
                if (kind == InputHook.MouseKind.ButtonUp && _swallowUp.Remove(vk))
                    return true;
                if (kind == InputHook.MouseKind.ButtonDown && !_hotkeysSuspended && TargetAt(x, y) is PanelTarget target)
                {
                    if (left && target.Id == PanelTarget.MoveOverlayId)
                    {
                        // The press and its release never reach the app: it keeps the focus.
                        _panelDragging = true;
                        _panelDragX = x;
                        _panelDragY = y;
                        _panelDragDx = _panelDragDy = 0;
                        _dispatcher.InvokeAsync(() => OverlayPanelDragged?.Invoke(PanelDragPhase.Started, 0, 0));
                        return true;
                    }
                    if (left)
                    {
                        // A disabled or Hold item does nothing on a left click.
                        if (target.Startable)
                            ToggleById(target.Id);
                    }
                    else
                    {
                        // A right click on the panel's own buttons reaches the app as usual.
                        if (!target.IsItem)
                            return false;
                        Guid id = target.Id;
                        _dispatcher.InvokeAsync(() => OverlayEnableToggleRequested?.Invoke(id));
                    }
                    if (target.PassThrough)
                        return false;
                    _swallowUp.Add(vk);
                    return true;
                }
            }
            return false;
        }
    }

    // Called with the lock held, on the input thread.
    private PanelTarget? TargetAt(int x, int y)
    {
        foreach (var target in _panelTargets!)
        {
            if (x >= target.Left && x < target.Right && y >= target.Top && y < target.Bottom)
                return target;
        }
        return null;
    }

    // Called with the lock held, on the input thread: one Moved on its way at a time, with the latest offset.
    private void PostPanelDrag()
    {
        if (_panelDragPosted)
            return;
        _panelDragPosted = true;
        _dispatcher.InvokeAsync(() =>
        {
            int dx, dy;
            bool dragging;
            lock (_sync)
            {
                dx = _panelDragDx;
                dy = _panelDragDy;
                dragging = _panelDragging;
                _panelDragPosted = false;
            }
            if (dragging)
                OverlayPanelDragged?.Invoke(PanelDragPhase.Moved, dx, dy);
        });
    }

    // ================= Key capture =================

    /// <summary>
    /// Captures the next key or mouse button, with the Ctrl/Alt/Shift/Win physically held at that
    /// moment. Esc cancels. When <paramref name="allowPrimaryMouse"/> is false, left and right
    /// click pass through so the user can still click the UI.
    /// With <paramref name="hotkey"/>, while a modifier is held, left and right click, the scroll
    /// wheel and Esc are captured too (see <see cref="HotkeyRules"/>); alone they still pass
    /// through, scroll and cancel.
    /// With <paramref name="modifierAlone"/> (a remap's source and target), a Ctrl, Alt, Shift or Win key pressed and
    /// released alone is captured too, by its side (e.g. Right Alt).
    /// </summary>
    public void BeginCapture(Action<HotkeyBinding> done, Action cancelled, bool allowPrimaryMouse, bool hotkey = false,
        bool modifierAlone = false)
    {
        CancelCapture();
        lock (_sync)
        {
            _captureDone = done;
            _captureCancelled = cancelled;
            _captureAllowsPrimaryMouse = allowPrimaryMouse;
            _captureHotkey = hotkey;
            _captureModifierAlone = modifierAlone;
            _captureLoneModifier = 0;
        }
    }

    private Action? _readyEnter;
    private Action? _readyEscape;

    /// <summary>
    /// Until one of them is pressed (or <see cref="StopReadyKeys"/>): Enter calls <paramref name="enter"/>, Esc
    /// <paramref name="escape"/> (on the UI thread), pressed alone in any app; they are blocked, so the app in front does not
    /// get them. Every other key passes. For an AI request's "ready?" window.
    /// </summary>
    public void WatchReadyKeys(Action enter, Action escape)
    {
        lock (_sync)
        {
            _readyEnter = enter;
            _readyEscape = escape;
        }
    }

    public void StopReadyKeys()
    {
        lock (_sync)
            _readyEnter = _readyEscape = null;
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

    /// <summary><paramref name="vk"/> with the modifiers physically held, each on the side held (both: Any).</summary>
    private static HotkeyBinding CurrentBinding(int vk)
    {
        var binding = new HotkeyBinding
        {
            Vk = vk,
            Ctrl = ModifierTracker.Ctrl,
            Alt = ModifierTracker.Alt,
            Shift = ModifierTracker.Shift,
            Win = ModifierTracker.Win,
        };
        if (binding.Ctrl) binding.CtrlSide = ModifierTracker.CtrlSide;
        if (binding.Alt) binding.AltSide = ModifierTracker.AltSide;
        if (binding.Shift) binding.ShiftSide = ModifierTracker.ShiftSide;
        if (binding.Win) binding.WinSide = ModifierTracker.WinSide;
        return binding;
    }

    private static bool Matches(HotkeyBinding? hotkey, HotkeyBinding pressed) =>
        hotkey != null && hotkey.Matches(pressed);

    // ================= Key routing (input thread) =================

    // Returns true to block the event. Internal for the unit tests.
    internal bool OnKey(int vk, bool isDown, bool physicalModifierEvent, bool altGrCtrl = false)
    {
        lock (_sync)
        {
            if (KeyNames.IsModifier(vk))
                return OnModifier(vk, isDown, physicalModifierEvent, altGrCtrl);

            // A key that ends a remap's hold (Ctrl + C after Ctrl + Tab → Alt + Tab): the target's modifiers are released
            // and the source's pressed again first; a key that passes is sent after them, so the app gets Ctrl + C.
            bool afterHold = isDown && _hold.Active && !_hold.Continues(vk) && !_activeRemaps.ContainsKey(vk)
                && !_holdKeysDown.ContainsKey(vk) && !_extraKeysDown.ContainsKey(vk);
            if (afterHold)
                EndHold(pressAgain: true);
            bool blocked = OnOtherKey(vk, isDown);
            if (afterHold && !blocked)
            {
                SendRemapKeys(new List<KeyStep> { new(vk, true) });
                return true;
            }
            return blocked;
        }
    }

    // A key or mouse button that is not a modifier. Called with the lock held.
    private bool OnOtherKey(int vk, bool isDown)
    {
        bool isMouse = KeyNames.IsMouse(vk);

        // The release of a key PuppyMacro holds for something running (a remap's target, a Hold item's hotkey) comes
        // first, whatever takes keys now (a key field waiting, a recording): otherwise the target or the item stays on.
        if (!isDown && ReleaseOwned(vk) is bool owned)
            return owned;

        // An AI request waits for the user to be ready: Enter or Esc alone, from any app, answers it; every other key
        // passes, so the user can open the right screen first (WatchReadyKeys). Not while PuppyMacro itself uses the
        // keys (a dialog or an editor open, a key field waiting, a recording). Only a press answers: the release of a
        // key held before is the app's (the press's own release is swallowed).
        if (_readyEnter != null && isDown && vk is KeyNames.VK_RETURN or KeyNames.VK_ESCAPE && !ModifierTracker.Any
            && !_hotkeysSuspended && _captureDone == null && !_recording)
        {
            Action answer = vk == KeyNames.VK_RETURN ? _readyEnter : _readyEscape!;
            _readyEnter = _readyEscape = null;
            _swallowUp.Add(vk);
            _dispatcher.InvokeAsync(answer);
            return Block();
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
                {
                    // The record hotkey's own modifiers, pressed last and still held: not part of the macro (their release
                    // comes after the recording; played back they would stay down).
                    while (_recorded.Count > 0 && _recorded[^1] is { Kind: RawEventKind.KeyDown } last && KeyNames.IsModifier(last.Vk)
                           && _recordedDown.Contains(last.Vk))
                    {
                        _recordedDown.Remove(last.Vk);
                        _recorded.RemoveAt(_recorded.Count - 1);
                    }
                    StopRecording();
                }
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

            // Released while a running loop holds it (pressed before the loop started): the
            // release would let go of the loop's key.
            if (IsHeldByLoop(vk))
                return Block();
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
        if (_holdKeysDown.TryGetValue(vk, out int repeatingKey))
        {
            if (!KeyNames.IsMouse(repeatingKey))
                SendRemapKeys(new List<KeyStep> { new(repeatingKey, true) });
            return true;
        }
        if (_extraKeysDown.TryGetValue(vk, out var repeatingExtra))
        {
            if (!KeyNames.IsMouse(repeatingExtra.TargetVk))
                SendRemapKeys(new List<KeyStep> { new(repeatingExtra.TargetVk, true) });
            return true;
        }

        var pressed = CurrentBinding(vk);

        // The source again while its remap holds the target's modifiers: only the target's key (Tab again in the Alt + Tab
        // switcher), whatever app is in front now.
        if (_hold.IsSourceAgain(pressed))
        {
            _holdKeysDown[vk] = _hold.TargetVk;
            SendRemapKeys(_hold.Again());
            return true;
        }

        // A remap for the app in front wins over the loops and macros for all apps, as an item for that app does.
        var remap = FindRemap(snap, pressed);
        bool remapForApp = remap is { AppExe: not null, Target.IsSet: true };

        if (!_hotkeysSuspended && RunHotkey(snap, pressed, canHold: true, run: true, allApps: !remapForApp))
        {
            _heldHotkeys.Add(vk);
            return Block();
        }

        // With an editor open, the Stop all hotkey still stops what runs: the editor's Test of an action.
        if (_hotkeysSuspended && Matches(snap.StopAll, pressed) && (_running.Count > 0 || _runningMacros.Count > 0))
        {
            StopAll();
            _heldHotkeys.Add(vk);
            return Block();
        }

        // A running loop holds this key down (Hold down row): the physical press changes
        // nothing, and its release would let go of the loop's key. Swallow both.
        if (IsHeldByLoop(vk))
        {
            _swallowUp.Add(vk);
            return Block();
        }

        // Pass extra modifiers, only when nothing matched these keys exactly (a remap, a hotkey): the source with other
        // modifiers held too. The source again while its remap holds (Shift + Ctrl + Tab, Ctrl + Tab → Alt + Tab) sends
        // only the target's key, so the hold goes on; otherwise the remap that has the most of these modifiers starts.
        bool withExtras = false;
        if (remap?.Target is not { IsSet: true })
        {
            if (_hold.IsSourceAgainWithExtras(pressed))
            {
                _holdKeysDown[vk] = _hold.TargetVk;
                SendRemapKeys(_hold.Again());
                return true;
            }
            remap = FindRemapWithExtras(snap, pressed);
            withExtras = remap != null;
        }

        // Remaps: an app-specific remap wins over an "all apps" one. A source with modifiers (Alt + A): the
        // modifiers held for it are released first, so the app gets only the target.
        if (remap?.Target is { IsSet: true } target)
        {
            if (_hold.Active)
                EndHold(pressAgain: false);
            var held = ModifierTracker.HeldKeys();
            // With extra modifiers, only the source's own are released: the others stay with the target.
            var sourceKeys = withExtras ? RemapExtras.SourceKeys(remap.Source!, held) : held;
            if (RemapHold.Applies(remap))
            {
                // Its key alone is released with the source's key; its modifiers with the source's (EndHold).
                _holdKeysDown[vk] = target.Vk;
                SendRemapKeys(_hold.Start(remap, sourceKeys, held.Except(sourceKeys).ToList()));
                return true;
            }
            if (withExtras)
            {
                var steps = RemapExtras.Press(remap.Source!, target, held, out var pressedModifiers);
                _extraKeysDown[vk] = (target.Vk, pressedModifiers);
                SendRemapKeys(steps);
                return true;
            }
            _activeRemaps[vk] = target;
            bool releaseHeld = pressed.HasModifiers;
            _injector.Post(() => SendRemapTarget(target, isDown: true, releaseHeld));
            return true;
        }

        return false;
    }

    /// <summary>
    /// A Ctrl, Alt, Shift or Win event. Called with the lock held. Modifiers pass through, and only real key events
    /// change their state; a modifier key that is a remap's source (Right Alt → Enter) is blocked instead, sends its
    /// target and does not count as a modifier. While a key field waits for a modifier alone, pressing and releasing
    /// one alone captures it.
    /// </summary>
    /// <summary>
    /// Called with the lock held: the release of a key a remap is holding its target for, or of a Hold item's hotkey (its
    /// loops and macros stop). True to block it; null when PuppyMacro holds nothing for this key.
    /// </summary>
    private bool? ReleaseOwned(int vk)
    {
        if (_activeRemaps.Remove(vk, out var remapTarget))
        {
            _injector.Post(() => SendRemapTarget(remapTarget, isDown: false));
            return true;
        }
        if (_holdKeysDown.Remove(vk, out int heldKey))
        {
            SendRemapKeys(new List<KeyStep> { new(heldKey, false) });
            return true;
        }
        if (_extraKeysDown.Remove(vk, out var extra))
        {
            SendRemapKeys(RemapExtras.Release(extra.TargetVk, extra.Modifiers));
            return true;
        }
        if (!_heldHotkeys.Remove(vk))
            return null;
        var snap = _snapshot;
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

    private bool OnModifier(int vk, bool isDown, bool physical, bool altGrCtrl)
    {
        if (physical && !isDown)
        {
            // One of the source's modifiers released: the hold ends, the target's modifiers are released.
            if (_hold.EndsWith(vk))
                EndHold(pressAgain: false);
            // A modifier the hold pressed again: its release goes after that press.
            if (_pressedAgain.Remove(vk))
            {
                if (!OnModifierEvent(vk, isDown, physical, altGrCtrl))
                    SendRemapKeys(new List<KeyStep> { new(vk, false) });
                return true;
            }
        }
        return OnModifierEvent(vk, isDown, physical, altGrCtrl);
    }

    /// <summary>
    /// Ends the remap's hold: the target's modifiers are released and, with <paramref name="pressAgain"/> (another key
    /// pressed), the source's modifiers still held are pressed again. Called with the lock held.
    /// </summary>
    private void EndHold(bool pressAgain)
    {
        var steps = _hold.End(pressAgain ? ModifierTracker.HeldKeys() : null);
        foreach (var step in steps)
        {
            if (step.Down)
                _pressedAgain.Add(step.Vk);
        }
        SendRemapKeys(steps);
    }

    /// <summary>
    /// Sends the keys of a remap's hold on the injector, after what it already has and in order: a press never comes after
    /// the release that follows it.
    /// </summary>
    private void SendRemapKeys(List<KeyStep> steps)
    {
        if (steps.Count == 0)
            return;
        if (_remapKeysForTests != null)
            _remapKeysForTests(steps);
        else
            _injector.Post(() => InputSender.Send(steps));
    }

    /// <summary>
    /// Ends the hold and releases the modifiers it pressed again, and the targets started with extra modifiers (the
    /// desktop changed, PuppyMacro closes: the releases of the keys held may never come). Called with the lock held.
    /// </summary>
    private List<KeyStep> ReleaseHold()
    {
        var steps = _extraKeysDown.Values.SelectMany(e => RemapExtras.Release(e.TargetVk, e.Modifiers)).ToList();
        _extraKeysDown.Clear();
        steps.AddRange(_holdKeysDown.Values.Select(vk => new KeyStep(vk, false)));
        _holdKeysDown.Clear();
        steps.AddRange(_hold.End());
        steps.AddRange(_pressedAgain.Select(vk => new KeyStep(vk, false)));
        _pressedAgain.Clear();
        return steps;
    }

    private bool OnModifierEvent(int vk, bool isDown, bool physical, bool altGrCtrl)
    {
        if (!physical)
        {
            // AltGr is the fake left Ctrl plus Right Alt: while Right Alt is remapped, its fake Ctrl goes with it.
            return altGrCtrl && _captureDone == null && !_recording
                && FindRemap(_snapshot, HotkeyBinding.FromKey(KeyNames.VK_RMENU)) != null;
        }

        // The release of a remapped modifier releases its target, whatever takes keys now (as ReleaseOwned for other keys).
        if (!isDown && _activeRemaps.Remove(vk, out var remapTarget))
        {
            _injector.Post(() => SendRemapTarget(remapTarget, isDown: false));
            return true;
        }
        if (_captureDone == null && !_recording)
        {
            if (isDown && _activeRemaps.TryGetValue(vk, out var repeating))
            {
                // Auto-repeat of a remapped modifier: repeat the target key (not its modifiers).
                if (!KeyNames.IsMouse(repeating.Vk))
                    _injector.Post(() => InputSender.Down(repeating.Vk));
                return true;
            }
            // The state before this press: the other modifiers held (exact match, as for any source).
            if (isDown && FindRemap(_snapshot, CurrentBinding(vk))?.Target is { IsSet: true } target)
            {
                _activeRemaps[vk] = target;
                _injector.Post(() => SendRemapTarget(target, isDown: true));
                return true;
            }
        }

        if (isDown && _captureDone != null && _captureModifierAlone && _captureLoneModifier != vk)
            _captureLoneModifier = ModifierTracker.Any || _captureLoneModifier != 0 ? -1 : vk;

        ModifierTracker.Update(vk, isDown);
        // A modifier's release is recorded only after its press was (one held before the recording, such as the record
        // hotkey's, would be a Key up with no Key down).
        if (_recording && !CursorGuard.IsOwnWindowForeground())
        {
            if (isDown)
            {
                _recordedDown.Add(vk);
                Record(RawEventKind.KeyDown, vk);
            }
            else if (_recordedDown.Remove(vk))
                Record(RawEventKind.KeyUp, vk);
        }

        if (!isDown && _captureDone != null && _captureModifierAlone)
        {
            if (vk == _captureLoneModifier)
            {
                var done = _captureDone;
                var binding = HotkeyBinding.FromKey(vk);
                _captureDone = null;
                _captureCancelled = null;
                _captureLoneModifier = 0;
                _dispatcher.InvokeAsync(() => done(binding));
                // A lone Alt or Win release would open a menu or Start: release it behind the mask key.
                if (vk is KeyNames.VK_LMENU or KeyNames.VK_RMENU or KeyNames.VK_LWIN or KeyNames.VK_RWIN)
                {
                    _injector.Post(() => InputSender.MaskedUp(vk));
                    return true;
                }
                return false;
            }
            if (!ModifierTracker.Any)
                _captureLoneModifier = 0;
        }
        return false;
    }

    /// <summary>
    /// Finds the global, loop or macro hotkey that matches <paramref name="pressed"/> and, with
    /// <paramref name="run"/>, runs it. Without <paramref name="canHold"/> (the wheel, which has
    /// no release) Hold loops and macros are skipped; without <paramref name="allApps"/> (a remap for the app in front
    /// has the key) the loops and macros for all apps are. Called with the lock held.
    /// </summary>
    private bool RunHotkey(EngineSnapshot snap, HotkeyBinding pressed, bool canHold, bool run, bool allApps = true)
    {
        if (Matches(snap.OverlayMode, pressed))
        {
            if (run)
                _dispatcher.InvokeAsync(() => OverlayModeToggleRequested?.Invoke());
            return true;
        }

        if (Matches(snap.StopAll, pressed))
        {
            if (run)
                StopAll();
            return true;
        }

        if (Matches(snap.OpenMain, pressed))
        {
            if (run)
                _dispatcher.InvokeAsync(() => OverlayExitAndOpenRequested?.Invoke());
            return true;
        }

        // A loop or macro for the app in front wins over one for all apps; one for another app lets the key pass.
        foreach (bool forAllApps in allApps ? new[] { false, true } : new[] { false })
        {
            bool Usable(string? appExe) => forAllApps ? appExe == null : appExe != null && AppScope.Same(appExe, _foregroundApp);

            var loopTarget = snap.Loops.FirstOrDefault(l => l.Enabled && Usable(l.AppExe)
                && Matches(l.Hotkey, pressed) && (canHold || l.Mode == ActivationMode.Toggle));
            if (loopTarget != null)
            {
                if (!run)
                    return true;
                if (loopTarget.Mode == ActivationMode.Toggle)
                    ToggleLoop(loopTarget);
                else
                    StartLoop(loopTarget);
                return true;
            }

            var macroTarget = snap.Macros.FirstOrDefault(m => m.Enabled && !snap.Unplayable.Contains(m.Id) && Usable(m.AppExe)
                && Matches(m.Hotkey, pressed) && (canHold || m.Mode == ActivationMode.Toggle));
            if (macroTarget != null)
            {
                if (!run)
                    return true;
                if (macroTarget.Mode == ActivationMode.Toggle)
                    ToggleMacro(macroTarget);
                else
                    StartMacro(macroTarget);
                return true;
            }
        }
        return false;
    }

    /// <summary>A running loop holds this key or button down (Hold down row). Called with the lock held.</summary>
    private bool IsHeldByLoop(int vk)
    {
        foreach (var runners in _running.Values)
        {
            foreach (var runner in runners)
            {
                if (runner.HeldVk == vk && !runner.StopRequested)
                    return true;
            }
        }
        return false;
    }

    // Runs on the input thread. Returns true to block the event.
    internal bool OnWheel(bool horizontal, int delta)
    {
        if (delta == 0)
            return false;
        lock (_sync)
        {
            int vk = KeyNames.WheelVk(horizontal, delta);
            bool modifier = ModifierTracker.Any;

            // Capturing a hotkey: Ctrl/Alt/Shift/Win + wheel is captured; the wheel alone scrolls.
            if (_captureDone != null)
            {
                if (!_captureHotkey || !modifier)
                    return false;
                var done = _captureDone;
                var binding = CurrentBinding(vk);
                _captureDone = null;
                _captureCancelled = null;
                _dispatcher.InvokeAsync(() => done(binding));
                return Block();
            }

            // With an editor open, the Stop all hotkey still stops what runs (an editor's Test of an action), as in OnKey.
            if (_hotkeysSuspended && !_recording && modifier && Matches(_snapshot.StopAll, CurrentBinding(vk))
                && (_running.Count > 0 || _runningMacros.Count > 0))
            {
                StopAll();
                return Block();
            }

            // Wheel hotkeys always have a modifier (HotkeyRules); recording keeps every wheel event.
            if (_recording || _hotkeysSuspended || !modifier)
            {
                _wheelVk = 0;
                return false;
            }

            var snap = _snapshot;
            var pressed = CurrentBinding(vk);
            if (!RunHotkey(snap, pressed, canHold: false, run: false))
            {
                _wheelVk = 0;
                return false;
            }

            // One notch (WHEEL_DELTA) is one press. Touchpads and free-spinning wheels send
            // smaller steps: they add up, in the same direction, to whole notches.
            if (vk != _wheelVk)
            {
                _wheelVk = vk;
                _wheelAccumulated = 0;
            }
            _wheelAccumulated += Math.Abs(delta);
            while (_wheelAccumulated >= KeyNames.WheelDelta)
            {
                _wheelAccumulated -= KeyNames.WheelDelta;
                RunHotkey(snap, pressed, canHold: false, run: true);
            }
            // Blocked even below a notch: the app must not scroll or zoom with the hotkey's modifiers.
            return Block();
        }
    }

    /// <summary>The remap whose source is exactly <paramref name="pressed"/> (key, modifiers and their sides).</summary>
    private RemapDefinition? FindRemap(EngineSnapshot snap, HotkeyBinding pressed)
    {
        RemapDefinition? forAllApps = null;
        string? foreground = null;
        foreach (var remap in snap.Remaps)
        {
            if (remap.Source?.Matches(pressed) != true)
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

    /// <summary>
    /// The remap with Pass extra modifiers whose source is <paramref name="pressed"/> with other modifiers held too: one for
    /// the app in front before one for all apps, then the one whose source has the most of the modifiers held (Shift +
    /// Alt + A: Alt + Shift + A's own remap is an exact match; Alt + A's comes before A's).
    /// </summary>
    private RemapDefinition? FindRemapWithExtras(EngineSnapshot snap, HotkeyBinding pressed)
    {
        RemapDefinition? best = null;
        bool bestForApp = false;
        int bestCount = -1;
        string? foreground = null;
        foreach (var remap in snap.Remaps)
        {
            if (!RemapExtras.Applies(remap) || remap.Target is not { IsSet: true } || !remap.Source!.MatchesWithExtras(pressed))
                continue;
            bool forApp = remap.AppExe != null;
            if (forApp)
            {
                foreground ??= ForegroundExeName() ?? "";
                if (!string.Equals(foreground, remap.AppExe, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            int count = (remap.Source.Ctrl ? 1 : 0) + (remap.Source.Alt ? 1 : 0) + (remap.Source.Shift ? 1 : 0) + (remap.Source.Win ? 1 : 0);
            if (best == null || forApp && !bestForApp || forApp == bestForApp && count > bestCount)
            {
                best = remap;
                bestForApp = forApp;
                bestCount = count;
            }
        }
        return best;
    }

    private static void SendRemapTarget(HotkeyBinding target, bool isDown, bool releaseHeld = false) =>
        InputSender.SendBinding(target, isDown, releaseHeld);

    /// <summary>File name of the foreground window's process, e.g. "Diablo IV.exe" (cached per process).</summary>
    private string? ForegroundExeName()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
            return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        return ExeName(pid);
    }

    /// <summary>File name of a process, e.g. "Diablo IV.exe"; "" when it cannot be read (cached per process).</summary>
    private string ExeName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var cached))
            return cached;

        string name = AppWindows.ExeName(pid);
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

        bool withModifier = _captureHotkey && ModifierTracker.Any;
        if (!_captureAllowsPrimaryMouse && !withModifier && KeyNames.IsPrimaryMouse(vk))
            return false;

        _swallowUp.Add(vk);

        if (vk == KeyNames.VK_ESCAPE && !withModifier)
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
    private void OnDesktopSwitch() => ForgetHeldKeys(
        "stopped because the desktop changed (the lock screen, a UAC prompt or Ctrl+Alt+Del), so the release of its hotkey may never arrive");

    /// <summary>
    /// A window of an app run as administrator (PuppyMacro not) came in front or left it. While it is in front Windows
    /// gives the hooks none of the keys, so the releases of the keys held when it came (Ctrl + Shift + Esc opening Task
    /// Manager) never arrive: when it leaves, what is held is forgotten, as after a desktop switch. Internal for the unit
    /// tests.
    /// </summary>
    internal void OnAdminWindowInFront(bool inFront)
    {
        lock (_sync)
        {
            bool left = _adminWindowInFront && !inFront;
            _adminWindowInFront = inFront;
            if (left)
                ForgetHeldKeys("stopped because an app run as administrator was in front, so the release of its hotkey may never have arrived");
        }
    }

    // The releases of the keys held may never arrive: Hold items stop, remaps and holds are released, modifiers forgotten.
    private void ForgetHeldKeys(string macroStopReason)
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
                    StopMacro(macro.Id, playSound: false, macroStopReason);
            }
            foreach (var target in _activeRemaps.Values.ToList())
                _injector.Post(() => SendRemapTarget(target, isDown: false));
            _activeRemaps.Clear();
            SendRemapKeys(ReleaseHold());
            _heldHotkeys.Clear();
            _swallowUp.Clear();
            ModifierTracker.Reset();
        }
    }

    // Input thread (and the UI thread once at start): a window came in front.
    private void OnForegroundWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        OnAdminWindowInFront(pid != 0 && AppWindows.IsAboveOwnIntegrity(pid));
        if (pid == 0 || pid == (uint)Environment.ProcessId)
            return; // PuppyMacro's own windows: the app before them stays the app in front
        lock (_sync)
        {
            _lastOtherWindow = window;
            OnForegroundApp(ExeName(pid));
        }
    }

    /// <summary>
    /// <paramref name="appExe"/> came in front: loops and macros running for another app stop (one stop sound), and
    /// the UI is told. Internal for the unit tests.
    /// </summary>
    internal void OnForegroundApp(string appExe)
    {
        lock (_sync)
        {
            if (_foregroundApp != null && AppScope.Same(_foregroundApp, appExe))
                return;
            _foregroundApp = appExe;

            var snap = _snapshot;
            bool ForOtherApp(string? itemApp) => itemApp != null && !AppScope.Same(itemApp, appExe);
            var loops = snap.Loops.Where(l => ForOtherApp(l.AppExe) && _running.ContainsKey(l.Id)).ToList();
            var macros = snap.Macros.Where(m => ForOtherApp(m.AppExe) && _runningMacros.ContainsKey(m.Id)).ToList();
            string? sound = loops.FirstOrDefault(l => l.SoundEnabled)?.SoundName
                ?? macros.FirstOrDefault(m => m.SoundEnabled)?.SoundName;
            if (sound != null)
                RequestSound(true, sound, start: false);
            foreach (var loop in loops)
                StopLoop(loop.Id, playSound: false);
            foreach (var macro in macros)
                StopMacro(macro.Id, playSound: false,
                    $"stopped because another app came in front ({(appExe.Length == 0 ? "an app whose name can't be read" : appExe)}), and it plays only in {macro.AppExe}");

            _dispatcher.InvokeAsync(() => ForegroundAppChanged?.Invoke(appExe));
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
            // On the injector, after what it still has: sent at once, a release could come before its own press.
            foreach (var target in _activeRemaps.Values.ToList())
                _injector.Post(() => SendRemapTarget(target, isDown: false));
            _activeRemaps.Clear();
            SendRemapKeys(ReleaseHold());
        }
        _input?.Dispose();
        _injector.Dispose(); // sends everything it has, then ends
    }
}
