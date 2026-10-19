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
/// The image search on light pictures with little contrast (a light theme: pale shapes on white): the graphics card
/// (WARP, the same 32-bit floats as a real card) must give the scores the processor gives, so an image found on one
/// device is found on the other, and a score is never above 100%.
/// </summary>
public class ScreenRecognitionPrecisionTests : IDisposable
{
    private readonly GpuMatcher _gpu = GpuMatcher.Warp();
    private readonly TempFolder _folder = new();
    private readonly ImageStore _images;
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public ScreenRecognitionPrecisionTests() => _images = new ImageStore(_folder.Path);

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
        _gpu.Dispose();
    }

    /// <summary>Random colors blurred a lot and stretched to [low, high]: soft shapes, as ScreenRecognitionEdgesTests makes them.</summary>
    private static Mat Smooth(int width, int height, int seed, double sigma, int low, int high)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        using var noise = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, noise.Data, bytes.Length);
        using var blurred = new Mat();
        Cv2.GaussianBlur(noise, blurred, new Size(0, 0), sigma);
        var stretched = new Mat();
        Cv2.Normalize(blurred, stretched, low, high, NormTypes.MinMax);
        return stretched;
    }

    [Theory]
    [InlineData(245, 40)]
    [InlineData(245, 100)]
    [InlineData(245, 200)]
    [InlineData(235, 100)]
    [InlineData(200, 200)]
    public void A_light_image_with_little_contrast_scores_the_same_on_both_devices(int low, int size)
    {
        // The exact piece of the picture: the processor scores it 100%. The graphics card sums the squares of the pixels in
        // 32-bit floats and subtracts the square of their sum (Match: sii - si * si / N): with bright pixels both are huge
        // and close, so the variance that is left is mostly rounding.
        using Mat picture = Smooth(400, 300, 3, 3, low, 255);
        using Mat image = new Mat(picture, new Rect(77, 51, size, size)).Clone();
        var calibration = Calibration.Of(image, 0.9);

        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9);
        using var buffer = _gpu.Upload(picture);
        var gpu = _gpu.Find(buffer, image, calibration, 0.9);
        _gpu.Forget(image);

        Assert.True(cpu.Found(1.0) && (cpu.X, cpu.Y) == (77, 51), $"processor: {cpu}");
        Assert.True((gpu.X, gpu.Y) == (77, 51), $"graphics card: {gpu}, processor: {cpu}");
        Assert.InRange(gpu.Score, cpu.Score - 0.005, cpu.Score + 0.005);
    }

    [Theory]
    [InlineData(245, 120, 90)]
    [InlineData(235, 100, 100)]
    [InlineData(200, 200, 100)]
    public void A_light_button_found_on_the_processor_is_found_on_the_graphics_card(int low, int size, int similarity)
    {
        // Through the If, as a run checks it: RecognitionDevice.Gpu (or Automatic on a whole screen larger than 1,000,000
        // pixels with a real graphics card) gives another answer than RecognitionDevice.Cpu for the same screen.
        using Mat screen = Smooth(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 5, 3, low, 255);
        _screen.Show(screen);
        var piece = new Rect(150, 90, size, size);
        string id;
        using (var part = new Mat(screen, piece))
        {
            Cv2.ImEncode(".png", part, out byte[] png);
            id = _images.Add(png);
        }
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Light button", Similarity = similarity, Area = SearchArea.Screen,
            AreaX = 0, AreaY = 0, AreaWidth = ConditionCheckerTests.FakeScreen.Width, AreaHeight = ConditionCheckerTests.FakeScreen.Height,
            Images = new List<ConditionImage> { new() { Id = id, X = piece.X, Y = piece.Y } },
        };
        var block = new MacroIf { Conditions = { condition }, Wait = ConditionWait.Once };

        var results = new Dictionary<RecognitionDevice, ConditionResult>();
        foreach (var device in new[] { RecognitionDevice.Cpu, RecognitionDevice.Gpu })
        {
            using var checker = new ConditionChecker(_screen, _images, device, 30);
            using var never = new ManualResetEvent(false);
            results[device] = checker.Check(block, never, describe: true);
        }

        var center = new Point(piece.X + size / 2, piece.Y + size / 2);
        Assert.True(results[RecognitionDevice.Cpu].Met, string.Join("\n", results[RecognitionDevice.Cpu].Details));
        Assert.Equal(center, results[RecognitionDevice.Cpu].Found[condition.Id]);
        Assert.True(results[RecognitionDevice.Gpu].Met, string.Join("\n", results[RecognitionDevice.Gpu].Details));
        Assert.Equal(center, results[RecognitionDevice.Gpu].Found[condition.Id]);
    }

    [Fact]
    public void A_score_on_the_graphics_card_is_never_above_100_percent()
    {
        // The editor and the Debug log show the score in whole percent: 106% similar is not a similarity.
        using Mat picture = Smooth(400, 300, 3, 3, 235, 255);
        using Mat image = new Mat(picture, new Rect(77, 51, 40, 40)).Clone();
        var calibration = Calibration.Of(image, 0.9);
        using var buffer = _gpu.Upload(picture);

        var gpu = _gpu.Find(buffer, image, calibration, 0.9);
        _gpu.Forget(image);

        Assert.InRange(ImageMatch.Percent(gpu.Score), 0, 100);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(0, 100)]
    [InlineData(0, 200)]
    [InlineData(60, 120)]
    public void A_picture_with_full_contrast_scores_the_same_on_both_devices(int low, int size)
    {
        // The case that works today, kept: dark and light pixels, the variance is large next to the rounding.
        using Mat picture = Smooth(400, 300, 3, 3, low, 255);
        using Mat image = new Mat(picture, new Rect(77, 51, size, size)).Clone();
        var calibration = Calibration.Of(image, 0.9);

        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9);
        using var buffer = _gpu.Upload(picture);
        var gpu = _gpu.Find(buffer, image, calibration, 0.9);
        _gpu.Forget(image);

        Assert.Equal((77, 51), (cpu.X, cpu.Y));
        Assert.Equal((77, 51), (gpu.X, gpu.Y));
        Assert.Equal(ImageMatch.Percent(cpu.Score), ImageMatch.Percent(gpu.Score));
    }
}
