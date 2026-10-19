using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// What the Debug log says an image condition saw (ConditionChecker.Describe) when the image could not be compared at
/// all: it must not read as "compared, and nothing looked like it".
/// </summary>
public class ConditionCheckerDescribeTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ImageStore _images;
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public ConditionCheckerDescribeTests() => _images = new ImageStore(_folder.Path);

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

    private IReadOnlyList<string> Describe(MacroCondition condition)
    {
        using var checker = new ConditionChecker(_screen, _images, RecognitionDevice.Cpu, 30);
        using var never = new ManualResetEvent(false);
        var result = checker.Check(new MacroIf { Conditions = { condition }, Wait = ConditionWait.Once }, never, describe: true);
        Assert.False(result.Met);
        return result.Details;
    }

    [Fact]
    public void An_image_larger_than_its_area_is_not_described_as_0_percent_similar()
    {
        // The image is on the screen, exactly, but the drawn area is one pixel narrower than it: nothing is compared.
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 31);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Big", Similarity = 90, Area = SearchArea.Drawn,
            AreaX = 100, AreaY = 100, AreaWidth = 59, AreaHeight = 40,
            Images = new List<ConditionImage> { new() { Id = Add(screen, new Rect(100, 100, 60, 40)), X = 100, Y = 100 } },
        };

        string line = Describe(condition).First(d => d.StartsWith("Image", StringComparison.Ordinal));

        // Today: "Image "Big": not found, the closest is 0% similar (needs 90%)": the user looks for why the image changed,
        // while the area is simply smaller than the image.
        Assert.DoesNotContain("0% similar", line);
    }

    [Fact]
    public void An_image_of_one_color_is_not_described_as_0_percent_similar()
    {
        // A plain image of one color is never found (Flatness: it has no shape); it is on the screen where it was taken.
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 41);
        Cv2.Rectangle(screen, new Rect(200, 150, 30, 30), new Scalar(40, 90, 200), -1);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Plain", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = Add(screen, new Rect(200, 150, 30, 30)), X = 200, Y = 150 } },
        };

        string line = Describe(condition).First(d => d.StartsWith("Image", StringComparison.Ordinal));

        Assert.DoesNotContain("0% similar", line);
    }

    [Fact]
    public void An_image_not_there_says_how_similar_the_closest_place_is()
    {
        // The case that works: compared everywhere, the closest place's similarity is real.
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 51);
        using var elsewhere = Picture(200, 200, 52);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Gone", Similarity = 90, Area = SearchArea.Screen,
            AreaX = 0, AreaY = 0, AreaWidth = ConditionCheckerTests.FakeScreen.Width, AreaHeight = ConditionCheckerTests.FakeScreen.Height,
            Images = new List<ConditionImage> { new() { Id = Add(elsewhere, new Rect(50, 50, 40, 40)), X = 50, Y = 50 } },
        };

        var details = Describe(condition);

        Assert.Contains(details, d => d.StartsWith("Image \"Gone\": not found, the closest is ", StringComparison.Ordinal) && d.EndsWith("% similar (needs 90%)", StringComparison.Ordinal));
        Assert.Contains("  -> Image \"Gone\" is not true", details);
    }

    /// <summary>
    /// A Text condition with no text to look for (a component's Text parameter given an empty value, a condition written by
    /// hand in the Code view): the monitor is there and shows its picture. Expected: the Debug log says there was nothing to
    /// look for, as Evaluate's own line "nothing to look at (no image PuppyMacro has, or no text)" means to. Observed: Check
    /// treats the blank text as a place it could not see, so the log says "PuppyMacro could not see this part of the screen
    /// (no monitor there, or its capture failed)", sending the user to look for a monitor or capture problem.
    /// </summary>
    [Fact]
    public void A_text_condition_with_no_text_is_not_described_as_a_part_of_the_screen_it_could_not_see()
    {
        if (!TextReader.CanRead(null))
            return;
        using var screen = Picture(ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height, 61);
        _screen.Show(screen);
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Text, Name = "Label", Text = "  ", Area = SearchArea.Screen,
            AreaX = 0, AreaY = 0, AreaWidth = ConditionCheckerTests.FakeScreen.Width, AreaHeight = ConditionCheckerTests.FakeScreen.Height,
        };

        string line = Describe(condition).First(d => d.StartsWith("Text", StringComparison.Ordinal));

        Assert.DoesNotContain("could not see", line);
    }
}
