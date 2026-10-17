using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Screen;

/// <summary>What an If found: whether its conditions are true, and where each Image or Text condition found its match.</summary>
internal sealed record ConditionResult(bool Met, bool Stopped, IReadOnlyDictionary<Guid, Point> Found)
{
    public static readonly ConditionResult Stop = new(false, true, new Dictionary<Guid, Point>());
}

/// <summary>
/// One condition checked now, with what PuppyMacro saw (the editor's messages): the pixel's color, the image's best
/// similarity, the text read, or why it could not look.
/// </summary>
internal sealed record ConditionReport(bool Met, Point? At = null, double Score = 0, string? Color = null, string? Read = null,
    bool Black = false, bool NoMonitor = false, bool NoLanguage = false);

/// <summary>
/// Checks the conditions of a macro's If on the screen, for one macro run: the images are read and prepared once and kept
/// until <see cref="Dispose"/>. Checks run at each new image of the screen, at most <c>ChecksPerSecond</c> times a second
/// (0: every image); the macro's Speed does not change the wait.
/// </summary>
internal sealed class ConditionChecker : IDisposable
{
    /// <summary>
    /// Areas larger than this (pixels) are searched on the graphics card with <see cref="RecognitionDevice.Automatic"/>,
    /// smaller ones on the processor, which is faster there (the tests: 2 ms against 11 ms on a small area).
    /// </summary>
    public const int GpuFromPixels = 1_000_000;

    /// <summary>A condition checked once still waits this long for the first image of a monitor that just started.</summary>
    public const int FirstImageMs = 1000;

    /// <summary>The sizes tried with "A little bigger or smaller", the image's own first.</summary>
    public static readonly double[] NearbyScales = { 1.0, 0.95, 1.05, 0.9, 1.1, 0.85, 1.15 };

    private readonly IScreen _screen;
    private readonly ImageStore? _images;
    private readonly RecognitionDevice _device;
    private readonly int _checksPerSecond;
    private readonly Dictionary<(string Id, double Scale), Mat?> _prepared = new();
    private readonly Dictionary<(Mat Image, double Need), Calibration> _calibrations = new();
    private readonly HashSet<GpuMatcher> _usedGpus = new();

    public ConditionChecker(IScreen screen, ImageStore? images, RecognitionDevice device, int checksPerSecond)
    {
        _screen = screen;
        _images = images;
        _device = device;
        _checksPerSecond = Math.Max(0, checksPerSecond);
    }

    /// <summary>One place a condition looks at.</summary>
    private sealed class Look
    {
        public required MacroCondition Condition { get; init; }
        public required Rect Area { get; init; }
        public ConditionImage? Image { get; init; }
        public IScreenWatch? Watch { get; set; }
    }

