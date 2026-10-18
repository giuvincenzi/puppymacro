using System;
using OpenCvSharp;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// How an image is searched coarse-to-fine: shrunk first (the coarse step finds where it may be), then compared at full
/// size where the coarse step found something (the full-size step decides). Decided once per image.
/// </summary>
/// <param name="Factor">How much image and screen are shrunk for the coarse step; 1 = no coarse step.</param>
/// <param name="CoarseMin">The lowest coarse score that is still a candidate for the full-size step.</param>
/// <param name="Gray">The coarse step in gray (a third of the work); false when the image's detail is color only.</param>
internal readonly record struct Calibration(int Factor, double CoarseMin, bool Gray)
{
    /// <summary>The short side the shrunk image aims at: from the tests on game screenshots (12 px: no miss).</summary>
    public const int Target = 12;

    /// <summary>The most it shrinks.</summary>
    public const int MaxFactor = 8;

    /// <summary>The coarse score the image keeps with itself, at worst, below which a smaller factor is used.</summary>
    public const double MinSelf = 0.6;

    /// <summary>
    /// The factor and the coarse threshold of an image, calibrated on the image itself: shrunk with every alignment of the
    /// f × f grid, how much it still looks like itself at worst (an image with fine detail changes with the alignment).
    /// Starts from the factor that makes the short side about <see cref="Target"/> and lowers it while the worst case is
    /// below <see cref="MinSelf"/>.
    /// </summary>
    public static Calibration Of(Mat image, double need)
    {
        bool gray = GrayKeepsDetail(image);
        using Mat source = gray ? image.CvtColor(ColorConversionCodes.BGR2GRAY) : image.Clone();
        for (int f = Math.Clamp(Math.Min(image.Width, image.Height) / Target, 1, MaxFactor); f > 1; f--)
        {
            int cw = (image.Width - f) / f, ch = (image.Height - f) / f;
            if (cw < 4 || ch < 4 || CoarseIsFlat(image, f, gray))
                continue;
            using Mat aligned = Shrink(source, 0, 0, f, cw, ch);
            double worst = 1;
            for (int py = 0; py < f && worst >= MinSelf; py++)
            {
                for (int px = 0; px < f; px++)
                {
                    if (px == 0 && py == 0)
                        continue;
                    using Mat shifted = Shrink(source, px, py, f, cw, ch);
                    using var score = new Mat();
                    Cv2.MatchTemplate(shifted, aligned, score, TemplateMatchModes.CCoeffNormed);
                    worst = Math.Min(worst, score.At<float>(0, 0));
                }
            }
            if (worst >= MinSelf)
                return new Calibration(f, need * worst - 0.15, gray);
        }
        return new Calibration(1, need, gray);
    }

    /// <summary>
    /// Whether gray keeps the image's detail: false when its differences are mostly of color at the same brightness (red on
    /// green), which gray would flatten.
    /// </summary>
    public static bool GrayKeepsDetail(Mat image)
    {
        Cv2.MeanStdDev(image, out _, out Scalar color);
        using Mat gray = image.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out _, out Scalar brightness);
        double colorSpread = Math.Sqrt((color.Val0 * color.Val0 + color.Val1 * color.Val1 + color.Val2 * color.Val2) / 3);
        return brightness.Val0 >= 0.5 * colorSpread;
    }

    /// <summary>
    /// The image shrunk by <paramref name="f"/> as the coarse step searches it is flat (<see cref="Flatness"/>): its detail
    /// is finer than the factor, so the coarse step would match nothing (or everything); a smaller factor is used.
    /// </summary>
    private static bool CoarseIsFlat(Mat image, int f, bool gray)
    {
        using Mat shrunk = image.Resize(new Size(image.Width / f, image.Height / f), 0, 0, InterpolationFlags.Area);
        if (!gray)
            return Flatness.IsFlat(shrunk);
        using Mat coarse = shrunk.CvtColor(ColorConversionCodes.BGR2GRAY);
        return Flatness.IsFlat(coarse);
    }

    private static Mat Shrink(Mat source, int px, int py, int f, int cw, int ch)
    {
        using var part = new Mat(source, new Rect(px, py, cw * f, ch * f));
        return part.Resize(new Size(cw, ch), 0, 0, InterpolationFlags.Area);
    }
}
