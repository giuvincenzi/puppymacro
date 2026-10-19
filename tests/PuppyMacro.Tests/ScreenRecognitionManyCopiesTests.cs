using System;
using OpenCvSharp;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The image search on a screen that shows the image many times (a grid of identical slots, a list of identical buttons):
/// more copies than the coarse step hands to the full-size step (<see cref="CpuMatcher.MaxCandidates"/>).
/// </summary>
public class ScreenRecognitionManyCopiesTests : IDisposable
{
    private readonly GpuMatcher _gpu = GpuMatcher.Warp();

    public void Dispose() => _gpu.Dispose();

    private static Mat Picture(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var picture = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(0, 0), 2);
        return picture;
    }

    private ImageMatch OnGpu(Mat picture, Mat image, Calibration calibration, double need)
    {
        using var buffer = _gpu.Upload(picture);
        var found = _gpu.Find(buffer, image, calibration, need);
        _gpu.Forget(image);
        return found;
    }

    /// <summary>A grid of copies of <paramref name="icon"/> on a random background; the first copy's top-left corner.</summary>
    private static (Mat Picture, Point First, int Copies) Grid(Mat icon, int step, int start, int seed)
    {
        var picture = Picture(700, 500, seed);
        var first = new Point(-1, -1);
        int copies = 0, w = icon.Width, h = icon.Height, pw = picture.Width, ph = picture.Height;
        for (int y = start; y + h <= ph; y += step)
        {
            for (int x = start; x + w <= pw; x += step)
            {
                icon.CopyTo(new Mat(picture, new Rect(x, y, w, h)));
                if (first.X < 0)
                    first = new Point(x, y);
                copies++;
            }
        }
        return (picture, first, copies);
    }
}