    /// <summary>Checks the If's conditions, waiting as it says, until they are true, the wait ends or <paramref name="stop"/>.</summary>
    public ConditionResult Check(MacroIf block, WaitHandle stop)
    {
        var looks = block.Conditions.SelectMany(Places).ToList();
        using var waiter = new AutoResetEvent(false);
        _screen.Listen(waiter);
        try
        {
            foreach (var look in looks)
                look.Watch = _screen.Start(look.Area);

            var clock = Stopwatch.StartNew();
            double limitMs = block.Wait switch
            {
                ConditionWait.Once => FirstImageMs,
                ConditionWait.UpTo => block.WaitSeconds * 1000,
                _ => double.PositiveInfinity,
            };
            double gapMs = _checksPerSecond == 0 ? 0 : 1000.0 / _checksPerSecond;
            var seen = new long[looks.Count];
            double lastCheck = double.NegativeInfinity;
            while (true)
            {
                bool ready = looks.All(l => l.Watch == null || l.Watch.Version != 0);
                bool changed = looks.Select((l, i) => l.Watch != null && l.Watch.Version != seen[i]).Any(c => c);
                double now = clock.Elapsed.TotalMilliseconds;
                if ((ready && (changed || double.IsNegativeInfinity(lastCheck)) && now - lastCheck >= gapMs)
                    || (block.Wait == ConditionWait.Once && now >= limitMs))
                {
                    for (int i = 0; i < looks.Count; i++)
                        seen[i] = looks[i].Watch?.Version ?? 0;
                    lastCheck = now;
                    var found = new Dictionary<Guid, Point>();
                    if (Evaluate(block, looks, found))
                        return new ConditionResult(true, false, found);
                    if (block.Wait == ConditionWait.Once)
                        return new ConditionResult(false, false, found);
                }
                now = clock.Elapsed.TotalMilliseconds;
                if (now >= limitMs && block.Wait == ConditionWait.UpTo)
                    return new ConditionResult(false, false, new Dictionary<Guid, Point>());
                // Wait for a new image, the next allowed check or the end of the wait, whichever comes first.
                double next = Math.Min(limitMs - now, changed && ready ? Math.Max(1, gapMs - (now - lastCheck)) : 250);
                int timeout = (int)Math.Clamp(Math.Ceiling(next), 1, 250);
                if (WaitHandle.WaitAny(new[] { stop, waiter }, timeout) == 0)
                    return ConditionResult.Stop;
            }
        }
        finally
        {
            _screen.Unlisten(waiter);
            foreach (var look in looks)
                look.Watch?.Dispose();
        }
    }

