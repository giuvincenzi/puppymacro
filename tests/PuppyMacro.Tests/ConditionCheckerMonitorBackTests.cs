using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// An If that waits while its monitor is not there when the wait starts (turned off, asleep, a display change in progress)
/// and comes back during the wait.
/// </summary>
public class ConditionCheckerMonitorBackTests : IDisposable
{
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public void Dispose() => _screen.Dispose();

    /// <summary>
    /// The fake screen whose monitor is missing until <see cref="Connect"/>: <see cref="IScreen.Start"/> gives no watch
    /// meanwhile, as <see cref="ScreenCapture.Start"/> does for a monitor that is off, asleep or gone for a moment (and for a
    /// capture it could not start).
    /// </summary>
    private sealed class MonitorComesBack(ConditionCheckerTests.FakeScreen screen) : IScreen
    {
        private volatile bool _connected;
        public int Starts;

        public void Connect() => _connected = true;

        public IScreenWatch? Start(Rect area)
        {
            Interlocked.Increment(ref Starts);
            return _connected ? screen.Start(area) : null;
        }

        public void Listen(AutoResetEvent waiter) => screen.Listen(waiter);

        public void Unlisten(AutoResetEvent waiter) => screen.Unlisten(waiter);
    }

    private static void After(int ms, Action change) => new Thread(() =>
    {
        Thread.Sleep(ms);
        change();
    }) { IsBackground = true }.Start();

    /// <summary>
    /// An If waits up to 6 seconds for a white pixel. When it starts the monitor is not there (Start gives no watch); it
    /// comes back after half a second, showing the white pixel. Expected: the If sees it, as it does for a capture that
    /// gave up (started again every <see cref="ConditionChecker.RestartMs"/>). Observed: the place has no watch for the
    /// whole wait (only a watch that exists and Failed is started again), so the If is not true after 6 seconds although
    /// the pixel was on the screen for 5.5 of them; with "As long as it takes" it would wait forever.
    /// </summary>
    [Fact]
    public void A_monitor_that_comes_back_during_the_wait_is_looked_at_again()
    {
        using var white = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(255));
        _screen.Show(white);
        var screen = new MonitorComesBack(_screen);
        var pixel = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF" };
        var block = new MacroIf { Conditions = new List<MacroCondition> { pixel }, Wait = ConditionWait.UpTo, WaitSeconds = 6 };
        After(500, () =>
        {
            screen.Connect();
            _screen.Show(white);
        });

        using var checker = new ConditionChecker(screen, null, RecognitionDevice.Cpu, 30);
        using var never = new ManualResetEvent(false);
        var clock = Stopwatch.StartNew();
        var result = checker.Check(block, never);

        Assert.True(result.Met, $"not seen after {clock.ElapsedMilliseconds} ms; Start was called {screen.Starts} time(s)");
        Assert.InRange(clock.ElapsedMilliseconds, 400, 4000);
    }
}
