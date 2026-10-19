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

    /// <summary>
    /// A grid of identical slots (an inventory, a list of identical buttons), more than 32 of them: every copy is 100%
    /// similar, so the place found must be, as <see cref="ImageMatch.Best"/> promises, the highest copy and then the leftmost
    /// one, the same on both devices. Observed: the coarse step keeps only the 32 candidates with the best coarse scores
    /// (<see cref="CpuMatcher.Candidates"/>), and among copies whose coarse scores differ only by rounding (the processor's
    /// Fourier transform) or by how each copy falls on the shrinking grid, which 32 are kept is arbitrary: the processor and
    /// the graphics card each answer some copy in the middle of the grid, and not the same one.
    /// </summary>
    [Theory]
    [InlineData(40, 45, 3)]
    [InlineData(48, 54, 1)]
    [InlineData(54, 60, 2)]
    public void Many_identical_copies_are_found_at_the_highest_then_leftmost_on_both_devices(int size, int step, int start)
    {
        using var icon = Picture(size, size, 600 + size);
        var (picture, first, copies) = Grid(icon, step, start, size + step);
        using (picture)
        {
            var calibration = Calibration.Of(icon, 0.9);

            var cpu = CpuMatcher.Find(picture, icon, calibration, 0.9);
            var gpu = OnGpu(picture, icon, calibration, 0.9);

            Assert.True(copies > CpuMatcher.MaxCandidates && calibration.Factor > 1);
            Assert.True((cpu.X, cpu.Y) == (first.X, first.Y), $"processor: {cpu}, first copy at {first}");
            Assert.True((gpu.X, gpu.Y) == (first.X, first.Y), $"graphics card: {gpu}, first copy at {first}");
        }
    }

    /// <summary>
    /// Many slots that look almost like the image (the same icon with a fine grain of ±<paramref name="grain"/> on every
    /// pixel, alternating like a checkerboard) and, at the end of the grid, the image itself: with Similarity 99% only that
    /// one is similar enough, and it is on the screen. The grain disappears when the coarse step shrinks the picture, so
    /// the look-alikes and the image get the same coarse score, and only 32 of them are compared at full size: when the
    /// image is not among them, the If says it is not there although it is, 100% similar. Observed: the processor answers a
    /// look-alike about 90% similar (not found); the graphics card finds the image here, only because the 32 it keeps
    /// happen to include it. With 31 look-alikes or fewer the processor finds it too.
    /// </summary>
    [Theory]
    [InlineData(48, 54, 5)]
    [InlineData(40, 45, 5)]
    [InlineData(60, 66, 4)]
    public void The_exact_image_is_found_among_many_look_alikes(int size, int step, int grain)
    {
        using var icon = Picture(size, size, 77);
        using var marked = icon.Clone();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var p = marked.At<Vec3b>(y, x);
                int d = (x + y) % 2 == 0 ? grain : -grain;
                marked.Set(y, x, new Vec3b((byte)Math.Clamp(p.Item0 + d, 0, 255), (byte)Math.Clamp(p.Item1 + d, 0, 255), (byte)Math.Clamp(p.Item2 + d, 0, 255)));
            }
        }
        var (picture, _, copies) = Grid(marked, step, 0, 9);
        using (picture)
        {
            // The last slot of the grid shows the icon itself.
            int lastX = (picture.Width - size) / step * step, lastY = (picture.Height - size) / step * step;
            icon.CopyTo(new Mat(picture, new Rect(lastX, lastY, size, size)));
            var calibration = Calibration.Of(icon, 0.99);
            using var lookAlike = new Mat();
            Cv2.MatchTemplate(marked, icon, lookAlike, TemplateMatchModes.CCoeffNormed);
            Assert.True(lookAlike.At<float>(0, 0) < 0.985, $"a look-alike is {lookAlike.At<float>(0, 0)} similar");

            var cpu = CpuMatcher.Find(picture, icon, calibration, 0.99);
            var gpu = OnGpu(picture, icon, calibration, 0.99);

            Assert.True(copies > CpuMatcher.MaxCandidates && calibration.Factor > 1);
            bool cpuFound = cpu.Found(0.99) && (cpu.X, cpu.Y) == (lastX, lastY), gpuFound = gpu.Found(0.99) && (gpu.X, gpu.Y) == (lastX, lastY);
            Assert.True(cpuFound && gpuFound,
                $"the image is at {lastX},{lastY}, a look-alike {lookAlike.At<float>(0, 0):0.000} similar; processor: {cpu}, graphics card: {gpu}");
        }
    }
}
