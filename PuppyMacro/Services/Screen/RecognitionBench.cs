#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// The tuning bench of the screen recognition (development build only, <c>PUPPYMACRO_BENCH</c>): on the real screen, how
/// long an image search takes on the processor and on the graphics card for each image and area size, and the text
/// recognition; or a steady load (a search at a rate, for a minute) to watch what it costs a game's frame rate. It
/// writes its results to a text file and opens it.
/// </summary>
internal static class RecognitionBench
{
    public const string Variable = "PUPPYMACRO_BENCH";

    private static readonly int[] ImageSides = { 24, 48, 96, 192 };
    private static readonly (int W, int H)[] Areas = { (128, 128), (400, 300), (800, 600), (1280, 720), (1920, 1080), (3840, 2160) };
    private const int Runs = 15;

    /// <summary>"1": the timings. "load-gpu" or "load-cpu", optionally ":checks per second" (30 by default): the load.</summary>
    public static void Run(string mode)
    {
        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);
        try
        {
            Rect monitor = ScreenCapture.MonitorBounds().First();
            using var watch = ScreenCapture.Shared.Start(monitor) ?? throw new InvalidOperationException("No monitor to look at.");
            var clock = Stopwatch.StartNew();
            while (watch.Version == 0 && clock.ElapsedMilliseconds < 3000)
                Thread.Sleep(15);
            using Mat screen = watch.Copy(monitor) ?? throw new InvalidOperationException("The screen gives no picture.");
            Line($"PuppyMacro recognition bench, {DateTime.Now:yyyy-MM-dd HH:mm}");
            Line($"Monitor {monitor.Width} x {monitor.Height}, graphics card: {watch.Gpu?.Name ?? "none"}{(watch.Software ? " (software)" : "")}");
            Line($"Processor: {Environment.ProcessorCount} logical cores");
            Line("");
            if (mode.StartsWith("load", StringComparison.OrdinalIgnoreCase))
                Load(mode, watch, screen, monitor, Line);
            else
                Timings(watch, screen, monitor, Line);
        }
        catch (Exception ex)
        {
            Line("Failed: " + ex);
        }
        string file = Path.Combine(Path.GetTempPath(), "PuppyMacro bench.txt");
        File.WriteAllText(file, report.ToString());
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>The image: a piece of the screen with the most detail near its middle, so the search has something real to find.</summary>
    private static Mat Piece(Mat screen, int side)
    {
        int width = screen.Width, height = screen.Height;
        var best = new OpenCvSharp.Rect(width / 2, height / 2, side, side);
        double most = -1;
        for (int y = height / 4; y + side < height * 3 / 4; y += side)
        {
            for (int x = width / 4; x + side < width * 3 / 4; x += side)
            {
                using var part = new Mat(screen, new OpenCvSharp.Rect(x, y, side, side));
                Cv2.MeanStdDev(part, out _, out Scalar spread);
                double detail = spread.Val0 + spread.Val1 + spread.Val2;
                if (detail > most)
                {
                    most = detail;
                    best = new OpenCvSharp.Rect(x, y, side, side);
                }
            }
        }
        return new Mat(screen, best).Clone();
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static void Timings(IScreenWatch watch, Mat screen, Rect monitor, Action<string> line)
    {
        line("Image search, median of " + Runs + " runs, in ms (the copy or conversion of the area included). Columns: CPU, GPU.");
        line("");
        var header = new StringBuilder("area \\ image".PadRight(14));
        foreach (int side in ImageSides)
            header.Append($"{side}x{side}".PadLeft(18));
        line(header.ToString());
        var images = ImageSides.Select(side => Piece(screen, side)).ToList();
        try
        {
            foreach (var (w, h) in Areas.Where(a => a.W <= monitor.Width && a.H <= monitor.Height))
            {
                var area = new Rect(monitor.X + (monitor.Width - w) / 2, monitor.Y + (monitor.Height - h) / 2, w, h);
                var row = new StringBuilder($"{w}x{h}".PadRight(14));
                foreach (var image in images)
                {
                    if (image.Width > w || image.Height > h)
                    {
                        row.Append("-".PadLeft(18));
                        continue;
                    }
                    var calibration = Calibration.Of(image, 0.9);
                    var cpu = new List<double>();
                    var gpu = new List<double>();
                    for (int i = 0; i < Runs + 2; i++)
                    {
                        var t = Stopwatch.StartNew();
                        using (var picture = watch.Copy(area))
                            CpuMatcher.Find(picture!, image, calibration, 0.9);
                        if (i >= 2)
                            cpu.Add(t.Elapsed.TotalMilliseconds);
                        if (watch.Gpu is not { } card)
                            continue;
                        t.Restart();
                        using (var picture = watch.Picture(area))
                            card.Find(picture!, image, calibration, 0.9);
                        if (i >= 2)
                            gpu.Add(t.Elapsed.TotalMilliseconds);
                    }
                    string g = gpu.Count > 0 ? Median(gpu).ToString("0.0", CultureInfo.InvariantCulture) : "-";
                    row.Append($"{Median(cpu).ToString("0.0", CultureInfo.InvariantCulture)} / {g}".PadLeft(18));
                }
                line(row.ToString());
            }
        }
        finally
        {
            foreach (var image in images)
            {
                watch.Gpu?.Forget(image);
                image.Dispose();
            }
        }

        line("");
        line("Text recognition (Windows OCR), median of 5 runs, in ms:");
        foreach (var (w, h) in Areas.Where(a => a.W <= monitor.Width && a.H <= monitor.Height))
        {
            var area = new Rect(monitor.X + (monitor.Width - w) / 2, monitor.Y + (monitor.Height - h) / 2, w, h);
            var times = new List<double>();
            int words = 0;
            for (int i = 0; i < 6; i++)
            {
                var t = Stopwatch.StartNew();
                using var picture = watch.Copy(area);
                var lines = TextReader.Read(picture!, null);
                if (i > 0)
                    times.Add(t.Elapsed.TotalMilliseconds);
                words = lines.Sum(l => l.Count);
            }
            line($"  {w}x{h}: {Median(times).ToString("0", CultureInfo.InvariantCulture)} ms, {words} words read");
        }
        line("");
        line($"The device Automatic chooses today: the graphics card above {ConditionChecker.GpuFromPixels:N0} pixels of area.");
    }

    /// <summary>A whole-screen search of a 96 px image at a rate for 60 seconds, on one device: watch the game's frame rate meanwhile.</summary>
    private static void Load(string mode, IScreenWatch watch, Mat screen, Rect monitor, Action<string> line)
    {
        bool onGpu = mode.Contains("gpu", StringComparison.OrdinalIgnoreCase);
        int rate = mode.Split(':') is { Length: 2 } parts && int.TryParse(parts[1], out int r) ? r : MacroDefinition.DefaultChecksPerSecond;
        using Mat image = Piece(screen, 96);
        var calibration = Calibration.Of(image, 0.9);
        var times = new List<double>();
        var clock = Stopwatch.StartNew();
        long next = 0;
        while (clock.ElapsedMilliseconds < 60_000)
        {
            var t = Stopwatch.StartNew();
            if (onGpu && watch.Gpu is { } card)
            {
                using var picture = watch.Picture(monitor);
                card.Find(picture!, image, calibration, 0.9);
            }
            else
            {
                using var picture = watch.Copy(monitor);
                CpuMatcher.Find(picture!, image, calibration, 0.9);
            }
            times.Add(t.Elapsed.TotalMilliseconds);
            next += rate > 0 ? 1000 / rate : 0;
            long wait = next - clock.ElapsedMilliseconds;
            if (wait > 0)
                Thread.Sleep((int)wait);
        }
        watch.Gpu?.Forget(image);
        line($"Load on the {(onGpu ? "graphics card" : "processor")}: the whole screen, a 96 px image, {rate} times a second for 60 s.");
        line($"{times.Count} searches, median {Median(times).ToString("0.0", CultureInfo.InvariantCulture)} ms, " +
             $"slowest {times.Max().ToString("0.0", CultureInfo.InvariantCulture)} ms.");
    }
}
#endif
