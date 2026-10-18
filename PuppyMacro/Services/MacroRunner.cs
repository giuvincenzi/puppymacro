using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Services;

/// <summary>
/// Plays a macro on its own thread: every action after its delay, scaled by the macro speed. An If checks its conditions
/// on the screen (the speed does not change that wait), then plays Then or Else, or ends the macro. A linked row plays its
/// prepared component in a frame of its own: the call's arguments (its bound fields read them when each action starts) and
/// the places its Ifs find, so two calls never mix them up.
/// </summary>
internal sealed class MacroRunner
{
    private const double DoubleClickGapMs = 60;
    private const double ScrollGapMs = 30;
    private const double MoveStepMs = 8;

    private readonly MacroDefinition _macro;
    private readonly ClipboardPaster _paster;
    private readonly ManualResetEvent _stop = new(false);
    private readonly HashSet<int> _held = new();
    private volatile bool _stopRequested;
    private PreciseTimer? _timer;
    private readonly ImageStore? _images;
    private readonly RecognitionDevice _device;
    private readonly IScreen _screen;
    private ConditionChecker? _checker;
    private readonly RunLog? _log;
    private string? _stopReason;
    private bool _ifStopped;
    private readonly Func<IReadOnlyList<OpenCvSharp.Rect>> _monitors;
    private List<IScreenWatch>? _frames;
    private readonly ComponentPrograms _programs;

    /// <summary>One call playing: its arguments, and where the conditions it checked so far found their image or text.</summary>
    private sealed class CallFrame
    {
        public required FrameArguments Arguments { get; init; }
        public Dictionary<Guid, OpenCvSharp.Point> Found { get; } = new();
    }

    // The macro's frame at the bottom, then one per component call inside it.
    private readonly Stack<CallFrame> _calls = new();

    private CallFrame Top => _calls.Peek();

    /// <param name="log">The run's Debug log (Debug on, or a test by an AI app); null writes none.</param>
    /// <param name="monitors">The monitors whose pictures the log takes (the desktop's when null).</param>
    /// <param name="programs">The components its linked rows and Component conditions call (the engine's snapshot).</param>
    /// <param name="arguments">The values of the macro's own frame: a component's Examples when it is tested alone.</param>
    public MacroRunner(MacroDefinition macro, ClipboardPaster paster, ImageStore? images = null,
        RecognitionDevice device = RecognitionDevice.Automatic, IScreen? screen = null, RunLog? log = null,
        Func<IReadOnlyList<OpenCvSharp.Rect>>? monitors = null, ComponentPrograms? programs = null, FrameArguments? arguments = null)
    {
        _programs = programs ?? ComponentPrograms.Empty;
        _calls.Push(new CallFrame { Arguments = arguments ?? FrameArguments.None });
        _log = log;
        _monitors = monitors ?? ScreenCapture.MonitorBounds;
        _macro = macro.Clone();
        _paster = paster;
        _images = images;
        _device = device;
        _screen = screen ?? ScreenCapture.Shared;
    }

    /// <summary>Raised on the runner thread when playback ends (finished, stopped or failed).</summary>
    public event Action<MacroRunner>? Exited;

    public bool StopRequested => _stopRequested;

