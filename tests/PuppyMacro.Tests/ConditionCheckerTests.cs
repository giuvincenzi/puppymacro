using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The conditions of an If on a fake screen (a picture that can change, its graphics card the software renderer): pixels,
/// images near where they were taken or in an area, more images, other sizes, All / Any, the waits and the stop.
/// </summary>
public class ConditionCheckerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ImageStore _images;
    private readonly FakeScreen _screen = new();

    public ConditionCheckerTests() => _images = new ImageStore(_folder.Path);

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    /// <summary>A screen-like picture: random colors, blurred so they have shapes, the same for a seed.</summary>
    private static Mat Picture(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var picture = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(5, 5), 0);
        return picture;
    }

    /// <summary>A piece of a picture added to the store, as the editor's Take image does.</summary>
    private ConditionImage Take(Mat picture, Rect piece)
    {
        using var part = new Mat(picture, piece);
        Cv2.ImEncode(".png", part, out byte[] png);
        return new ConditionImage { Id = _images.Add(png), X = piece.X, Y = piece.Y };
    }

    private static MacroIf If(ConditionWait wait, params MacroCondition[] conditions) =>
        new() { Conditions = new List<MacroCondition>(conditions), Wait = wait, WaitSeconds = 2 };

    private static MacroCondition ImageCondition(SearchArea area, params ConditionImage[] images) => new()
    {
        Kind = ConditionKind.Image, Name = "Button", Images = new List<ConditionImage>(images), Similarity = 90, Area = area,
        AreaX = 0, AreaY = 0, AreaWidth = FakeScreen.Width, AreaHeight = FakeScreen.Height,
    };

    private ConditionResult Check(MacroIf block, RecognitionDevice device = RecognitionDevice.Cpu, WaitHandle? stop = null)
    {
        using var checker = new ConditionChecker(_screen, _images, device, 30);
        using var never = new ManualResetEvent(false);
        return checker.Check(block, stop ?? never);
    }

    [Theory]
    [InlineData("#FF8000", 0, true)]
    [InlineData("#F58000", 5, true)]
    [InlineData("#F08000", 5, false)]
    [InlineData("#FF8000x", 5, false)]
    public void A_pixel_matches_its_color_within_the_tolerance(string color, int tolerance, bool matches)
    {
        Assert.Equal(matches, ConditionChecker.PixelMatches(new Vec3b(0, 128, 255), color, tolerance));
    }

    [Fact]
    public void A_pixel_condition_reads_the_screen()
    {
        using var picture = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        picture.Set(40, 30, new Vec3b(0, 128, 255));
        _screen.Show(picture);

        var red = new MacroCondition { Kind = ConditionKind.Pixel, X = 30, Y = 40, Color = "#FF8000", Tolerance = 5 };
        var blue = new MacroCondition { Kind = ConditionKind.Pixel, X = 30, Y = 40, Color = "#0000FF", Tolerance = 5 };

        Assert.True(Check(If(ConditionWait.Once, red)).Met);
        Assert.False(Check(If(ConditionWait.Once, blue)).Met);
    }

    [Theory]
    [InlineData(RecognitionDevice.Cpu)]
    [InlineData(RecognitionDevice.Gpu)]
    public void An_image_moved_a_little_is_found_around_where_it_was_taken_and_its_center_given(RecognitionDevice device)
    {
        using var first = Picture(FakeScreen.Width, FakeScreen.Height, 1);
        var image = Take(first, new Rect(200, 150, 48, 32));
        // The same piece 20 px to the right and 10 px lower.
        using var moved = Picture(FakeScreen.Width, FakeScreen.Height, 2);
        new Mat(first, new Rect(200, 150, 48, 32)).CopyTo(new Mat(moved, new Rect(220, 160, 48, 32)));
        _screen.Show(moved);
        var condition = ImageCondition(SearchArea.Around, image);

        var result = Check(If(ConditionWait.Once, condition), device);

        Assert.True(result.Met);
        Assert.Equal(new Point(220 + 24, 160 + 16), result.Found[condition.Id]);
    }

    [Fact]
    public void Around_does_not_look_far_from_where_the_image_was_taken()
    {
        using var first = Picture(FakeScreen.Width, FakeScreen.Height, 3);
        var image = Take(first, new Rect(20, 20, 40, 40));
        using var far = Picture(FakeScreen.Width, FakeScreen.Height, 4);
        new Mat(first, new Rect(20, 20, 40, 40)).CopyTo(new Mat(far, new Rect(500, 300, 40, 40)));
        _screen.Show(far);

        Assert.False(Check(If(ConditionWait.Once, ImageCondition(SearchArea.Around, image))).Met);
        Assert.True(Check(If(ConditionWait.Once, ImageCondition(SearchArea.Screen, image))).Met);
    }

    [Fact]
    public void One_of_more_images_is_enough()
    {
        using var source = Picture(FakeScreen.Width, FakeScreen.Height, 5);
        var a = Take(source, new Rect(10, 10, 40, 40));
        var b = Take(source, new Rect(300, 200, 40, 40));
        using var screen = Picture(FakeScreen.Width, FakeScreen.Height, 6);
        new Mat(source, new Rect(300, 200, 40, 40)).CopyTo(new Mat(screen, new Rect(100, 300, 40, 40)));
        _screen.Show(screen);
        var condition = ImageCondition(SearchArea.Screen, a, b);

        var result = Check(If(ConditionWait.Once, condition));

        Assert.True(result.Met);
        Assert.Equal(new Point(120, 320), result.Found[condition.Id]);
    }

    [Fact]
    public void A_little_bigger_is_found_only_with_nearby_sizes()
    {
        using var source = Picture(FakeScreen.Width, FakeScreen.Height, 7);
        var image = Take(source, new Rect(100, 100, 60, 60));
        using var screen = Picture(FakeScreen.Width, FakeScreen.Height, 8);
        using (var bigger = new Mat(source, new Rect(100, 100, 60, 60)).Resize(new Size(66, 66), 0, 0, InterpolationFlags.Linear))
            bigger.CopyTo(new Mat(screen, new Rect(400, 250, 66, 66)));
        _screen.Show(screen);
        var exact = ImageCondition(SearchArea.Screen, image);
        var nearby = ImageCondition(SearchArea.Screen, image);
        nearby.NearbySizes = true;

        Assert.False(Check(If(ConditionWait.Once, exact)).Met);
        var result = Check(If(ConditionWait.Once, nearby));
        Assert.True(result.Met);
        Assert.InRange(result.Found[nearby.Id].X, 430, 436);
        Assert.InRange(result.Found[nearby.Id].Y, 280, 286);
    }

    [Fact]
    public void All_needs_every_condition_and_Any_one()
    {
        using var picture = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(picture);
        var black = new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 1, Color = "#000000" };
        var white = new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 1, Color = "#FFFFFF" };

        var all = If(ConditionWait.Once, black, white);
        var any = If(ConditionWait.Once, black, white);
        any.Match = ConditionMatch.Any;

        Assert.False(Check(all).Met);
        Assert.True(Check(any).Met);
    }

    [Fact]
    public void Up_to_waits_for_the_screen_to_show_it()
    {
        using var empty = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        using var shown = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(255));
        _screen.Show(empty);
        var white = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF" };
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            _screen.Show(shown);
        }, TestContext.Current.CancellationToken);

        var clock = Stopwatch.StartNew();
        var result = Check(If(ConditionWait.UpTo, white));

        Assert.True(result.Met);
        Assert.InRange(clock.ElapsedMilliseconds, 250, 1900);
    }

    [Fact]
    public void Up_to_gives_up_after_its_seconds_and_once_at_once()
    {
        using var empty = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(empty);
        var white = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF" };

        var clock = Stopwatch.StartNew();
        Assert.False(Check(If(ConditionWait.UpTo, white)).Met);
        Assert.InRange(clock.ElapsedMilliseconds, 1900, 4000);

        clock.Restart();
        Assert.False(Check(If(ConditionWait.Once, white)).Met);
        Assert.True(clock.ElapsedMilliseconds < 500);
    }

    [Fact]
    public void Stopping_the_macro_ends_the_wait()
    {
        using var empty = new Mat(FakeScreen.Height, FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(empty);
        var white = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF" };
        using var stop = new ManualResetEvent(false);
        _ = Task.Run(async () =>
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            stop.Set();
        }, TestContext.Current.CancellationToken);

        var result = Check(If(ConditionWait.Forever, white), stop: stop);

        Assert.True(result.Stopped);
        Assert.False(result.Met);
    }

    [Fact]
    public void Every_watch_on_the_screen_ends_with_the_check()
    {
        using var picture = Picture(FakeScreen.Width, FakeScreen.Height, 9);
        _screen.Show(picture);
        var image = Take(picture, new Rect(50, 50, 30, 30));

        Check(If(ConditionWait.Once, ImageCondition(SearchArea.Around, image), new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" }));

        Assert.Equal(0, _screen.Watching);
        Assert.Equal(0, _screen.Listening);
    }

    /// <summary>One monitor of 640 × 400 at the desktop's origin, showing a picture; the software renderer as its card.</summary>
    private sealed class FakeScreen : IScreen, IDisposable
    {
        public const int Width = 640, Height = 400;

        private readonly object _sync = new();
        private readonly List<AutoResetEvent> _listeners = new();
        private Mat? _picture;
        private long _version;

        public GpuMatcher Gpu { get; } = GpuMatcher.Warp();
        public int Watching;
        public int Listening => _listeners.Count;

        public void Show(Mat picture)
        {
            lock (_sync)
            {
                _picture?.Dispose();
                _picture = picture.Clone();
                _version++;
            }
            lock (_listeners)
                foreach (var waiter in _listeners)
                    waiter.Set();
        }

        public IScreenWatch? Start(Rect area)
        {
            if (!new Rect(0, 0, Width, Height).Contains(new Point(area.X + area.Width / 2, area.Y + area.Height / 2)))
                return null;
            Interlocked.Increment(ref Watching);
            return new Watch(this);
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

        public void Dispose()
        {
            _picture?.Dispose();
            Gpu.Dispose();
        }

        private sealed class Watch(FakeScreen screen) : IScreenWatch
        {
            public Rect Bounds => new(0, 0, Width, Height);
            public long Version => Interlocked.Read(ref screen._version);
            public GpuMatcher? Gpu => screen.Gpu;
            public bool Software => true;

            public GpuMatcher.GpuBuffer? Picture(Rect area)
            {
                using var part = Copy(area);
                return part == null ? null : screen.Gpu.Upload(part);
            }

            public Mat? Copy(Rect area)
            {
                lock (screen._sync)
                    return screen._picture == null ? null : new Mat(screen._picture, area.Intersect(Bounds)).Clone();
            }

            public void Dispose() => Interlocked.Decrement(ref screen.Watching);
        }
    }
}
