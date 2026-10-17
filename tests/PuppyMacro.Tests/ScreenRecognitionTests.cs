using System;
using OpenCvSharp;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Image search: the processor (OpenCV) and the graphics card (the software renderer WARP, on every PC and on GitHub) find
/// the same place, coarse-to-fine as the calibration says, and never in a flat area.
/// </summary>
public class ScreenRecognitionTests : IDisposable
{
    private readonly GpuMatcher _gpu = GpuMatcher.Warp();

    public void Dispose() => _gpu.Dispose();

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

    private ImageMatch OnGpu(Mat picture, Mat image, Rect? area = null)
    {
        using var buffer = _gpu.Upload(picture);
        var found = _gpu.Find(buffer, image, Calibration.Of(image, 0.9), 0.9, area);
        _gpu.Forget(image);
        return found;
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(40, 40)]
    [InlineData(184, 52)]
    [InlineData(300, 120)]
    public void Processor_and_graphics_card_find_the_same_place(int width, int height)
    {
        using Mat picture = Picture(900, 500, 1);
        var place = new Rect(513, 287, width, height);
        using Mat image = new Mat(picture, place).Clone();

        var cpu = CpuMatcher.Find(picture, image, Calibration.Of(image, 0.9), 0.9);
        var gpu = OnGpu(picture, image);

        Assert.Equal((place.X, place.Y), (cpu.X, cpu.Y));
        Assert.Equal((place.X, place.Y), (gpu.X, gpu.Y));
        Assert.True(cpu.Score > 0.99 && gpu.Score > 0.99);
    }

    [Fact]
    public void A_search_area_is_where_it_looks()
    {
        using Mat picture = Picture(900, 500, 2);
        using Mat image = new Mat(picture, new Rect(100, 100, 64, 48)).Clone();

        Assert.False(CpuMatcher.Find(picture, image, Calibration.Of(image, 0.9), 0.9, new Rect(400, 0, 500, 500)).Found(0.9));
        Assert.False(OnGpu(picture, image, new Rect(400, 0, 500, 500)).Found(0.9));
        Assert.Equal(100, OnGpu(picture, image, new Rect(50, 50, 200, 200)).X);
    }

    [Fact]
    public void A_flat_area_is_never_found()
    {
        using var picture = new Mat(300, 400, MatType.CV_8UC3, Scalar.All(0));
        using var image = new Mat(30, 30, MatType.CV_8UC3, Scalar.All(0));

        Assert.False(CpuMatcher.FindFull(picture, image).Found(0.5));
        Assert.False(OnGpu(picture, image).Found(0.5));
    }

    [Fact]
    public void A_piece_shown_twice_is_found_at_the_top_then_at_the_left()
    {
        using Mat picture = Picture(600, 400, 3);
        using Mat piece = new Mat(picture, new Rect(10, 10, 40, 40)).Clone();
        piece.CopyTo(new Mat(picture, new Rect(400, 300, 40, 40)));
        piece.CopyTo(new Mat(picture, new Rect(300, 120, 40, 40)));

        var cpu = CpuMatcher.Find(picture, piece, Calibration.Of(piece, 0.9), 0.9);
        var gpu = OnGpu(picture, piece);

        Assert.Equal((10, 10), (cpu.X, cpu.Y));
        Assert.Equal((10, 10), (gpu.X, gpu.Y));
    }

    [Fact]
    public void A_small_image_is_searched_at_full_size_and_a_large_one_shrunk()
    {
        using Mat picture = Picture(900, 500, 4);
        using Mat small = new Mat(picture, new Rect(10, 10, 16, 16)).Clone();
        using Mat large = new Mat(picture, new Rect(10, 10, 200, 120)).Clone();

        Assert.Equal(1, Calibration.Of(small, 0.9).Factor);
        Assert.True(Calibration.Of(large, 0.9).Factor > 1);
    }

    [Fact]
    public void Detail_only_in_color_is_not_searched_in_gray()
    {
        // Red and green of the same brightness, in stripes: gray would be flat.
        using var stripes = new Mat(40, 40, MatType.CV_8UC3, new Scalar(0, 0, 255));
        for (int x = 0; x < 40; x += 8)
            Cv2.Rectangle(stripes, new Rect(x, 0, 4, 40), new Scalar(0, 130, 0), -1);
        using Mat picture = Picture(300, 200, 5);

        Assert.False(Calibration.GrayKeepsDetail(stripes));
        Assert.True(Calibration.GrayKeepsDetail(picture));
    }

    [Fact]
    public void A_picture_comes_back_from_the_graphics_card_the_same()
    {
        using Mat picture = Picture(123, 45, 6);
        using var buffer = _gpu.Upload(picture);
        using Mat back = _gpu.Download(buffer);
        using var difference = new Mat();

        Cv2.Absdiff(picture, back, difference);

        Assert.Equal(0, Cv2.CountNonZero(difference.Reshape(1)));
    }

    [Fact]
    public void A_large_search_runs_in_bands_and_finds_the_same_place()
    {
        // Noise without blur: no detail survives shrinking, so the whole search is at full size, more work than one dispatch may do.
        var bytes = new byte[1200 * 700 * 3];
        new Random(7).NextBytes(bytes);
        using var picture = new Mat(700, 1200, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        var place = new Rect(901, 517, 48, 40);
        using Mat image = new Mat(picture, place).Clone();
        var calibration = Calibration.Of(image, 0.9);
        double work = (1200.0 - 48 + 1) * (700 - 40 + 1) * 48 * 40;

        var gpu = OnGpu(picture, image);
        var cpu = CpuMatcher.Find(picture, image, calibration, 0.9);

        Assert.Equal(1, calibration.Factor);
        Assert.True(work > GpuMatcher.MaxWorkPerDispatch * 10, "the search fits in one dispatch: no bands to test");
        Assert.Equal((place.X, place.Y), (gpu.X, gpu.Y));
        Assert.Equal((place.X, place.Y), (cpu.X, cpu.Y));
    }
}