    public void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "PuppyMacro macro",
        };
        thread.Start();
    }

    /// <param name="why">For the Debug log: why it stops (by the user when null).</param>
    public void Stop(string? why = null)
    {
        _stopReason ??= why ?? "stopped by the user";
        _stopRequested = true;
        _stop.Set();
    }

    /// <summary>Why the run ended, as its Debug log says it; null while it runs.</summary>
    public string? EndReason { get; private set; }

    /// <summary>The run's Debug log, when it has one.</summary>
    public RunLog? DebugLog => _log;

    private double Speed => Math.Clamp(_macro.Speed, 0.25, 4);

    private void Run()
    {
        string end = "finished";
        try
        {
            using var timer = PreciseTimer.Create();
            _timer = timer;
            // With a log, every monitor's capture runs for the whole run: a picture before an action is a copy, not a start.
            if (_log != null)
            {
                _frames = _monitors().Select(b => _screen.Start(b)).OfType<IScreenWatch>().ToList();
                // The first action's picture needs each monitor's first image: up to ConditionChecker.FirstImageMs.
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < ConditionChecker.FirstImageMs && _frames.Any(w => w.Version == 0 && !w.Failed))
                    if (_stop.WaitOne(15))
                        break;
            }

            int repeats = _macro.Repeat switch
            {
                RepeatMode.Loop => int.MaxValue,
                RepeatMode.Times => Math.Max(1, _macro.RepeatCount),
                _ => 1,
            };

            for (int round = 0; round < repeats && !_stopRequested; round++)
            {
                if (repeats > 1)
                    _log?.Event(repeats == int.MaxValue ? $"Round {round + 1}" : $"Round {round + 1} of {repeats}");
                if (!Play(_macro.Actions, ""))
                    return;
                if (_macro.Actions.Count == 0 && !Wait(100))
                    return; // empty macro in loop mode: do not spin
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Macro thread failed: {ex}");
            end = $"failed: {ex.Message}";
        }
        finally
        {
            if (_stopRequested)
                end = _stopReason ?? "stopped by the user";
            else if (_ifStopped)
                end = "an If was not true, and it stops the macro (Stop the macro)";
            EndReason = end;
            _log?.End(end, Frame());
            foreach (var watch in _frames ?? new List<IScreenWatch>())
                watch.Dispose();
            ReleaseHeld();
            _checker?.Dispose();
            Exited?.Invoke(this);
        }
    }

    /// <summary>
    /// Plays a list of actions (the macro's, a Then or an Else). False when the macro ends here. <paramref name="path"/>:
    /// where the list is, for the Debug log ("" for the macro's own, "3 Then " inside the If of action 3).
    /// </summary>
    private bool Play(List<MacroAction> actions, string path)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            var action = ComponentPrograms.Bound(actions[i], Top.Arguments);
            if (!Wait(action.DelayMs))
                return false;
            _current = $"{path}{i + 1}";
            if (!Execute(action))
                return false;
        }
        return true;
    }

    /// <summary>The action playing, as the Debug log numbers it ("2", "2 Then 1").</summary>
    private string _current = "";

    private void Log(string text) => _log?.Line($"#{_current} {text}");

    /// <summary>An event of the log, with each monitor's picture now (just before what it says happens).</summary>
    private void Event(string text) => _log?.Event($"#{_current} {text}", Frame());

    /// <summary>Each monitor as it is now, for the log; nothing without a log or a picture yet.</summary>
    private List<(OpenCvSharp.Rect Bounds, OpenCvSharp.Mat Picture)>? Frame()
    {
        if (_log == null || _frames == null)
            return null;
        var pictures = new List<(OpenCvSharp.Rect, OpenCvSharp.Mat)>();
        foreach (var watch in _frames.Where(w => !w.Failed && w.Version > 0))
        {
            try
            {
                if (watch.Copy(watch.Bounds) is { } picture)
                    pictures.Add((watch.Bounds, picture));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A picture that cannot be taken only leaves the log without it.
            }
        }
        return pictures;
    }

    /// <summary>
    /// A row linked to a Library component: the component as the Library had it when the macro started (prepared in the
    /// engine's snapshot), in a new frame with the row's arguments, numbered under the row in the Debug log
    /// ("4 (Remove one affix) 2"). Skipped when an argument has no value this time (its condition found nothing).
    /// </summary>
    private bool Component(MacroAction row)
    {
        if (row.ComponentId is not Guid id || _programs.Find(id) is not { } callee)
        {
            Log("Component: not in the Library, skipped");
            return true;
        }
        if (_calls.Count > ComponentSignatures.MaxDepth)
        {
            Log($"Component \"{callee.Name}\": skipped, more than {ComponentSignatures.MaxDepth} components inside each other");
            return true;
        }
        if (ComponentPrograms.Evaluate(callee, row.Arguments, Top.Arguments, Top.Found, out string? missing) is not { } arguments)
        {
            Log($"Component \"{callee.Name}\": skipped, {missing} has no value this time (its condition found nothing)");
            return true;
        }
        string me = _current;
        Log($"Component \"{callee.Name}\": {callee.Actions.Count} action(s){ArgumentsText(callee, arguments)}");
        _calls.Push(new CallFrame { Arguments = arguments });
        try
        {
            return Play(callee.Actions, $"{me} ({callee.Name}) ");
        }
        finally
        {
            _calls.Pop();
            _current = me;
        }
    }

    /// <summary>", with Slot = 3986, 1056; Settle = 100 ms" for the Debug log, in English.</summary>
    private static string ArgumentsText(PreparedComponent callee, FrameArguments arguments) =>
        callee.Parameters.Count == 0 ? ""
            : ", with " + Loc.English(() => string.Join("; ", callee.Parameters.Select(p =>
                $"{p.Name} = {ComponentValues.Describe(arguments[p.Name], p.Type, p.Unit)}")));

    /// <summary>Checks an If's conditions, then plays Then or Else, or ends the macro (Stop the macro).</summary>
    private bool If(MacroIf block)
    {
        _checker ??= new ConditionChecker(_screen, _images, _device, _macro.ChecksPerSecond);
        string me = _current;
        Log($"If: {(block.Conditions.Count > 1 ? (block.Match == ConditionMatch.All ? "all of " : "one of ") : "")}{block.Conditions.Count} condition(s), " +
            block.Wait switch
            {
                ConditionWait.Once => "checks once",
                ConditionWait.UpTo => $"waits up to {block.WaitSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s",
                _ => "waits as long as it takes",
            });
        var clock = Stopwatch.StartNew();
        var checking = Checked(block);
        var result = checking == null ? new ConditionResult(false, false, new Dictionary<Guid, OpenCvSharp.Point>())
            : _checker.Check(checking, _stop, describe: _log != null);
        _current = me;
        if (result.Stopped)
        {
            Log($"If: stopped while it was checking, after {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
            return false;
        }
        KeepFound(Top.Found, block, result);
        // The event with the screen as the conditions saw it, then what each one saw.
        Event($"If: {(result.Met ? "true" : "not true")} after {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s -> " +
            (result.Met ? $"Then ({block.Then.Count} actions)"
                : block.OnFail == ConditionFail.Else ? $"Else ({block.Else.Count} actions)" : "the macro stops (Stop the macro)"));
        foreach (string line in result.Details)
            _log?.Line($"  {line}");
        if (result.Met)
            return Play(block.Then, $"{me} Then ");
        if (block.OnFail != ConditionFail.Else)
        {
            _ifStopped = true;
            return false;
        }
        return Play(block.Else, $"{me} Else ");
    }

    /// <summary>
    /// The If as the checker sees it in this frame: each condition with its bound fields filled, a Component condition as
    /// the condition its component returns. A condition that can't be checked this time (a value its call needs is
    /// missing) is never true: with All the If is not true at once (null), with Any the others decide.
    /// </summary>
    private MacroIf? Checked(MacroIf block)
    {
        var conditions = new List<MacroCondition>();
        foreach (var condition in block.Conditions)
        {
            if (_programs.Effective(condition, Top.Arguments, Top.Found) is { } effective)
                conditions.Add(effective);
            else
            {
                Log($"If: the condition \"{condition.Name}\" can't be checked this time (its component or a value is missing)");
                if (block.Match == ConditionMatch.All)
                    return null;
            }
        }
        if (conditions.Count == 0)
            return null;
        if (conditions.SequenceEqual(block.Conditions))
            return block;
        return new MacroIf { Conditions = conditions, Match = block.Match, Wait = block.Wait, WaitSeconds = block.WaitSeconds, OnFail = block.OnFail };
    }

    /// <summary>
    /// Keeps where this If's conditions found their image or text: only what this check found. A condition of this If not
    /// found now (one of Any's) has no place any more, not the one an earlier round found.
    /// </summary>
    internal static void KeepFound(Dictionary<Guid, OpenCvSharp.Point> found, MacroIf block, ConditionResult result)
    {
        foreach (var condition in block.Conditions)
            found.Remove(condition.Id);
        foreach (var (id, point) in result.Found)
            found[id] = point;
    }

    /// <summary>
    /// The point of a mouse action: its own, or where its condition found the image or text, moved by the offset. Null
    /// when that condition found nothing (one of Any's that was not true): the action is skipped.
    /// </summary>
    private (int X, int Y)? PointOf(MacroAction a)
    {
        if (a.FoundId is not { } id)
            return (a.X, a.Y);
        return Top.Found.TryGetValue(id, out var p) ? (p.X + a.FoundOffsetX, p.Y + a.FoundOffsetY) : null;
    }

    /// <summary>Waits <paramref name="ms"/> at ×1 speed. Returns false if stopped.</summary>
    private bool Wait(double ms) => _timer!.Wait(ms / Speed, _stop);

    private bool Execute(MacroAction a)
    {
        double hold = Math.Max(1, a.HoldMs / Speed);
        string where = "";
        if (a.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo
            && a.FoundId != null)
        {
            if (PointOf(a) is not { } point)
            {
                Log($"{ActionName(a)}: skipped, its condition found nothing this time");
                return true;
            }
            a = a.Clone();
            (a.X, a.Y) = point;
            where = " (where its condition found it)";
        }
        if (_log != null && a.Type is not (MacroActionType.If or MacroActionType.Component))
            Event(Describe(a) + where);
        switch (a.Type)
        {
            case MacroActionType.If:
                return a.If == null || If(a.If);

            case MacroActionType.Component:
                return Component(a);

            case MacroActionType.PressKey:
                for (int r = 0; r < Math.Max(1, a.Repeat); r++)
                {
                    if (r > 0 && !Wait(a.RepeatPauseMs))
                        return false;
                    // A combination presses its modifiers too; those a Key down holds stay as they are.
                    bool keepGoing = a.HasModifiers
                        ? InputSender.TapBinding(a.Key, hold, _timer!, _stop, _held)
                        : InputSender.Tap(a.Vk, hold, _timer!, _stop);
                    if (!keepGoing)
                        return false;
                }
                return true;

            case MacroActionType.KeyDown:
                InputSender.Down(a.Vk);
                _held.Add(a.Vk);
                return true;

            case MacroActionType.KeyUp:
                InputSender.Up(a.Vk);
                _held.Remove(a.Vk);
                return true;

            case MacroActionType.Click:
                if (CursorGuard.IsOwnWindowAt(a.X, a.Y))
                {
                    Log("   skipped: a PuppyMacro window is at that point"); // never click PuppyMacro's own windows
                    return true;
                }
                InputSender.MoveTo(a.X, a.Y);
                for (int r = 0; r < Math.Max(1, a.Repeat); r++)
                {
                    if (r > 0 && !Wait(a.RepeatPauseMs))
                        return false;
                    for (int c = 0; c < Math.Max(1, a.ClickCount); c++)
                    {
                        if (c > 0 && !Wait(DoubleClickGapMs))
                            return false;
                        if (!InputSender.Tap(a.Vk, hold, _timer!, _stop))
                            return false;
                    }
                }
                return true;

            case MacroActionType.MouseDown:
                if (CursorGuard.IsOwnWindowAt(a.X, a.Y))
                {
                    Log("   skipped: a PuppyMacro window is at that point");
                    return true;
                }
                InputSender.MoveTo(a.X, a.Y);
                InputSender.Down(a.Vk);
                _held.Add(a.Vk);
                return true;

            case MacroActionType.MouseUp:
                InputSender.MoveTo(a.X, a.Y);
                InputSender.Up(a.Vk);
                _held.Remove(a.Vk);
                return true;

            case MacroActionType.MoveTo:
                return a.Smooth ? Glide(a.X, a.Y, MacroAction.SmoothDurationMs(a.SmoothSpeed)) : MoveNow(a.X, a.Y);

            case MacroActionType.MovePath:
                return FollowPath(a.Path);

            case MacroActionType.Scroll:
                for (int s = 0; s < Math.Max(1, a.ScrollSteps); s++)
                {
                    if (s > 0 && !Wait(ScrollGapMs))
                        return false;
                    InputSender.Wheel(a.ScrollDirection);
                }
                return true;

            case MacroActionType.PasteText:
                return Paste(a, hold);

            default:
                return true;
        }
    }

    private static string ActionName(MacroAction a) => a.Type switch
    {
        MacroActionType.MouseDown => "Mouse button down",
        MacroActionType.MouseUp => "Mouse button up",
        MacroActionType.MoveTo => "Move to",
        _ => a.Type.ToString(),
    };

    /// <summary>An action as the Debug log says it, in English (key names too).</summary>
    private static string Describe(MacroAction a) => Loc.English(() => a.Type switch
    {
        MacroActionType.PressKey => $"Press {KeyNames.Format(a.Key)}{Times(a)}, held {a.HoldMs:0} ms",
        MacroActionType.KeyDown => $"Key down {KeyNames.Get(a.Vk)}",
        MacroActionType.KeyUp => $"Key up {KeyNames.Get(a.Vk)}",
        MacroActionType.Click => $"{(a.ClickCount == 2 ? "Double click" : "Click")} {KeyNames.Get(a.Vk)} at {a.X},{a.Y}{Times(a)}",
        MacroActionType.MouseDown => $"Mouse button down {KeyNames.Get(a.Vk)} at {a.X},{a.Y}",
        MacroActionType.MouseUp => $"Mouse button up {KeyNames.Get(a.Vk)} at {a.X},{a.Y}",
        MacroActionType.MoveTo => $"Move to {a.X},{a.Y}{(a.Smooth ? " smoothly" : "")}",
        MacroActionType.MovePath => $"Move along a recorded path of {a.Path.Count} points",
        MacroActionType.Scroll => $"Scroll {a.ScrollDirection.ToString().ToLowerInvariant()} {a.ScrollSteps} step(s)",
        MacroActionType.PasteText => $"Paste {(a.Text ?? "").Length} characters",
        _ => a.Type.ToString(),
    });

    private static string Times(MacroAction a) => a.Repeat > 1 ? $" {a.Repeat} times" : "";

    private static bool MoveNow(int x, int y)
    {
        InputSender.MoveTo(x, y);
        return true;
    }

    /// <summary>Moves from the current cursor position to (x, y) with an ease-in-out curve.</summary>
    private bool Glide(int x, int y, double durationMs)
    {
        NativeMethods.GetCursorPos(out var start);
        double duration = durationMs / Speed;
        int steps = Math.Max(1, (int)(duration / MoveStepMs));
        for (int s = 1; s <= steps; s++)
        {
            double t = (double)s / steps;
            double eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            InputSender.MoveTo(
                (int)Math.Round(start.X + (x - start.X) * eased),
                (int)Math.Round(start.Y + (y - start.Y) * eased));
            if (s < steps && !_timer!.Wait(duration / steps, _stop))
                return false;
        }
        return true;
    }

    /// <summary>Replays a recorded path, interpolating between the kept points.</summary>
    private bool FollowPath(List<PathPoint> path)
    {
        if (path.Count == 0)
            return true;

        InputSender.MoveTo(path[0].X, path[0].Y);
        long frequency = Stopwatch.Frequency;
        long started = Stopwatch.GetTimestamp();

        for (int k = 1; k < path.Count; k++)
        {
            PathPoint a = path[k - 1], b = path[k];
            double segmentMs = Math.Max(0, (b.T - a.T) / Speed);
            int steps = Math.Max(1, (int)(segmentMs / MoveStepMs));
            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                double targetMs = a.T / Speed + segmentMs * t;
                double nowMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / frequency;
                if (targetMs > nowMs && !_timer!.Wait(targetMs - nowMs, _stop))
                    return false;
                InputSender.MoveTo((int)Math.Round(a.X + (b.X - a.X) * t), (int)Math.Round(a.Y + (b.Y - a.Y) * t));
            }
        }
        return !_stop.WaitOne(0);
    }

    private bool Paste(MacroAction a, double hold)
    {
        if (string.IsNullOrEmpty(a.Text))
            return true;
        if (a.EnterBefore && !InputSender.Tap(KeyNames.VK_RETURN, hold, _timer!, _stop))
            return false;

        var previous = _paster.Replace(a.Text);
        try
        {
            // Once Ctrl+V is sent the paste always completes, as a loop's (ActionRunner.PasteText): a stop skips only
            // Enter after, never the waits before the clipboard is restored.
            InputSender.SendPasteShortcut();
            _timer!.WaitFully(40);
            if (a.EnterAfter && !_stop.WaitOne(0))
                InputSender.Tap(KeyNames.VK_RETURN, hold, _timer!, _stop);
            _timer!.WaitFully(150);
        }
        finally
        {
            _paster.Restore(previous);
        }
        return !_stop.WaitOne(0);
    }

    /// <summary>Keys and buttons pressed by KeyDown/MouseDown and not released yet are released on exit.</summary>
    private void ReleaseHeld()
    {
        foreach (int vk in _held)
            InputSender.Up(vk);
        _held.Clear();
    }
}
