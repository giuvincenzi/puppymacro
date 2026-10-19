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
}
