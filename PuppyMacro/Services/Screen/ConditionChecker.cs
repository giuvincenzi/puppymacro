using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// What an If found: whether its conditions are true, and where each Image or Text condition found its match. Asked to
/// describe (a run's Debug log): what each condition saw at the last check, in English, and, when they were not true, a
/// picture of each monitor they looked at (the caller disposes them).
/// </summary>
internal sealed record ConditionResult(bool Met, bool Stopped, IReadOnlyDictionary<Guid, Point> Found)
{
    public static readonly ConditionResult Stop = new(false, true, new Dictionary<Guid, Point>());

    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public IReadOnlyList<(Rect Bounds, Mat Picture)> Pictures { get; init; } = Array.Empty<(Rect, Mat)>();
}

/// <summary>
/// One condition checked now, with what PuppyMacro saw (the editor's messages): the pixel's color, the image's best
/// similarity, the text read, or why it could not look.
/// </summary>
internal sealed record ConditionReport(bool Met, Point? At = null, double Score = 0, string? Color = null, string? Read = null,
    bool Black = false, bool NoMonitor = false, bool NoLanguage = false)
{
    /// <summary>
    /// Whether the condition is true by what was seen (<see cref="Met"/>: what it looks for is there): with Not, when it
    /// is not there; a part of the screen PuppyMacro could not see never makes it true. The editor and the AI tools say it
    /// the same way.
    /// </summary>
    public bool TrueFor(MacroCondition condition) => condition.Not ? !NoLanguage && !NoMonitor && !Met : Met;
}

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

    /// <summary>How often a capture that gave up is tried again while a condition waits for it.</summary>
    public const int RestartMs = 2000;

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

        /// <summary>What the last check saw here, for the Debug log: a pixel's color, the text read.</summary>
        public string? Seen { get; set; }
    }

    /// <summary>Checks the If's conditions, waiting as it says, until they are true, the wait ends or <paramref name="stop"/>.</summary>
    public ConditionResult Check(MacroIf block, WaitHandle stop, bool describe = false)
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
            List<string>? details = null;
            double lastCheck = double.NegativeInfinity, lastRestart = 0;
            while (true)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                // A capture that gave up or whose monitor changed: a new watch, at most every RestartMs, gets a new one.
                if (now - lastRestart >= RestartMs && looks.Any(l => l.Watch is { Failed: true }))
                {
                    lastRestart = now;
                    for (int i = 0; i < looks.Count; i++)
                    {
                        if (looks[i].Watch is not { Failed: true } failed)
                            continue;
                        failed.Dispose();
                        looks[i].Watch = _screen.Start(looks[i].Area);
                        seen[i] = 0;
                    }
                }
                // Ready when every capture gave its first image; one that has none (gone, failed) does not hold the others
                // back: its conditions are simply not true.
                bool ready = looks.All(l => l.Watch is not { Version: 0, Failed: false });
                bool changed = looks.Select((l, i) => l.Watch != null && l.Watch.Version != seen[i]).Any(c => c);
                bool firstWait = double.IsNegativeInfinity(lastCheck);
                // The first check waits for the first images, up to FirstImageMs, whatever the wait: an Up to shorter than
                // the capture's start still looks once.
                bool mustCheck = firstWait && now >= FirstImageMs;
                if ((ready && (changed || firstWait) && now - lastCheck >= gapMs) || mustCheck)
                {
                    for (int i = 0; i < looks.Count; i++)
                        seen[i] = looks[i].Watch?.Version ?? 0;
                    lastCheck = now;
                    var found = new Dictionary<Guid, Point>();
                    details = describe ? new List<string>() : null;
                    if (Evaluate(block, looks, found, details))
                        return new ConditionResult(true, false, found) { Details = details ?? (IReadOnlyList<string>)Array.Empty<string>() };
                    if (block.Wait == ConditionWait.Once)
                        return NotTrue(looks, details);
                }
                now = clock.Elapsed.TotalMilliseconds;
                if (block.Wait == ConditionWait.UpTo && now >= limitMs && !double.IsNegativeInfinity(lastCheck))
                    return NotTrue(looks, details);
                // Wait for a new image, the next allowed check or the end of the wait, whichever comes first.
                double left = (block.Wait == ConditionWait.UpTo && !double.IsNegativeInfinity(lastCheck) ? limitMs : double.PositiveInfinity) - now;
                double next = Math.Min(left, changed && ready ? Math.Max(1, gapMs - (now - lastCheck)) : 250);
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

    /// <summary>Not true in time: what the last check saw and, when asked to describe, a picture of each monitor looked at.</summary>
    private static ConditionResult NotTrue(List<Look> looks, List<string>? details)
    {
        var pictures = new List<(Rect, Mat)>();
        if (details != null)
        {
            foreach (var watch in looks.Select(l => l.Watch).OfType<IScreenWatch>().Where(w => !w.Failed).DistinctBy(w => w.Bounds))
            {
                try
                {
                    if (watch.Copy(watch.Bounds) is { } picture)
                        pictures.Add((watch.Bounds, picture));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A picture that cannot be taken only leaves the log without it.
                }
            }
        }
        return new ConditionResult(false, false, new Dictionary<Guid, Point>())
        {
            Details = details ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Pictures = pictures,
        };
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
                        var (_, met, at, score) = FindImage(look.Watch, area, look.Image!, condition);
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

    /// <summary>
    /// All or Any of the conditions; the places found are those of the conditions found true. A condition true for not
    /// being there is true only when every place it looks at was seen, so a screen it could not see never makes it true.
    /// </summary>
    private bool Evaluate(MacroIf block, List<Look> looks, Dictionary<Guid, Point> found, List<string>? details = null)
    {
        bool any = false, all = true;
        foreach (var condition in block.Conditions)
        {
            bool there = false, seenAll = true, looked = false;
            Point? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var look in looks.Where(l => l.Condition == condition))
            {
                var (seen, ok, at, score) = Check(look);
                seenAll &= seen;
                looked = true;
                details?.Add(Describe(condition, look, seen, ok, at, score));
                if (ok && score > bestScore)
                {
                    there = true;
                    best = at;
                    bestScore = score;
                }
            }
            bool met = condition.Not ? looked && seenAll && !there : there;
            if (details != null)
            {
                if (!looked)
                    details.Add($"{Name(condition)}: nothing to look at (no image PuppyMacro has, or no text)");
                details.Add($"  -> {Name(condition)} is {(met ? "true" : "not true")}{(condition.Not ? " (true when it is not there)" : "")}");
            }
            if (met && best is { } point && condition.GivesPlace)
                found[condition.Id] = point;
            any |= met;
            all &= met;
            if (block.Match == ConditionMatch.All && !met)
            {
                if (details != null && condition != block.Conditions[^1])
                    details.Add("  (the next conditions were not checked: All needs every one)");
                return false;
            }
        }
        return block.Match == ConditionMatch.All ? all : any;
    }

    private static string Name(MacroCondition c) => c.Kind switch
    {
        ConditionKind.Pixel => $"Pixel {c.X},{c.Y}",
        ConditionKind.Image => $"Image \"{c.Name}\"",
        _ => $"Text \"{c.Name}\"",
    };

    /// <summary>One place of a condition as the Debug log shows it: what was wanted and what PuppyMacro saw.</summary>
    private string Describe(MacroCondition c, Look look, bool seen, bool there, Point at, double score)
    {
        if (!seen)
            return $"{Name(c)}: PuppyMacro could not see this part of the screen (no monitor there, or its capture failed)";
        return c.Kind switch
        {
            ConditionKind.Pixel => $"{Name(c)}: saw {look.Seen}, wanted {c.Color} within {c.Tolerance}%",
            ConditionKind.Image when there => $"{Name(c)}: found at {at.X},{at.Y}, {ImageMatch.Percent(score)}% similar (needs {c.Similarity}%)",
            ConditionKind.Image => $"{Name(c)}: not found, the closest is {ImageMatch.Percent(score)}% similar (needs {c.Similarity}%)",
            _ when there => $"{Name(c)}: \"{c.Text}\" found at {at.X},{at.Y}",
            _ => $"{Name(c)}: \"{c.Text}\" not found; read \"{Shorten(look.Seen ?? "")}\"",
        };
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;

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

    /// <summary>One place checked now: whether PuppyMacro could see it, whether what it looks for is there, where and how similar.</summary>
    private (bool Seen, bool Met, Point At, double Score) Check(Look look)
    {
        if (look.Watch is not { } watch)
            return (false, false, default, 0);
        Rect area = look.Area.Intersect(watch.Bounds);
        if (area.Width <= 0 || area.Height <= 0)
            return (false, false, default, 0);
        var c = look.Condition;
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
            {
                using var pixel = watch.Copy(area);
                if (pixel == null)
                    return (false, false, default, 0);
                var seen = pixel.At<Vec3b>(0, 0);
                bool met = PixelMatches(seen, c.Color, c.Tolerance);
                look.Seen = ScreenSnapshot.Hex(seen);
                return (true, met, new Point(c.X, c.Y), 1);
            }
            case ConditionKind.Image:
                return FindImage(watch, area, look.Image!, c);
            case ConditionKind.Text:
            {
                using var picture = watch.Copy(area);
                if (picture == null || string.IsNullOrWhiteSpace(c.Text))
                    return (false, false, default, 0);
                var lines = TextReader.Read(picture, c.Language);
                look.Seen = string.Join(" ", lines.Select(l => string.Join(" ", l.Select(w => w.Text))));
                if (TextReader.Find(lines, c.Text, c.TextMatch, c.MatchCase) is not { } r)
                    return (true, false, default, 0);
                return (true, true, new Point(area.X + r.X + r.Width / 2, area.Y + r.Y + r.Height / 2), 1);
            }
        }
        return (false, false, default, 0);
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

    private (bool Seen, bool Met, Point At, double Score) FindImage(IScreenWatch watch, Rect area, ConditionImage image, MacroCondition c)
    {
        double need = Math.Clamp(c.Similarity, 50, 100) / 100.0;
        // The image at its own size decides the device: one that can't be shrunk (calibration factor 1) is searched brute
        // force, which OpenCV does far faster on the processor (with the Fourier transform) than the shaders.
        var own = Image(image.Id, 1.0);
        bool shrinks = own != null && CalibrationOf(own, need).Factor > 1;
        var gpu = UseGpu(watch, area, shrinks) ? watch.Gpu : null;
        GpuMatcher.GpuBuffer? onCard = null;
        Mat? onProcessor = null;
        try
        {
            if (gpu != null)
                onCard = watch.Picture(area);
            else
                onProcessor = watch.Copy(area);
            if (onCard == null && onProcessor == null)
                return (false, false, default, 0);

            var best = ImageMatch.None;
            Size bestSize = default;
            foreach (double scale in c.NearbySizes ? NearbyScales : NearbyScales.Take(1))
            {
                var mat = Image(image.Id, scale);
                if (mat == null)
                    continue;
                var cal = CalibrationOf(mat, need);
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
                return (true, false, default, best.Score);
            return (true, true, new Point(area.X + best.X + bestSize.Width / 2, area.Y + best.Y + bestSize.Height / 2), best.Score);
        }
        finally
        {
            onCard?.Dispose();
            onProcessor?.Dispose();
        }
    }

    private bool UseGpu(IScreenWatch watch, Rect area, bool shrinks) => watch.Gpu != null && _device switch
    {
        RecognitionDevice.Cpu => false,
        RecognitionDevice.Gpu => true,
        _ => !watch.Software && shrinks && (long)area.Width * area.Height > GpuFromPixels,
    };

    private Calibration CalibrationOf(Mat mat, double need)
    {
        if (!_calibrations.TryGetValue((mat, need), out var calibration))
            _calibrations[(mat, need)] = calibration = Calibration.Of(mat, need);
        return calibration;
    }

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
