using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PuppyMacro.Models;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>
/// Waitable timer created with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, so waits
/// are not rounded up to the 10-15.6 ms system tick.
/// </summary>
internal sealed class PreciseTimer : IDisposable
{
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    private readonly TimerWaitHandle _handle;

    private PreciseTimer(IntPtr handle) => _handle = new TimerWaitHandle(handle);

    public static PreciseTimer Create()
    {
        IntPtr handle = NativeMethods.CreateWaitableTimerExW(
            IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);

        // Fallback for systems without high-resolution timers (before Windows 10 1803).
        if (handle == IntPtr.Zero)
            handle = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, 0, NativeMethods.TIMER_ALL_ACCESS);

        if (handle == IntPtr.Zero)
            throw new Win32Exception();

        return new PreciseTimer(handle);
    }

    /// <summary>Waits for <paramref name="milliseconds"/>. Returns false if <paramref name="stop"/> was signaled.</summary>
    public bool Wait(double milliseconds, WaitHandle stop)
    {
        if (milliseconds <= 0)
            return !stop.WaitOne(0);

        long dueTime = -(long)(milliseconds * 10_000); // negative = relative, in 100 ns units
        if (!NativeMethods.SetWaitableTimer(_handle.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            return !stop.WaitOne((int)Math.Ceiling(milliseconds));

        int signaled = WaitHandle.WaitAny(new[] { stop, _handle });
        return signaled != 0;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Runs one action of a loop on its own thread, at the action's interval.</summary>
internal sealed class ActionRunner
{
    private const double MaxHoldMs = 30;
    private const double PasteSettleMs = 40;
    private const double ClipboardRestoreDelayMs = 150;

    private readonly LoopAction _action;
    private readonly ClipboardPaster _paster;
    private readonly ManualResetEvent _stop = new(false);
    private volatile bool _stopRequested;

    public ActionRunner(LoopAction action, ClipboardPaster paster)
    {
        _action = action.Clone();
        _paster = paster;
    }

    /// <summary>Raised on the runner thread when it ends.</summary>
    public event Action<ActionRunner>? Exited;

    public bool StopRequested => _stopRequested;

    /// <summary>The key or mouse button this runner holds down while it runs (a Hold down row), or 0.</summary>
    public int HeldVk => _action.Type == ActionType.Key && _action.HoldDown ? _action.KeyVk : 0;

    public void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "PuppyMacro action",
        };
        thread.Start();
    }

    /// <summary>Asks the thread to stop. Does not block; keys are always released.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _stop.Set();
    }

    private void Run()
    {
        double intervalMs = Math.Clamp(_action.IntervalMs, LoopAction.MinIntervalMs, LoopAction.MaxIntervalMs);
        double holdMs = Math.Min(MaxHoldMs, intervalMs / 2.0);
        long frequency = Stopwatch.Frequency;
        long intervalTicks = (long)(intervalMs * frequency / 1000.0);

        try
        {
            using var timer = PreciseTimer.Create();

            if (_action.Type == ActionType.Key && _action.HoldDown)
            {
                // Held down for the whole time the loop runs; released when it stops.
                InputSender.PressAndHold(_action.KeyVk);
                try
                {
                    _stop.WaitOne();
                }
                finally
                {
                    InputSender.Up(_action.KeyVk);
                }
                return;
            }

            long next = Stopwatch.GetTimestamp();

            while (true)
            {
                bool keepGoing = _action.Type == ActionType.Text
                    ? PasteText(timer, holdMs)
                    : PressKey(timer, holdMs);
                if (!keepGoing)
                    break;

                next += intervalTicks;
                long now = Stopwatch.GetTimestamp();
                if (next < now)
                    next = now; // fell behind: skip instead of sending a burst

                double waitMs = (next - now) * 1000.0 / frequency;
                if (!timer.Wait(waitMs, _stop))
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Action thread failed: {ex}");
        }
        finally
        {
            Exited?.Invoke(this);
        }
    }

    private bool PressKey(PreciseTimer timer, double holdMs)
    {
        // Never click on PuppyMacro's own windows (it could press its own buttons).
        if (KeyNames.IsMouse(_action.KeyVk) && CursorGuard.IsOverOwnWindow())
            return !_stop.WaitOne(0);

        return InputSender.Tap(_action.KeyVk, holdMs, timer, _stop);
    }

    private bool PasteText(PreciseTimer timer, double holdMs)
    {
        if (string.IsNullOrEmpty(_action.Text))
            return !_stop.WaitOne(0);

        if (_action.EnterBefore)
            InputSender.Tap(KeyNames.VK_RETURN, holdMs, timer, _stop);

        var previous = _paster.Replace(_action.Text);
        try
        {
            InputSender.SendPasteShortcut();
            timer.Wait(PasteSettleMs, _stop);

            if (_action.EnterAfter)
                InputSender.Tap(KeyNames.VK_RETURN, holdMs, timer, _stop);

            // Give the target time to read the clipboard before restoring it.
            timer.Wait(ClipboardRestoreDelayMs, _stop);
        }
        finally
        {
            _paster.Restore(previous);
        }
        return !_stop.WaitOne(0);
    }
}

/// <summary>Checks whether the cursor (or a point) is over one of this process's windows.</summary>
internal static class CursorGuard
{
    /// <summary>True if the foreground window belongs to PuppyMacro.</summary>
    public static bool IsOwnWindowForeground()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        return processId == CurrentProcessId;
    }

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    public static bool IsOverOwnWindow() =>
        NativeMethods.GetCursorPos(out var point) && IsOwnWindowAt(point.X, point.Y);

    /// <summary>True if the window at this screen point (physical pixels) belongs to PuppyMacro.</summary>
    public static bool IsOwnWindowAt(int x, int y)
    {
        IntPtr window = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (window == IntPtr.Zero)
            return false;

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        return processId == CurrentProcessId;
    }
}
