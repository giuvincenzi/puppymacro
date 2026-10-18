using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace PuppyMacro.Services.Screen;

/// <summary>Where an image was found: its top-left corner on the searched picture and how similar it is (0 to 1).</summary>
internal readonly record struct ImageMatch(int X, int Y, double Score)
{
    public static readonly ImageMatch None = new(-1, -1, -1);

    /// <summary>
    /// Scores this close count as equal: the same piece shown twice scores a little differently in 32-bit floats (and
    /// differently again on the other device), so the place chosen among equals must not depend on that rounding.
    /// </summary>
    public const double Tie = 1e-4;

    /// <summary>
    /// Whether it is similar enough, in whole percent as the user chooses and reads it: the score is computed in 32-bit
    /// floats, so an identical image gives 0.99997, which must count as the 100% it shows.
    /// </summary>
    public bool Found(double need) => X >= 0 && Percent(Score) >= Percent(need);

    /// <summary>A score (0 to 1) in whole percent, rounded as it is shown.</summary>
    public static int Percent(double score) => (int)Math.Round(Math.Max(0, score) * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The better of two: the more similar; equally similar (within <see cref="Tie"/>: a repeated piece), the higher on the
    /// screen, then the more to the left, so the same screen always gives the same answer on both devices.
    /// </summary>
    public static ImageMatch Best(ImageMatch a, ImageMatch b)
    {
        if (a.X < 0)
            return b;
        if (b.X < 0)
            return a;
        if (Math.Abs(a.Score - b.Score) > Tie)
            return a.Score > b.Score ? a : b;
        if (a.Y != b.Y)
            return a.Y < b.Y ? a : b;
        return a.X <= b.X ? a : b;
    }

    public ImageMatch Offset(int dx, int dy) => X < 0 ? this : this with { X = X + dx, Y = Y + dy };
}

/// <summary>
/// Image search on the processor: OpenCV's normalized correlation (TM_CCOEFF_NORMED on color), coarse-to-fine as its
/// <see cref="Calibration"/> says. The same results as <see cref="GpuMatcher"/>: the same flat places and images
/// (<see cref="Flatness"/>), the same candidates (<see cref="Candidates"/>) and the same place among equal scores
/// (<see cref="Place"/>).
/// </summary>
internal static class CpuMatcher
{
    /// <summary>The most places the coarse step hands to the full-size step.</summary>
    public const int MaxCandidates = 32;

    /// <summary>
    /// Searches <paramref name="image"/> in <paramref name="area"/> of <paramref name="screen"/> (all of it when null). A
    /// flat image (<see cref="Flatness"/>) is never found: it has no shape to find.
    /// </summary>
    public static ImageMatch Find(Mat screen, Mat image, Calibration calibration, double need, Rect? area = null)
    {
        Rect a = (area ?? new Rect(0, 0, screen.Width, screen.Height)).Intersect(new Rect(0, 0, screen.Width, screen.Height));
        if (a.Width < image.Width || a.Height < image.Height || Flatness.IsFlat(image))
            return ImageMatch.None;
        using var part = new Mat(screen, a);
        if (calibration.Factor <= 1)
            return FindFull(part, image).Offset(a.X, a.Y);

        int f = calibration.Factor;
        using Mat small0 = part.Resize(new Size(part.Width / f, part.Height / f), 0, 0, InterpolationFlags.Area);
        using Mat image0 = image.Resize(new Size(image.Width / f, image.Height / f), 0, 0, InterpolationFlags.Area);
        using Mat small = calibration.Gray ? small0.CvtColor(ColorConversionCodes.BGR2GRAY) : small0.Clone();
        using Mat smallImage = calibration.Gray ? image0.CvtColor(ColorConversionCodes.BGR2GRAY) : image0.Clone();
        if (small.Width < smallImage.Width || small.Height < smallImage.Height)
            return FindFull(part, image).Offset(a.X, a.Y);
        using var scores = new Mat();
        Cv2.MatchTemplate(small, smallImage, scores, TemplateMatchModes.CCoeffNormed);
        IgnoreFlat(small, smallImage.Size(), scores);

        var best = ImageMatch.None;
        foreach (Point c in Candidates(Above(scores, calibration.CoarseMin), smallImage.Width, smallImage.Height))
        {
            // Around each candidate, at full size: the full-size step decides.
            int m = f * 2;
            int x0 = Math.Max(0, c.X * f - m), y0 = Math.Max(0, c.Y * f - m);
            int x1 = Math.Min(part.Width, c.X * f + image.Width + m), y1 = Math.Min(part.Height, c.Y * f + image.Height + m);
            using var around = new Mat(part, new Rect(x0, y0, x1 - x0, y1 - y0));
            best = ImageMatch.Best(best, FindFull(around, image).Offset(x0, y0));
        }
        return best.Offset(a.X, a.Y);
    }

    /// <summary>The full-size search everywhere in <paramref name="picture"/>.</summary>
    public static ImageMatch FindFull(Mat picture, Mat image)
    {
        if (picture.Width < image.Width || picture.Height < image.Height || Flatness.IsFlat(image))
            return ImageMatch.None;
        using var scores = new Mat();
        Cv2.MatchTemplate(picture, image, scores, TemplateMatchModes.CCoeffNormed);
        IgnoreFlat(picture, image.Size(), scores);
        Cv2.MinMaxLoc(scores, out _, out double max);
        return max < -1.5 ? ImageMatch.None : Place(scores, max);
    }

    /// <summary>
    /// The place of a score map with the best score: the first in reading order (the highest, then the leftmost) whose
    /// score is within <see cref="ImageMatch.Tie"/> of <paramref name="max"/>, not the exact maximum, which float rounding
    /// picks among equal places. The graphics card chooses the same way.
    /// </summary>
    private static unsafe ImageMatch Place(Mat scores, double max)
    {
        float least = (float)(max - ImageMatch.Tie);
        int width = scores.Width, height = scores.Height;
        for (int y = 0; y < height; y++)
        {
            float* row = (float*)scores.Ptr(y);
            for (int x = 0; x < width; x++)
                if (row[x] >= least)
                    return new ImageMatch(x, y, row[x]);
        }
        return ImageMatch.None;
    }

    /// <summary>Every place of a coarse score map at or above <paramref name="min"/> (in 32-bit floats, as the graphics card compares), with its score.</summary>
    private static unsafe List<(float Score, int X, int Y)> Above(Mat scores, double min)
    {
        float least = (float)min;
        var list = new List<(float, int, int)>();
        int width = scores.Width, height = scores.Height;
        for (int y = 0; y < height; y++)
        {
            float* row = (float*)scores.Ptr(y);
            for (int x = 0; x < width; x++)
                if (row[x] >= least)
                    list.Add((row[x], x, y));
        }
        return list;
    }

    /// <summary>
    /// The candidates of the coarse step, on both devices, from every place at or above its threshold: the best first (on
    /// equal scores, the first in reading order), each one taking out the places around it (an image-sized rectangle
    /// centered on it), at most <see cref="MaxCandidates"/>.
    /// </summary>
    public static List<Point> Candidates(IEnumerable<(float Score, int X, int Y)> above, int width, int height)
    {
        var list = new List<Point>();
        foreach (var (_, x, y) in above.OrderByDescending(p => p.Score).ThenBy(p => p.Y).ThenBy(p => p.X))
        {
            if (list.Count >= MaxCandidates)
                break;
            if (list.Any(c => x >= c.X - width / 2 && x < c.X - width / 2 + width && y >= c.Y - height / 2 && y < c.Y - height / 2 + height))
                continue;
            list.Add(new Point(x, y));
        }
        return list;
    }

    /// <summary>
    /// OpenCV calls a flat window (no variation, like a black band) a perfect match: those scores become -2, so a flat piece
    /// of the screen never counts as found. Flat as <see cref="Flatness"/> says: the variance summed over the channels, as
    /// the graphics card measures it, so a window with detail only in color is not flat.
    /// </summary>
    private static void IgnoreFlat(Mat picture, Size image, Mat scores)
    {
        int rw = scores.Width, rh = scores.Height, tw = image.Width, th = image.Height;
        double n = tw * th;
        Mat[] channels = picture.Split();
        using var variance = new Mat(rh, rw, MatType.CV_64F, Scalar.All(0));
        try
        {
            foreach (Mat channel in channels)
            {
                using var sum = new Mat();
                using var squares = new Mat();
                Cv2.Integral(channel, sum, squares, MatType.CV_64F);
                using Mat s = Window(sum, rw, rh, tw, th);
                using Mat q = Window(squares, rw, rh, tw, th);
                using var mean = new Mat();
                using var meanOfSquares = new Mat();
                using var squaredMean = new Mat();
                Cv2.Divide(s, n, mean);
                Cv2.Divide(q, n, meanOfSquares);
                Cv2.Multiply(mean, mean, squaredMean);
                Cv2.Add(variance, meanOfSquares, variance);
                Cv2.Subtract(variance, squaredMean, variance);
            }
        }
        finally
        {
            foreach (Mat channel in channels)
                channel.Dispose();
        }
        using var flat = new Mat();
        Cv2.Compare(variance, Scalar.All(Flatness.MaxVariance), flat, CmpTypes.LE);
        scores.SetTo(Scalar.All(-2), flat);
    }

    /// <summary>The sum of every tw × th window, from an integral image.</summary>
    private static Mat Window(Mat integral, int rw, int rh, int tw, int th)
    {
        using var a = new Mat(integral, new Rect(tw, th, rw, rh));
        using var b = new Mat(integral, new Rect(tw, 0, rw, rh));
        using var c = new Mat(integral, new Rect(0, th, rw, rh));
        using var d = new Mat(integral, new Rect(0, 0, rw, rh));
        var sum = new Mat();
        Cv2.Subtract(a, b, sum);
        Cv2.Subtract(sum, c, sum);
        Cv2.Add(sum, d, sum);
        return sum;
    }
}