    /// <summary>Checks one condition on the screen now and says what it saw (the editor's live check and Test).</summary>
    public ConditionReport Inspect(MacroCondition condition)
    {
        if (condition.Kind == ConditionKind.Text && !TextReader.CanRead(condition.Language))
            return new ConditionReport(false, NoLanguage: true);
        var looks = Places(condition).ToList();
        if (looks.Count == 0)
            return new ConditionReport(false);
        try
        {
            foreach (var look in looks)
                look.Watch = _screen.Start(look.Area);
            if (looks.All(l => l.Watch == null))
                return new ConditionReport(false, NoMonitor: true);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < FirstImageMs && looks.Any(l => l.Watch is { Version: 0 }))
                Thread.Sleep(15);

            var report = new ConditionReport(false);
            foreach (var look in looks.Where(l => l.Watch != null))
            {
                Rect area = look.Area.Intersect(look.Watch!.Bounds);
                using var picture = look.Watch.Copy(area);
                if (picture == null)
                    continue;
                bool black = IsBlack(picture);
                switch (condition.Kind)
                {
                    case ConditionKind.Pixel:
                    {
                        var color = picture.At<Vec3b>(0, 0);
                        return new ConditionReport(PixelMatches(color, condition.Color, condition.Tolerance), new Point(condition.X, condition.Y),
                            Color: ScreenSnapshot.Hex(color));
                    }
                    case ConditionKind.Image:
                    {
                        var (met, at, score) = FindImage(look.Watch, area, look.Image!, condition);
                        if (met && (!report.Met || score > report.Score) || !report.Met && score > report.Score)
                            report = new ConditionReport(met, met ? at : null, score, Black: black);
                        else if (black)
                            report = report with { Black = true };
                        break;
                    }
                    case ConditionKind.Text:
                    {
                        var lines = TextReader.Read(picture, condition.Language);
                        string read = string.Join(" ", lines.Select(l => string.Join(" ", l.Select(w => w.Text))));
                        var found = string.IsNullOrWhiteSpace(condition.Text) ? null
                            : TextReader.Find(lines, condition.Text, condition.TextMatch, condition.MatchCase);
                        Point? at = found is { } r ? new Point(area.X + r.X + r.Width / 2, area.Y + r.Y + r.Height / 2) : null;
                        return new ConditionReport(found != null, at, Read: read, Black: black);
                    }
                }
            }
            return report;
        }
        finally
        {
            foreach (var look in looks)
                look.Watch?.Dispose();
        }
    }

    /// <summary>Nothing but black: the app hides itself from screen capture, or runs in exclusive fullscreen.</summary>
    private static bool IsBlack(Mat picture)
    {
        using var gray = picture.Channels() == 1 ? picture.Clone() : picture.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out _, out double max);
        return max < 8;
    }

    /// <summary>All or Any of the conditions; the places found are those of the conditions found true.</summary>
    private bool Evaluate(MacroIf block, List<Look> looks, Dictionary<Guid, Point> found)
    {
        bool any = false, all = true;
        foreach (var condition in block.Conditions)
        {
            bool met = false;
            Point? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var look in looks.Where(l => l.Condition == condition))
            {
                var (ok, at, score) = Check(look);
                if (ok && score > bestScore)
                {
                    met = true;
                    best = at;
                    bestScore = score;
                }
            }
            if (met && best is { } point && condition.Finds)
                found[condition.Id] = point;
            any |= met;
            all &= met;
            if (block.Match == ConditionMatch.All && !met)
                return false;
        }
        return block.Match == ConditionMatch.All ? all : any;
    }

    /// <summary>The places a condition looks at: a pixel, each image near where it was taken or in the area, the text's area.</summary>
    private IEnumerable<Look> Places(MacroCondition c)
    {
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
                yield return new Look { Condition = c, Area = new Rect(c.X, c.Y, 1, 1) };
                break;
            case ConditionKind.Image:
                foreach (var image in c.Images ?? new List<ConditionImage>())
                {
                    var mat = Image(image.Id, 1.0);
                    if (mat == null)
                        continue;
                    Rect area = c.Area == SearchArea.Around ? Around(image, mat, c.NearbySizes) : new Rect(c.AreaX, c.AreaY, c.AreaWidth, c.AreaHeight);
                    yield return new Look { Condition = c, Area = area, Image = image };
                }
                break;
            case ConditionKind.Text:
                yield return new Look { Condition = c, Area = new Rect(c.AreaX, c.AreaY, c.AreaWidth, c.AreaHeight) };
                break;
        }
    }

    /// <summary>Near where the image was taken: its rectangle grown by its larger side on every side.</summary>
    public static Rect Around(ConditionImage image, Mat mat, bool nearbySizes)
    {
        int margin = Math.Max(mat.Width, mat.Height);
        if (nearbySizes)
            margin += (int)Math.Ceiling(Math.Max(mat.Width, mat.Height) * (NearbyScales.Max() - 1));
        return new Rect(image.X - margin, image.Y - margin, mat.Width + 2 * margin, mat.Height + 2 * margin);
    }

    private (bool Met, Point At, double Score) Check(Look look)
    {
        if (look.Watch is not { } watch)
            return (false, default, 0);
        Rect area = look.Area.Intersect(watch.Bounds);
        if (area.Width <= 0 || area.Height <= 0)
            return (false, default, 0);
        var c = look.Condition;
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
            {
                using var pixel = watch.Copy(area);
                if (pixel == null)
                    return (false, default, 0);
                var seen = pixel.At<Vec3b>(0, 0);
                bool met = PixelMatches(seen, c.Color, c.Tolerance);
                MacroTrace.Write($"  pixel {c.X},{c.Y}: {ScreenSnapshot.Hex(seen)}, wanted {c.Color} ±{c.Tolerance}% -> {met} (frame {watch.Version})");
                return (met, new Point(c.X, c.Y), 1);
            }
            case ConditionKind.Image:
                return FindImage(watch, area, look.Image!, c);
            case ConditionKind.Text:
            {
                using var picture = watch.Copy(area);
                if (picture == null || string.IsNullOrWhiteSpace(c.Text))
                    return (false, default, 0);
                var lines = TextReader.Read(picture, c.Language);
                if (TextReader.Find(lines, c.Text, c.TextMatch, c.MatchCase) is not { } r)
                    return (false, default, 0);
                return (true, new Point(area.X + r.X + r.Width / 2, area.Y + r.Y + r.Height / 2), 1);
            }
        }
        return (false, default, 0);
    }

    /// <summary>Whether red, green and blue are each within <paramref name="tolerance"/> percent of 255 of the color.</summary>
    public static bool PixelMatches(Vec3b bgr, string? color, int tolerance)
    {
        if (color is not { Length: 7 } || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        double most = Math.Clamp(tolerance, 0, 100) * 255 / 100.0;
        return Math.Abs(bgr.Item2 - ((rgb >> 16) & 0xFF)) <= most
            && Math.Abs(bgr.Item1 - ((rgb >> 8) & 0xFF)) <= most
            && Math.Abs(bgr.Item0 - (rgb & 0xFF)) <= most;
    }

    private (bool Met, Point At, double Score) FindImage(IScreenWatch watch, Rect area, ConditionImage image, MacroCondition c)
    {
        double need = Math.Clamp(c.Similarity, 50, 100) / 100.0;
        var gpu = UseGpu(watch, area) ? watch.Gpu : null;
        GpuMatcher.GpuBuffer? onCard = null;
        Mat? onProcessor = null;
        try
        {
            if (gpu != null)
                onCard = watch.Picture(area);
            else
                onProcessor = watch.Copy(area);
            if (onCard == null && onProcessor == null)
                return (false, default, 0);

            var best = ImageMatch.None;
            Size bestSize = default;
            foreach (double scale in c.NearbySizes ? NearbyScales : NearbyScales.Take(1))
            {
                var mat = Image(image.Id, scale);
                if (mat == null)
                    continue;
                if (!_calibrations.TryGetValue((mat, need), out var cal))
                    _calibrations[(mat, need)] = cal = Calibration.Of(mat, need);
                ImageMatch match;
                if (onCard != null)
                {
                    _usedGpus.Add(gpu!);
                    match = gpu!.Find(onCard, mat, cal, need);
                }
                else
                {
                    match = CpuMatcher.Find(onProcessor!, mat, cal, need);
                }
                if (ImageMatch.Best(best, match) == match && match.X >= 0)
                {
                    best = match;
                    bestSize = mat.Size();
                }
                // The image's own size found it: the others are not tried.
                if (scale == 1.0 && best.Found(need))
                    break;
            }
            if (!best.Found(need))
                return (false, default, best.Score);
            return (true, new Point(area.X + best.X + bestSize.Width / 2, area.Y + best.Y + bestSize.Height / 2), best.Score);
        }
        finally
        {
            onCard?.Dispose();
            onProcessor?.Dispose();
        }
    }

    private bool UseGpu(IScreenWatch watch, Rect area) => watch.Gpu != null && _device switch
    {
        RecognitionDevice.Cpu => false,
        RecognitionDevice.Gpu => true,
        _ => !watch.Software && (long)area.Width * area.Height > GpuFromPixels,
    };

    /// <summary>An image of the store at a size, read once per run; null when the store has not it.</summary>
    private Mat? Image(string id, double scale)
    {
        if (_prepared.TryGetValue((id, scale), out var kept))
            return kept;
        Mat? mat = null;
        if (scale == 1.0)
        {
            if (_images?.Read(id) is { } png)
            {
                mat = Cv2.ImDecode(png, ImreadModes.Color);
                if (mat.Empty())
                {
                    mat.Dispose();
                    mat = null;
                }
            }
        }
        else if (Image(id, 1.0) is { } original)
        {
            int w = (int)Math.Round(original.Width * scale), h = (int)Math.Round(original.Height * scale);
            if (w >= 4 && h >= 4)
                mat = original.Resize(new Size(w, h), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        }
        _prepared[(id, scale)] = mat;
        return mat;
    }

    public void Dispose()
    {
        foreach (var mat in _prepared.Values)
        {
            if (mat == null)
                continue;
            foreach (var gpu in _usedGpus)
                gpu.Forget(mat);
            mat.Dispose();
        }
        _prepared.Clear();
        _calibrations.Clear();
        _usedGpus.Clear();
    }
}
