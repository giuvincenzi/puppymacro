using System;
using OpenCvSharp;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The image search at its edges: the processor (OpenCV) and the graphics card (WARP) must give the same answer for
/// detail only in color, nearly flat pictures, a similar piece next to the image, repeated pieces, images at the corners
/// of an odd-sized area; and the HDR frames turned to SDR colors.
/// </summary>
public class ScreenRecognitionEdgesTests : IDisposable
{
    private readonly GpuMatcher _gpu = GpuMatcher.Warp();

    public void Dispose() => _gpu.Dispose();

    /// <summary>Random colors blurred as the existing tests do: shapes of a few pixels.</summary>
    private static Mat Picture(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var picture = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(5, 5), 0);
        return picture;
    }

    /// <summary>Random colors blurred a lot and stretched to [low, high]: large soft shapes that survive shrinking.</summary>
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

    private ImageMatch OnGpu(Mat picture, Mat image, Calibration calibration, double need, Rect? area = null)
    {
        using var buffer = _gpu.Upload(picture);
        var found = _gpu.Find(buffer, image, calibration, need, area);
        _gpu.Forget(image);
        return found;
    }

    /// <summary>
    /// Red (255, 0, 0) and green (0, 130, 0) have the same brightness in gray (76): a piece made only of them has its detail
    /// only in color, as <see cref="Calibration.GrayKeepsDetail"/> knows.
    /// </summary>
    internal static Mat RedGreenCells(int size, int cell, int seed)
    {
        var random = new Random(seed);
        var cells = new Mat(size, size, MatType.CV_8UC3, new Scalar(0, 0, 255));
        for (int y = 0; y < size; y += cell)
            for (int x = 0; x < size; x += cell)
                if (random.Next(2) == 0)
                    Cv2.Rectangle(cells, new Rect(x, y, cell, cell), new Scalar(0, 130, 0), -1);
        return cells;
    }

    [Fact]
    public void Detail_only_in_color_is_found_by_the_processor_as_by_the_graphics_card()
    {
        using Mat picture = Picture(400, 300, 31);
        using Mat cells = RedGreenCells(40, 4, 32);
        cells.CopyTo(new Mat(picture, new Rect(211, 97, 40, 40)));
        var calibration = Calibration.Of(cells, 0.9);

        var gpu = OnGpu(picture, cells, calibration, 0.9);
        var cpu = CpuMatcher.Find(picture, cells, calibration, 0.9);

        Assert.False(calibration.Gray);
        Assert.True(gpu.Found(0.9) && (gpu.X, gpu.Y) == (211, 97), $"graphics card: {gpu}");
        // The processor turns every window to gray to tell flat ones (IgnoreFlat): red and green cells are one gray, so the
        // window where the image is gets -2 and the image is never found.
        Assert.True(cpu.Found(0.9) && (cpu.X, cpu.Y) == (211, 97), $"processor: {cpu}");
    }

    [Fact]
    public void A_nearly_flat_picture_is_flat_for_both_or_for_neither()
    {
        // A dark area with one faint dot (13 of 255): its brightness varies by 0.19 per pixel in gray, 0.56 summed over
        // the three colors. The processor's limit is 0.25 in gray, the graphics card's 0.5 over the three colors.
        using var picture = new Mat(150, 200, MatType.CV_8UC3, Scalar.All(0));
        picture.Set(70, 80, new Vec3b(13, 13, 13));
        using Mat image = new Mat(picture, new Rect(70, 60, 30, 30)).Clone();
        var calibration = Calibration.Of(image, 0.9);

        var gpu = OnGpu(picture, image, calibration, 0.9);
        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9);

        Assert.Equal((gpu.Found(0.9), gpu.X, gpu.Y), (cpu.Found(0.9), cpu.X, cpu.Y));
    }

    [Fact]
    public void A_similar_piece_next_to_the_image_does_not_hide_it_on_the_graphics_card()
    {
        // The image, and next to it the same image with a fine checkerboard added: shrunk by the calibration's factor the
        // checkerboard averages out, so the copy looks exactly like the image in the coarse step, but at full size it is
        // far from it. The image itself is placed half a cell off the coarse grid, so its coarse score is a little lower.
        using Mat source = Smooth(200, 200, 41, 6, 60, 195);
        using Mat image = new Mat(source, new Rect(50, 50, 48, 48)).Clone();
        var calibration = Calibration.Of(image, 0.9);
        Assert.Equal(4, calibration.Factor);
        using Mat decoy = image.Clone();
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                int d = (x + y) % 2 == 0 ? 50 : -50;
                var c = decoy.At<Vec3b>(y, x);
                decoy.Set(y, x, new Vec3b((byte)(c.Item0 + d), (byte)(c.Item1 + d), (byte)(c.Item2 + d)));
            }
        }
        using Mat picture = Smooth(256, 192, 42, 6, 60, 195);
        image.CopyTo(new Mat(picture, new Rect(2, 2, 48, 48)));
        decoy.CopyTo(new Mat(picture, new Rect(52, 0, 48, 48)));

        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9);
        var gpu = OnGpu(picture, image, calibration, 0.9);

        Assert.True(cpu.Found(0.9) && (cpu.X, cpu.Y) == (2, 2), $"processor: {cpu}");
        // The graphics card keeps one candidate per 16 x 16 block of coarse places: the copy, alone, is refined.
        Assert.True(gpu.Found(0.9) && (gpu.X, gpu.Y) == (2, 2), $"graphics card: {gpu}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_small_piece_shown_three_times_is_found_at_the_top_then_at_the_left(int seed)
    {
        // A piece too small to shrink (factor 1): the processor searches it once at full size.
        using Mat picture = Picture(500, 400, 100 + seed);
        var random = new Random(seed);
        int x0 = random.Next(0, 100), y0 = random.Next(0, 60);
        using Mat piece = new Mat(picture, new Rect(x0, y0, 14, 14)).Clone();
        piece.CopyTo(new Mat(picture, new Rect(random.Next(200, 480), y0, 14, 14)));
        piece.CopyTo(new Mat(picture, new Rect(random.Next(0, 480), random.Next(150, 380), 14, 14)));
        var calibration = Calibration.Of(piece, 0.9);

        var cpu = CpuMatcher.Find(picture, piece, calibration, 0.9);
        var gpu = OnGpu(picture, piece, calibration, 0.9);

        Assert.Equal(1, calibration.Factor);
        Assert.Equal((x0, y0), (gpu.X, gpu.Y));
        Assert.Equal((x0, y0), (cpu.X, cpu.Y));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void An_image_at_a_corner_of_an_odd_sized_area_is_found_by_both(int right, int bottom)
    {
        using Mat picture = Smooth(500, 300, 51, 3, 20, 235);
        var area = new Rect(13, 7, 301, 211);
        int x = right == 1 ? area.Right - 50 : area.X, y = bottom == 1 ? area.Bottom - 30 : area.Y;
        using Mat image = new Mat(picture, new Rect(x, y, 50, 30)).Clone();
        var calibration = Calibration.Of(image, 0.9);

        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9, area);
        var gpu = OnGpu(picture, image, calibration, 0.9, area);

        Assert.True(cpu.Found(0.9) && (cpu.X, cpu.Y) == (x, y), $"processor: {cpu}");
        Assert.True(gpu.Found(0.9) && (gpu.X, gpu.Y) == (x, y), $"graphics card: {gpu}");
    }

    [Theory]
    [InlineData(64, 48)]
    [InlineData(65, 49)]
    [InlineData(64, 47)]
    public void An_area_as_large_as_the_image_or_smaller(int width, int height)
    {
        using Mat picture = Smooth(300, 200, 61, 3, 20, 235);
        using Mat image = new Mat(picture, new Rect(101, 33, 64, 48)).Clone();
        var calibration = Calibration.Of(image, 0.9);
        var area = new Rect(101, 33, width, height);
        bool fits = width >= 64 && height >= 48;

        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9, area);
        var gpu = OnGpu(picture, image, calibration, 0.9, area);

        Assert.Equal(fits, cpu.Found(0.9));
        Assert.Equal(fits, gpu.Found(0.9));
        if (fits)
        {
            Assert.Equal((101, 33), (cpu.X, cpu.Y));
            Assert.Equal((101, 33), (gpu.X, gpu.Y));
        }
    }

    /// <summary>sRGB of a linear value, as Windows shows SDR content.</summary>
    private static int Srgb(double linear)
    {
        double l = Math.Clamp(linear, 0, 1);
        double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (int)Math.Round(s * 255);
    }

    [Theory]
    [InlineData(80f)]
    [InlineData(160f)]
    [InlineData(40f)]
    [InlineData(240f)]
    public void An_HDR_frame_is_turned_to_SDR_colors_with_the_SDR_white_level(float white)
    {
        // The frame's values are read as linear light (scRGB: 1 = 80 nits), divided by the SDR white level, then encoded in
        // sRGB: 0 stays black, the SDR white is 255, brighter than it is 255 too.
        using var frame = new Mat(1, 6, MatType.CV_8UC3);
        byte[] values = { 0, 1, 64, 128, 200, 255 };
        for (int i = 0; i < values.Length; i++)
            frame.Set(0, i, new Vec3b(values[i], values[i], values[i]));
        var (texture, view) = _gpu.Frame(frame);
        try
        {
            using var converted = _gpu.Convert(view, scRgb: true, white, new Rect(0, 0, 6, 1), 1, 6, 1);
            using Mat back = _gpu.Download(converted);
            for (int i = 0; i < values.Length; i++)
            {
                int expected = Srgb(values[i] / 255.0 * 80 / white);
                Assert.InRange(back.At<Vec3b>(0, i).Item0, expected - 1, expected + 1);
            }
        }
        finally
        {
            GpuMatcher.Release(view);
            GpuMatcher.Release(texture);
        }
    }

    [Fact]
    public void A_single_color_image_is_never_found_by_either()
    {
        // A flat image matches nothing (the doc of CpuMatcher: a flat piece never counts as found). OpenCV gives every place
        // the score 1 for a template of one color, and IgnoreFlat only takes away the flat windows of the screen: the first
        // place that is not flat is "found" at 100%.
        using Mat picture = Picture(300, 200, 71);
        using var flat = new Mat(20, 20, MatType.CV_8UC3, new Scalar(10, 200, 30));
        flat.CopyTo(new Mat(picture, new Rect(50, 50, 20, 20)));
        var calibration = Calibration.Of(flat, 0.5);

        var cpu = CpuMatcher.Find(picture, flat, calibration, 0.5);

        Assert.False(OnGpu(picture, flat, calibration, 0.5).Found(0.5));
        Assert.False(cpu.Found(0.5), $"processor: {cpu}");
    }
}
