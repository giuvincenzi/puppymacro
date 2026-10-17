using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// A still picture of every monitor, as the macros' conditions see the screen (the same capture and colors): the pickers
/// show it, and a pixel's color or an image is taken from it, so what is picked is what a condition looks for.
/// </summary>
internal sealed class ScreenSnapshot : IDisposable
{
    /// <summary>How long a monitor may take to give its first picture.</summary>
    private const int WaitMs = 2000;

    private ScreenSnapshot(List<(Rect Bounds, Mat Picture)> monitors) => Monitors = monitors;

    /// <summary>Each monitor's bounds (desktop coordinates, physical pixels) and its picture (BGR).</summary>
    public List<(Rect Bounds, Mat Picture)> Monitors { get; }

    /// <summary>Takes the picture of every monitor; a monitor that gives none in time is left out.</summary>
    public static ScreenSnapshot Take() => Take(ScreenCapture.Shared);

    public static ScreenSnapshot Take(IScreen screen)
    {
        var monitors = new List<(Rect, Mat)>();
        var watches = ScreenCapture.MonitorBounds().Select(b => (Bounds: b, Watch: screen.Start(b))).ToList();
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < WaitMs && watches.Any(w => w.Watch is { Version: 0 }))
                Thread.Sleep(15);
            foreach (var (bounds, watch) in watches)
            {
                if (watch?.Copy(bounds) is { } picture)
                    monitors.Add((bounds, picture));
            }
        }
        finally
        {
            foreach (var (_, watch) in watches)
                watch?.Dispose();
        }
        return new ScreenSnapshot(monitors);
    }

    /// <summary>The color of a pixel (desktop coordinates), or null outside every monitor.</summary>
    public Vec3b? Pixel(int x, int y)
    {
        foreach (var (bounds, picture) in Monitors)
            if (bounds.Contains(x, y))
                return picture.At<Vec3b>(y - bounds.Y, x - bounds.X);
        return null;
    }

    /// <summary>
    /// A part of the screen (desktop coordinates) as a picture: the part of the monitor that holds most of it. Null when
    /// it is on no monitor.
    /// </summary>
    public (Rect Area, Mat Picture)? Part(Rect area)
    {
        var best = Monitors
            .Select(m => (m.Bounds, m.Picture, Inside: area.Intersect(m.Bounds)))
            .Where(m => m.Inside.Width > 0 && m.Inside.Height > 0)
            .OrderByDescending(m => (long)m.Inside.Width * m.Inside.Height)
            .FirstOrDefault();
        if (best.Picture == null)
            return null;
        var inside = best.Inside;
        using var view = new Mat(best.Picture, new Rect(inside.X - best.Bounds.X, inside.Y - best.Bounds.Y, inside.Width, inside.Height));
        return (inside, view.Clone());
    }

    /// <summary>"#RRGGBB" of a BGR color.</summary>
    public static string Hex(Vec3b bgr) => $"#{bgr.Item2:X2}{bgr.Item1:X2}{bgr.Item0:X2}";

    public void Dispose()
    {
        foreach (var (_, picture) in Monitors)
            picture.Dispose();
        Monitors.Clear();
    }
}
