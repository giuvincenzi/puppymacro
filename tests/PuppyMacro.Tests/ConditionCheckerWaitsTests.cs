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
/// The waits of an If when one of its monitors gives no image (its capture starting again and again: Windows refuses it
/// for a while, see <see cref="CaptureRetry"/>), the editor's check against the run for a text whose language is not
/// installed, and the waits' timing.
/// </summary>
public class ConditionCheckerWaitsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ImageStore _images;
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public ConditionCheckerWaitsTests() => _images = new ImageStore(_folder.Path);

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    /// <summary>Does <paramref name="change"/> after <paramref name="ms"/> on a thread of its own (ConditionCheckerTests.After).</summary>
    private static void After(int ms, Action change) => new Thread(() =>
    {
        Thread.Sleep(ms);
        change();
    }) { IsBackground = true }.Start();

    /// <summary>
    /// Two monitors side by side: the left one (0, 0, 640 × 400) shows a picture that can change; the right one (640, 0)
    /// is there but its capture has given no image yet and has not given up (it keeps trying, as a capture Windows
    /// refuses for a while does for about 10 seconds, then again after every restart).
    /// </summary>
    private sealed class TwoMonitors : IScreen, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<AutoResetEvent> _listeners = new();
        private Mat _picture = new(400, 640, MatType.CV_8UC3, Scalar.All(0));
        private long _version = 1;

        public static readonly Rect Left = new(0, 0, 640, 400), Right = new(640, 0, 640, 400);

        public void Show(Mat picture)
        {
            lock (_sync)
            {
                _picture.Dispose();
                _picture = picture.Clone();
                _version++;
            }
            lock (_listeners)
                foreach (var waiter in _listeners)
                    waiter.Set();
        }

        public IScreenWatch? Start(Rect area)
        {
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            if (Left.Contains(center))
                return new Working(this);
            if (Right.Contains(center))
                return new NoImageYet();
            return null;
        }

        public void Listen(AutoResetEvent waiter)
        {
            lock (_listeners)
                _listeners.Add(waiter);
        }

        public void Unlisten(AutoResetEvent waiter)
        {
            lock (_listeners)
                _listeners.Remove(waiter);
        }

        public void Dispose() => _picture.Dispose();

        private sealed class Working(TwoMonitors screen) : IScreenWatch
        {
            public Rect Bounds => Left;
            public long Version => Interlocked.Read(ref screen._version);
            public GpuMatcher? Gpu => null;
            public bool Software => true;
            public bool Failed => false;
            public GpuMatcher.GpuBuffer? Picture(Rect area) => null;

            public Mat? Copy(Rect area)
            {
                lock (screen._sync)
                    return new Mat(screen._picture, area.Intersect(Bounds)).Clone();
            }

            public void Dispose()
            {
            }
        }

        private sealed class NoImageYet : IScreenWatch
        {
            public Rect Bounds => Right;
            public long Version => 0;
            public GpuMatcher? Gpu => null;
            public bool Software => true;
            public bool Failed => false;
            public GpuMatcher.GpuBuffer? Picture(Rect area) => null;
            public Mat? Copy(Rect area) => null;

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public void A_monitor_without_an_image_does_not_keep_Any_from_seeing_the_other_monitor()
    {
        // Any: a white pixel on the left monitor, or one on the right monitor. The left monitor turns white after 1.5 s,
        // after the first check (at FirstImageMs, when the right monitor still has no image).
        using var screen = new TwoMonitors();
        var left = new MacroCondition { Kind = ConditionKind.Pixel, Name = "Left", X = 10, Y = 10, Color = "#FFFFFF" };
        var right = new MacroCondition { Kind = ConditionKind.Pixel, Name = "Right", X = 650, Y = 10, Color = "#FFFFFF" };
        var block = new MacroIf { Conditions = { left, right }, Match = ConditionMatch.Any, Wait = ConditionWait.UpTo, WaitSeconds = 4 };
        using var white = new Mat(400, 640, MatType.CV_8UC3, Scalar.All(255));
        After(1500, () => screen.Show(white));

        using var checker = new ConditionChecker(screen, null, RecognitionDevice.Cpu, 30);
        using var never = new ManualResetEvent(false);
        var clock = Stopwatch.StartNew();
        var result = checker.Check(block, never);

        // The run never looks again after its first check: "ready" waits for every monitor's first image, also past
        // FirstImageMs, so the left monitor turning white is never seen and the If gives up after its 4 seconds.
        Assert.True(result.Met, $"not true after {clock.ElapsedMilliseconds} ms");
        Assert.InRange(clock.ElapsedMilliseconds, 1400, 3000);
    }

    [Fact]
    public void A_monitor_without_an_image_does_not_keep_a_Not_on_the_other_monitor_from_becoming_true()
    {
        // Any: the white pixel on the left monitor is gone (Not), or a white pixel on the right monitor. The left one goes
        // black after 1.5 s.
        using var screen = new TwoMonitors();
        using var white = new Mat(400, 640, MatType.CV_8UC3, Scalar.All(255));
        using var black = new Mat(400, 640, MatType.CV_8UC3, Scalar.All(0));
        screen.Show(white);
        var gone = new MacroCondition { Kind = ConditionKind.Pixel, Name = "Gone", X = 10, Y = 10, Color = "#FFFFFF", Not = true };
        var right = new MacroCondition { Kind = ConditionKind.Pixel, Name = "Right", X = 650, Y = 10, Color = "#FFFFFF" };
        var block = new MacroIf { Conditions = { gone, right }, Match = ConditionMatch.Any, Wait = ConditionWait.UpTo, WaitSeconds = 4 };
        After(1500, () => screen.Show(black));

        using var checker = new ConditionChecker(screen, null, RecognitionDevice.Cpu, 30);
        using var never = new ManualResetEvent(false);
        var result = checker.Check(block, never);

        Assert.True(result.Met);
    }

    [Fact]
    public void Up_to_with_every_monitor_working_sees_a_change_after_the_first_second()
    {
        // The same wait with one monitor: the change after FirstImageMs is seen. Passes today; it is the behavior the two
        // tests above expect when another monitor gives no image.
        using var picture = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        using var white = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(255));
        _screen.Show(picture);
        var pixel = new MacroCondition { Kind = ConditionKind.Pixel, X = 10, Y = 10, Color = "#FFFFFF" };
        var block = new MacroIf { Conditions = { pixel }, Wait = ConditionWait.UpTo, WaitSeconds = 4 };
        After(1500, () => _screen.Show(white));

        using var checker = new ConditionChecker(_screen, _images, RecognitionDevice.Cpu, 30);
        using var never = new ManualResetEvent(false);
        var clock = Stopwatch.StartNew();
        var result = checker.Check(block, never);

        Assert.True(result.Met);
        Assert.InRange(clock.ElapsedMilliseconds, 1400, 3000);
    }

    // ================= The editor's check and the run agree on a text =================

    /// <summary>A language tag Windows accepts but no PC has the text recognition of.</summary>
    private const string NotInstalled = "qaa";

    [Fact]
    public void A_text_whose_language_is_not_installed_is_checked_by_the_run_as_the_editor_says()
    {
        // A macro made on a PC with Italian text recognition, played on one without it (shared, or the language removed).
        if (TextReader.Languages.Count == 0 || TextReader.CanRead(NotInstalled))
            return;
        using var screen = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(screen, "Salvage all", new Point(30, 90), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 3);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Text, Name = "Salvage", Text = "Salvage", TextMatch = TextMatch.Contains, Language = NotInstalled,
            AreaX = 0, AreaY = 0, AreaWidth = ConditionCheckerTests.FakeScreen.Width, AreaHeight = ConditionCheckerTests.FakeScreen.Height,
        };

        using var checker = new ConditionChecker(_screen, _images, RecognitionDevice.Cpu, 0);
        var report = checker.Inspect(condition);
        using var never = new ManualResetEvent(false);
        bool run = checker.Check(new MacroIf { Conditions = { condition }, Wait = ConditionWait.Once }, never).Met;

        // The editor (and check_condition) says the language is not installed, so not true; the run reads the text in
        // Windows' own language (TextReader.Engine falls back to it) and finds it: Then is played.
        Assert.True(report.NoLanguage);
        Assert.Equal(report.TrueFor(condition), run);
    }
}
