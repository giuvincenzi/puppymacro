using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace PuppyMacro.Services.Screen;

/// <summary>Where an image was found: its top-left corner on the searched picture and how similar it is (0 to 1).</summary>
internal readonly record struct ImageMatch(int X, int Y, double Score)
{
    public static readonly ImageMatch None = new(-1, -1, -1);

    public bool Found(double need) => X >= 0 && Score >= need;

    /// <summary>
    /// The better of two: the more similar; equally similar (a repeated piece), the higher on the screen, then the more to
    /// the left, so the same screen always gives the same answer.
    /// </summary>
    public static ImageMatch Best(ImageMatch a, ImageMatch b)
    {
        if (a.X < 0)
            return b;
        if (b.X < 0)
            return a;
        if (Math.Abs(a.Score - b.Score) > 1e-4)
            return a.Score > b.Score ? a : b;
        if (a.Y != b.Y)
            return a.Y < b.Y ? a : b;
        return a.X <= b.X ? a : b;
    }

    public ImageMatch Offset(int dx, int dy) => X < 0 ? this : this with { X = X + dx, Y = Y + dy };
}

/// <summary>
/// Image search on the processor: OpenCV's normalized correlation (TM_CCOEFF_NORMED) on color, coarse-to-fine as its
/// <see cref="Calibration"/> says. The same results as <see cref="GpuMatcher"/>.
/// </summary>
internal static class CpuMatcher
{
    /// <summary>The most places the coarse step hands to the full-size step.</summary>
    public const int MaxCandidates = 32;

    /// <summary>Below this brightness variance a window is flat (a black band): it matches nothing, as on the GPU.</summary>
    private const double FlatVariance = 0.25;

    /// <summary>Searches <paramref name="image"/> in <paramref name="area"/> of <paramref name="screen"/> (all of it when null).</summary>
    public static ImageMatch Find(Mat screen, Mat image, Calibration calibration, double need, Rect? area = null)
    {
        Rect a = (area ?? new Rect(0, 0, screen.Width, screen.Height)).Intersect(new Rect(0, 0, screen.Width, screen.Height));
        if (a.Width < image.Width || a.Height < image.Height)
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
        foreach (Point c in Candidates(scores, smallImage.Width, smallImage.Height, calibration.CoarseMin))
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
        if (picture.Width < image.Width || picture.Height < image.Height)
            return ImageMatch.None;
        using var scores = new Mat();
        Cv2.MatchTemplate(picture, image, scores, TemplateMatchModes.CCoeffNormed);
        IgnoreFlat(picture, image.Size(), scores);
        // MinMaxLoc gives the first maximum in reading order: the highest, then the leftmost.
        Cv2.MinMaxLoc(scores, out _, out double max, out _, out Point at);
        return max < -1.5 ? ImageMatch.None : new ImageMatch(at.X, at.Y, max);
    }

    /// <summary>The best places of a score map, best first, apart from each other by about half the image.</summary>
    public static List<Point> Candidates(Mat scores, int width, int height, double min)
    {
        using Mat left = scores.Clone();
        var list = new List<Point>();
        for (int i = 0; i < MaxCandidates; i++)
        {
            Cv2.MinMaxLoc(left, out _, out double max, out _, out Point at);
            if (max < min)
                break;
            list.Add(at);
            Cv2.Rectangle(left, new Rect(at.X - width / 2, at.Y - height / 2, width, height), Scalar.All(-2), -1);
        }
        return list;
    }

    /// <summary>
    /// OpenCV calls a flat window (no variation, like a black band) a perfect match: those scores become -2, so a flat piece
    /// of the screen never counts as found.
    /// </summary>
    private static void IgnoreFlat(Mat picture, Size image, Mat scores)
    {
        using Mat gray = picture.Channels() == 1 ? picture.Clone() : picture.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var sum = new Mat();
        using var squares = new Mat();
        Cv2.Integral(gray, sum, squares, MatType.CV_64F);
        int rw = scores.Width, rh = scores.Height, tw = image.Width, th = image.Height;
        double n = tw * th;
        using Mat s = Window(sum, rw, rh, tw, th);
        using Mat q = Window(squares, rw, rh, tw, th);
        using var mean = new Mat();
        using var meanOfSquares = new Mat();
        using var squaredMean = new Mat();
        using var variance = new Mat();
        Cv2.Divide(s, n, mean);
        Cv2.Divide(q, n, meanOfSquares);
        Cv2.Multiply(mean, mean, squaredMean);
        Cv2.Subtract(meanOfSquares, squaredMean, variance);
        using Mat flat = variance.LessThan(FlatVariance);
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
