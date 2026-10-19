using System;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using Windows.Media.Ocr;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Windows' text recognition on pictures it does not read as they are: larger than it reads (shrunk, the places grown
/// back), tilted text (its places turned back), and an area so thin that shrinking it leaves no row.
/// </summary>
public class TextReaderScalingTests
{
    private const string Line = "Open the CURRENCY tab";
    private const double FontScale = 1.4;
    private const int Thickness = 3;

    /// <summary>Where "CURRENCY" is drawn when <see cref="Line"/> starts at (x, baseline).</summary>
    private static Point2d CurrencyCenter(int x, int baseline)
    {
        int start = x + Cv2.GetTextSize("Open the ", HersheyFonts.HersheySimplex, FontScale, Thickness, out _).Width;
        var word = Cv2.GetTextSize("CURRENCY", HersheyFonts.HersheySimplex, FontScale, Thickness, out _);
        return new Point2d(start + word.Width / 2.0, baseline - word.Height / 2.0);
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Theory]
    [InlineData(3000, 300)]
    [InlineData(11400, 1200)]
    [InlineData(16000, 1600)]
    public void A_picture_larger_than_Windows_reads_gives_places_on_the_picture_itself(int width, int height)
    {
        // Wider than Windows reads (OcrEngine.MaxImageDimension): shrunk to read, the places grown back to the picture's
        // size. The first case is not shrunk. (Windows reads nothing on a picture much wider than tall, 9000 × 300: so
        // the large ones are taller.)
        if (TextReader.Languages.Count == 0)
            return;
        using var picture = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));
        int x = width - 1500, baseline = 200;
        // Drawn twice as large, so it is still readable once shrunk.
        Cv2.PutText(picture, Line, new Point(x, baseline), HersheyFonts.HersheySimplex, FontScale * 2, Scalar.All(0), Thickness * 2);
        int start = x + Cv2.GetTextSize("Open the ", HersheyFonts.HersheySimplex, FontScale * 2, Thickness * 2, out _).Width;
        var word = Cv2.GetTextSize("CURRENCY", HersheyFonts.HersheySimplex, FontScale * 2, Thickness * 2, out _);

        var lines = TextReader.Read(picture, null);
        var found = TextReader.Find(lines, "currency", TextMatch.Exact, false);

        Assert.True(found != null, $"max {OcrEngine.MaxImageDimension}; read: " + string.Join(" | ", System.Linq.Enumerable.Select(lines, l => string.Join(" ", System.Linq.Enumerable.Select(l, w => w.Text + w.Bounds)))));
        var center = Center(found.Value);
        Assert.InRange(center.X, start + word.Width / 2 - 25, start + word.Width / 2 + 25);
        Assert.InRange(center.Y, baseline - word.Height / 2 - 20, baseline - word.Height / 2 + 20);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(12)]
    [InlineData(-12)]
    public void Tilted_text_is_found_where_it_is_on_the_picture(double degrees)
    {
        if (TextReader.Languages.Count == 0)
            return;
        using var straight = new Mat(500, 900, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(straight, Line, new Point(150, 260), HersheyFonts.HersheySimplex, FontScale, Scalar.All(0), Thickness);
        // The whole picture turned around its center (OpenCV: positive is counterclockwise on the screen).
        using var turn = Cv2.GetRotationMatrix2D(new Point2f(450, 250), degrees, 1);
        using var tilted = new Mat();
        Cv2.WarpAffine(straight, tilted, turn, straight.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
        var c = CurrencyCenter(150, 260);
        double ex = turn.At<double>(0, 0) * c.X + turn.At<double>(0, 1) * c.Y + turn.At<double>(0, 2);
        double ey = turn.At<double>(1, 0) * c.X + turn.At<double>(1, 1) * c.Y + turn.At<double>(1, 2);

        var found = TextReader.Find(TextReader.Read(tilted, null), "currency", TextMatch.Exact, false);

        Assert.NotNull(found);
        var center = Center(found.Value);
        Assert.True(Math.Abs(center.X - ex) <= 20 && Math.Abs(center.Y - ey) <= 20,
            $"found at {center.X},{center.Y}, drawn at {ex:0},{ey:0}");
    }

    /// <summary>
    /// A Text condition on a monitor wider than Windows reads (10000 px: NVIDIA Surround or AMD Eyefinity joining three 4K
    /// screens into one display of 11520 × 2160), whose area is mostly off the top or the bottom of it, so the part on the
    /// monitor, what is read, is 1 px tall. The picture is shrunk to <c>(int)(1 × 10000 / 11520)</c> = 0 rows, and
    /// OpenCV's Resize throws ("inv_scale_x &gt; 0"). Expected: nothing read (no line), as for any picture without text.
    /// Observed: an exception out of <see cref="TextReader.Read"/>, which ends the running macro with an error.
    /// </summary>
    [Fact]
    public void A_very_wide_picture_one_pixel_tall_reads_nothing_instead_of_failing()
    {
        if (TextReader.Languages.Count == 0)
            return;
        const int width = 11520;
        Assert.True(width > OcrEngine.MaxImageDimension);
        using var picture = new Mat(1, width, MatType.CV_8UC3, Scalar.All(255));

        var lines = TextReader.Read(picture, null);

        Assert.Empty(lines);
    }
}
