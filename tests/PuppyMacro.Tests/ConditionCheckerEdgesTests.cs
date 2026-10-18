using System;
using System.Collections.Generic;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The conditions of an If at their edges: the editor's check (Inspect) and the run (Check) agree, a capture that gave up
/// is started again, monitors left of and above the primary one, areas partly off the monitor, detail only in color on
/// both devices, tolerances at their limits.
/// </summary>
public class ConditionCheckerEdgesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ImageStore _images;
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public ConditionCheckerEdgesTests() => _images = new ImageStore(_folder.Path);

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    private static Mat Picture(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var picture = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(5, 5), 0);
        return picture;
    }

    private string Add(Mat picture, Rect piece)
    {
        using var part = new Mat(picture, piece);
        Cv2.ImEncode(".png", part, out byte[] png);
        return _images.Add(png);
    }

    private static MacroIf If(ConditionWait wait, double seconds, params MacroCondition[] conditions) =>
        new() { Conditions = new List<MacroCondition>(conditions), Wait = wait, WaitSeconds = seconds };

    private static ConditionResult Check(IScreen screen, ImageStore? images, MacroIf block, RecognitionDevice device = RecognitionDevice.Cpu)
    {
        using var checker = new ConditionChecker(screen, images, device, 30);
        using var never = new ManualResetEvent(false);
        return checker.Check(block, never);
    }

    // ================= The editor's check and the run agree =================

    [Fact]
    public void Not_with_an_image_on_no_monitor_is_not_true_in_the_editor_either()
    {
        // Two images: one near where it was taken on the screen (not there now), one taken on a monitor that is gone.
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 1);
        _screen.Show(screen);
        using var elsewhere = Picture(200, 200, 2);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Button", Not = true, Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage>
            {
                new() { Id = Add(elsewhere, new Rect(10, 10, 40, 40)), X = 100, Y = 100 },
                new() { Id = Add(elsewhere, new Rect(100, 100, 40, 40)), X = 5000, Y = 5000 },
            },
        };

        bool run = Check(_screen, _images, If(ConditionWait.Once, 0, condition)).Met;
        using var checker = new ConditionChecker(_screen, _images, RecognitionDevice.Cpu, 0);
        bool editor = checker.Inspect(condition).TrueFor(condition);

        // The run does not call it true: a part of the screen it could not see never makes a Not true.
        Assert.False(run);
        // The editor (and the AI tools' check_condition) says the same as the run.
        Assert.Equal(run, editor);
    }

    [Fact]
    public void Not_on_a_monitor_that_gives_no_image_is_not_true_in_the_editor_either()
    {
        // The monitor is there but gives no image (nothing shown on the fake screen).
        var white = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF", Not = true };

        bool run = Check(_screen, _images, If(ConditionWait.Once, 0, white)).Met;
        using var checker = new ConditionChecker(_screen, _images, RecognitionDevice.Cpu, 0);
        bool editor = checker.Inspect(white).TrueFor(white);

        Assert.False(run);
        Assert.Equal(run, editor);
    }

    // ================= A capture that gave up =================

    /// <summary>
    /// One monitor whose capture is shared by every watch on it, as <see cref="ScreenCapture"/> does: a capture in use is kept
    /// as it is (RefreshMonitors skips a monitor in use), one that gave up is made again only when no watch uses it. The
    /// first capture gives no image and gives up a moment after it starts (Windows refused it: the lock screen, a UAC
    /// prompt); the ones made after it work.
    /// </summary>
    private sealed class SharedCaptureScreen : IScreen, IDisposable
    {
        private readonly object _sync = new();
        private readonly Mat _picture = new(400, 640, MatType.CV_8UC3, Scalar.All(255));
        private Capture _current = new(broken: true);

        public int Captures = 1;

        private sealed class Capture(bool broken)
        {
            private readonly System.Diagnostics.Stopwatch _age = System.Diagnostics.Stopwatch.StartNew();

            public bool Broken => broken;
            public bool Failed => broken && _age.ElapsedMilliseconds >= 200;
            public int Users;
        }

        public IScreenWatch? Start(Rect area)
        {
            lock (_sync)
            {
                if (_current.Users == 0 && _current.Failed)
                {
                    _current = new Capture(broken: false);
                    Captures++;
                }
                _current.Users++;
                return new Watch(this, _current);
            }
        }

        public void Listen(AutoResetEvent waiter)
        {
        }

        public void Unlisten(AutoResetEvent waiter)
        {
        }

        public void Dispose() => _picture.Dispose();

        private sealed class Watch(SharedCaptureScreen screen, Capture capture) : IScreenWatch
        {
            private bool _disposed;

            public Rect Bounds => new(0, 0, 640, 400);
            public long Version => capture.Broken ? 0 : 1;
            public GpuMatcher? Gpu => null;
            public bool Software => true;
            public bool Failed => capture.Failed;

            public GpuMatcher.GpuBuffer? Picture(Rect area) => null;

            public Mat? Copy(Rect area)
            {
                lock (screen._sync)
                    return capture.Broken ? null : new Mat(screen._picture, area.Intersect(Bounds)).Clone();
            }

            public void Dispose()
            {
                lock (screen._sync)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    capture.Users--;
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_capture_that_gave_up_is_started_again_while_the_If_waits(int pixels)
    {
        // The screen comes back after the capture gave up: the If waiting for a white pixel gets a new capture after
        // RestartMs and sees it, with one place on the monitor or two (two pixels, a pixel and an image, two images...).
        using var screen = new SharedCaptureScreen();
        var conditions = new MacroCondition[pixels];
        for (int i = 0; i < pixels; i++)
            conditions[i] = new MacroCondition { Kind = ConditionKind.Pixel, X = 5 + i, Y = 5, Color = "#FFFFFF" };

        var result = Check(screen, null, If(ConditionWait.UpTo, 5, conditions));

        // ConditionChecker.Check gives each failed watch a new one in turn: while the other place still holds the monitor,
        // the capture that gave up is in use, so ScreenCapture keeps it and the new watch is on it again, every time.
        Assert.True(screen.Captures > 1, "the capture that gave up was never made again");
        Assert.True(result.Met);
    }

    // ================= Monitors left of and above the primary one =================

    /// <summary>One monitor at any place of the desktop (negative coordinates too), showing a picture.</summary>
    private sealed class MonitorScreen(Rect bounds, Mat picture) : IScreen, IDisposable
    {
        public GpuMatcher Gpu { get; } = GpuMatcher.Warp();
        public Rect Bounds { get; } = bounds;
        public Mat Shown { get; } = picture;

        public IScreenWatch? Start(Rect area)
        {
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            return Bounds.Contains(center) || Bounds.IntersectsWith(area) ? new Watch(this) : null;
        }

        public void Listen(AutoResetEvent waiter)
        {
        }

        public void Unlisten(AutoResetEvent waiter)
        {
        }

        public void Dispose() => Gpu.Dispose();

        private sealed class Watch(MonitorScreen screen) : IScreenWatch
        {
            public Rect Bounds => screen.Bounds;
            public long Version => 1;
            public GpuMatcher? Gpu => screen.Gpu;
            public bool Software => true;
            public bool Failed => false;

            public GpuMatcher.GpuBuffer? Picture(Rect area)
            {
                using var part = Copy(area);
                return part == null ? null : screen.Gpu.Upload(part);
            }

            public Mat? Copy(Rect area)
            {
                Rect bounds = screen.Bounds;
                Rect part = area.Intersect(bounds);
                if (part.Width <= 0 || part.Height <= 0)
                    return null;
                return new Mat(screen.Shown, new Rect(part.X - bounds.X, part.Y - bounds.Y, part.Width, part.Height)).Clone();
            }

            public void Dispose()
            {
            }
        }
    }

    [Theory]
    [InlineData(RecognitionDevice.Cpu)]
    [InlineData(RecognitionDevice.Gpu)]
    public void An_image_on_a_monitor_left_of_and_above_the_primary_one_is_found_in_desktop_coordinates(RecognitionDevice device)
    {
        var bounds = new Rect(-640, -400, 640, 400);
        using var picture = Picture(640, 400, 11);
        using var screen = new MonitorScreen(bounds, picture);
        // Taken at the monitor's bottom-right corner: the Around area runs off the monitor on two sides.
        var piece = new Rect(640 - 48, 400 - 32, 48, 32);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Corner", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = Add(picture, piece), X = bounds.X + piece.X, Y = bounds.Y + piece.Y } },
        };
        var inArea = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Corner 2", Similarity = 90, Area = SearchArea.Drawn,
            AreaX = -700, AreaY = -450, AreaWidth = 700, AreaHeight = 460,
            Images = condition.Images,
        };

        var around = Check(screen, _images, If(ConditionWait.Once, 0, condition), device);
        var area = Check(screen, _images, If(ConditionWait.Once, 0, inArea), device);

        var center = new Point(bounds.X + piece.X + 24, bounds.Y + piece.Y + 16);
        Assert.True(around.Met);
        Assert.Equal(center, around.Found[condition.Id]);
        Assert.True(area.Met);
        Assert.Equal(center, area.Found[inArea.Id]);
    }

    [Fact]
    public void A_pixel_at_negative_coordinates_is_read_on_its_monitor()
    {
        var bounds = new Rect(-1920, -300, 640, 400);
        using var picture = new Mat(400, 640, MatType.CV_8UC3, Scalar.All(0));
        picture.Set(399, 639, new Vec3b(0, 128, 255));
        using var screen = new MonitorScreen(bounds, picture);
        var corner = new MacroCondition { Kind = ConditionKind.Pixel, X = -1920 + 639, Y = -300 + 399, Color = "#FF8000" };
        var outside = new MacroCondition { Kind = ConditionKind.Pixel, X = -1920 + 640, Y = -300 + 399, Color = "#000000" };

        Assert.True(Check(screen, null, If(ConditionWait.Once, 0, corner)).Met);
        Assert.False(Check(screen, null, If(ConditionWait.Once, 0, outside)).Met);
    }

    // ================= Detail only in color, through the If =================

    [Theory]
    [InlineData(RecognitionDevice.Cpu)]
    [InlineData(RecognitionDevice.Gpu)]
    [InlineData(RecognitionDevice.Automatic)]
    public void A_red_and_green_image_of_one_brightness_is_found_with_every_device(RecognitionDevice device)
    {
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 21);
        using Mat cells = ScreenRecognitionEdgesTests.RedGreenCells(40, 4, 22);
        cells.CopyTo(new Mat(screen, new Rect(300, 200, 40, 40)));
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Badge", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = Add(screen, new Rect(300, 200, 40, 40)), X = 300, Y = 200 } },
        };

        var result = Check(_screen, _images, If(ConditionWait.Once, 0, condition), device);

        Assert.True(result.Met, $"not found with {device}");
        Assert.Equal(new Point(320, 220), result.Found[condition.Id]);
    }

    [Theory]
    [InlineData(RecognitionDevice.Cpu)]
    [InlineData(RecognitionDevice.Gpu)]
    public void An_image_of_one_color_taken_in_the_editor_is_never_found_somewhere_else(RecognitionDevice device)
    {
        // A plain button of one color, taken with the editor's Take image (which, unlike the AI tools, does not refuse a
        // flat area), on a busy screen.
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 41);
        Cv2.Rectangle(screen, new Rect(200, 150, 30, 30), new Scalar(40, 90, 200), -1);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Plain button", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = Add(screen, new Rect(200, 150, 30, 30)), X = 200, Y = 150 } },
        };

        var result = Check(_screen, _images, If(ConditionWait.Once, 0, condition), device);

        // Never found (a flat image matches nothing), or found where it is; never a place of the screen that looks nothing like it.
        if (result.Met)
            Assert.Equal(new Point(215, 165), result.Found[condition.Id]);
    }

    // ================= Tolerances at their limits =================

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    [InlineData(5, 12, true)]
    [InlineData(5, 13, false)]
    [InlineData(100, 255, true)]
    [InlineData(-5, 0, true)]
    [InlineData(-5, 1, false)]
    [InlineData(150, 255, true)]
    public void A_pixel_tolerance_is_a_percent_of_255_on_each_color(int tolerance, int difference, bool matches)
    {
        // Each of red, green and blue off by the difference, one at a time.
        var color = new Vec3b(0, 0, 0);
        Assert.Equal(matches, ConditionChecker.PixelMatches(new Vec3b((byte)difference, 0, 0), "#000000", tolerance));
        Assert.Equal(matches, ConditionChecker.PixelMatches(new Vec3b(0, (byte)difference, 0), "#000000", tolerance));
        Assert.Equal(matches, ConditionChecker.PixelMatches(new Vec3b(0, 0, (byte)difference), "#000000", tolerance));
        Assert.True(ConditionChecker.PixelMatches(color, "#000000", tolerance));
    }

    [Theory]
    [InlineData("#ff8000", true)]
    [InlineData("#FF8000", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("FF8000#", false)]
    public void A_color_in_lower_case_matches_and_a_missing_one_never(string? color, bool matches)
    {
        Assert.Equal(matches, ConditionChecker.PixelMatches(new Vec3b(0, 128, 255), color, 0));
    }

    [Fact]
    public void Similarity_exactly_at_the_need_is_found_and_just_below_is_not()
    {
        Assert.True(new ImageMatch(0, 0, 0.895).Found(0.9));
        Assert.False(new ImageMatch(0, 0, 0.8949).Found(0.9));
        Assert.True(new ImageMatch(0, 0, 0.5).Found(0.5));
        Assert.False(new ImageMatch(-1, -1, 1).Found(0.5));
        Assert.Equal(0, ImageMatch.Percent(-2));
    }

    [Fact]
    public void An_image_larger_than_its_area_is_simply_not_found()
    {
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 31);
        _screen.Show(screen);
        string id = Add(screen, new Rect(100, 100, 60, 40));
        foreach (var device in new[] { RecognitionDevice.Cpu, RecognitionDevice.Gpu })
        {
            var condition = new MacroCondition
            {
                Kind = ConditionKind.Image, Name = "Big", Similarity = 90, Area = SearchArea.Drawn,
                AreaX = 100, AreaY = 100, AreaWidth = 59, AreaHeight = 40,
                Images = new List<ConditionImage> { new() { Id = id, X = 100, Y = 100 } },
            };
            Assert.False(Check(_screen, _images, If(ConditionWait.Once, 0, condition), device).Met);
            condition.AreaWidth = 60;
            Assert.True(Check(_screen, _images, If(ConditionWait.Once, 0, condition), device).Met);
        }
    }
}
