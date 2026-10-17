using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Services;

/// <summary>
/// Plays a macro on its own thread: every action after its delay, scaled by the macro speed. An If checks its conditions
/// on the screen (the speed does not change that wait), then plays Then or Else, or ends the macro.
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

    // Where the conditions checked so far found their image or text, for the mouse actions that use it.
    private readonly Dictionary<Guid, OpenCvSharp.Point> _found = new();

    public MacroRunner(MacroDefinition macro, ClipboardPaster paster, ImageStore? images = null,
        RecognitionDevice device = RecognitionDevice.Automatic, IScreen? screen = null)
    {
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

    public void Stop()
    {
        _stopRequested = true;
        _stop.Set();
    }

    private double Speed => Math.Clamp(_macro.Speed, 0.25, 4);

    private void Run()
    {
        try
        {
            using var timer = PreciseTimer.Create();
            _timer = timer;

            int repeats = _macro.Repeat switch
            {
                RepeatMode.Loop => int.MaxValue,
                RepeatMode.Times => Math.Max(1, _macro.RepeatCount),
                _ => 1,
            };

            for (int round = 0; round < repeats && !_stopRequested; round++)
            {
                if (!Play(_macro.Actions))
                    return;
                if (_macro.Actions.Count == 0 && !Wait(100))
                    return; // empty macro in loop mode: do not spin
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Macro thread failed: {ex}");
        }
        finally
        {
            ReleaseHeld();
            _checker?.Dispose();
            Exited?.Invoke(this);
        }
    }

    /// <summary>Plays a list of actions (the macro's, a Then or an Else). False when the macro ends here.</summary>
    private bool Play(List<MacroAction> actions)
    {
        foreach (var action in actions)
        {
            if (!Wait(action.DelayMs) || !Execute(action))
                return false;
        }
        return true;
    }

    /// <summary>Checks an If's conditions, then plays Then or Else, or ends the macro (Stop the macro).</summary>
    private bool If(MacroIf block)
    {
        _checker ??= new ConditionChecker(_screen, _images, _device, _macro.ChecksPerSecond);
        var result = _checker.Check(block, _stop);
        if (result.Stopped)
            return false;
        foreach (var (id, point) in result.Found)
            _found[id] = point;
        if (result.Met)
            return Play(block.Then);
        return block.OnFail == ConditionFail.Else && Play(block.Else);
    }

    /// <summary>
    /// The point of a mouse action: its own, or where its condition found the image or text, moved by the offset. Null
    /// when that condition found nothing (one of Any's that was not true): the action is skipped.
    /// </summary>
    private (int X, int Y)? PointOf(MacroAction a)
    {
        if (a.FoundId is not { } id)
            return (a.X, a.Y);
        return _found.TryGetValue(id, out var p) ? (p.X + a.FoundOffsetX, p.Y + a.FoundOffsetY) : null;
    }

    /// <summary>Waits <paramref name="ms"/> at ×1 speed. Returns false if stopped.</summary>
    private bool Wait(double ms) => _timer!.Wait(ms / Speed, _stop);

    private bool Execute(MacroAction a)
    {
        double hold = Math.Max(1, a.HoldMs / Speed);
        if (a.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo
            && a.FoundId != null)
        {
            if (PointOf(a) is not { } point)
                return true;
            a = a.Clone();
            (a.X, a.Y) = point;
        }
        switch (a.Type)
        {
            case MacroActionType.If:
                return a.If == null || If(a.If);

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
                    return true; // never click PuppyMacro's own windows
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
                    return true;
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
