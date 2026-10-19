using System;
using System.Collections.Generic;
using OpenCvSharp;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The image search on generated cases: the processor (OpenCV) and the graphics card (WARP) give the same answer on random
/// pictures of many sizes and textures, with images taken from them and changed a little (brightness, contrast, noise) or
/// taken from another picture (not there), searched in the whole picture or in an area at any offset.
/// </summary>
public class ScreenRecognitionRandomTests : IDisposable
{
    private readonly GpuMatcher _gpu = GpuMatcher.Warp();

    public void Dispose() => _gpu.Dispose();

    private static Mat Picture(int width, int height, int seed, double sigma)
    {
        var bytes = new byte[width * height * 3];
        new Random(seed).NextBytes(bytes);
        var picture = new Mat(height, width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(0, 0), sigma);
        return picture;
    }

    private ImageMatch OnGpu(Mat picture, Mat image, Calibration calibration, double need, Rect? area = null)
    {
        using var buffer = _gpu.Upload(picture);
        var found = _gpu.Find(buffer, image, calibration, need, area);
        _gpu.Forget(image);
        return found;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Both_devices_agree_on_images_taken_from_the_picture_and_changed_a_little(int block)
    {
        var failures = new List<string>();
        for (int seed = block * 50; seed < block * 50 + 50; seed++)
        {
            var r = new Random(seed);
            int width = r.Next(120, 500), height = r.Next(120, 400);
            using var picture = Picture(width, height, seed, r.Next(1, 6));
            int iw = r.Next(8, Math.Min(140, width - 1)), ih = r.Next(8, Math.Min(140, height - 1));
            int x = r.Next(0, width - iw + 1), y = r.Next(0, height - ih + 1);
            using var image = new Mat(picture, new Rect(x, y, iw, ih)).Clone();
            int kind = r.Next(4);
            if (kind == 1)
            {
                image.ConvertTo(image, -1, 1, r.Next(-30, 30));
            }
            else if (kind == 2)
            {
                image.ConvertTo(image, -1, 0.8 + r.NextDouble() * 0.4, 0);
            }
            else if (kind == 3)
            {
                using var noise = new Mat(image.Size(), MatType.CV_8UC3);
                Cv2.Randu(noise, Scalar.All(0), Scalar.All(20));
                Cv2.Add(image, noise, image);
            }
            double need = r.Next(50, 101) / 100.0;
            var calibration = Calibration.Of(image, need);

            var cpu = CpuMatcher.Find(picture, image, calibration, need);
            var gpu = OnGpu(picture, image, calibration, need);

            bool same = cpu.Found(need) == gpu.Found(need) && (!cpu.Found(need) || (cpu.X == gpu.X && cpu.Y == gpu.Y))
                && Math.Abs(Math.Max(cpu.Score, 0) - Math.Max(gpu.Score, 0)) < 0.01;
            if (!same)
                failures.Add($"seed {seed}: processor {cpu}, graphics card {gpu}");
        }
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Both_devices_agree_in_areas_at_any_offset_and_on_images_not_there(int block)
    {
        var failures = new List<string>();
        for (int seed = 1000 + block * 50; seed < 1000 + block * 50 + 50; seed++)
        {
            var r = new Random(seed);
            int width = r.Next(150, 500), height = r.Next(150, 400);
            using var picture = Picture(width, height, seed, r.Next(1, 6));
            using var other = Picture(width, height, seed + 7, r.Next(1, 6));
            int iw = r.Next(12, 100), ih = r.Next(12, 100);
            int x = r.Next(0, width - iw + 1), y = r.Next(0, height - ih + 1);
            bool absent = r.Next(2) == 0;
            using var image = new Mat(absent ? other : picture, new Rect(x, y, iw, ih)).Clone();
            double need = r.Next(50, 101) / 100.0;
            var calibration = Calibration.Of(image, need);
            int ax = r.Next(0, 40), ay = r.Next(0, 40);
            var area = new Rect(ax, ay, width - ax - r.Next(0, 40), height - ay - r.Next(0, 40));

            var cpu = CpuMatcher.Find(picture, image, calibration, need, area);
            var gpu = OnGpu(picture, image, calibration, need, area);

            bool same = cpu.Found(need) == gpu.Found(need) && (!cpu.Found(need) || (cpu.X == gpu.X && cpu.Y == gpu.Y));
            if (!same)
                failures.Add($"seed {seed}: processor {cpu}, graphics card {gpu}");
        }
        Assert.Empty(failures);
    }
}
