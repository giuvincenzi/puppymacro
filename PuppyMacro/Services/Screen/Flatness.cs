using OpenCvSharp;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// When a picture is flat (one color, or so close to it that it has no shape): the one definition of the image search on
/// both devices, for a place of the screen and for an image. The variance of its pixels, summed over its channels (blue,
/// green and red; gray has one), at or below <see cref="MaxVariance"/>. In color, so detail only in color (red on green of
/// one brightness) is not flat.
/// </summary>
internal static class Flatness
{
    /// <summary>
    /// The largest variance per pixel, summed over the channels, of a flat picture. The graphics card's shaders use the
    /// same number (<see cref="GpuMatcher"/>: a window is flat when its summed variance is not above N × 0.5).
    /// </summary>
    public const double MaxVariance = 0.5;

    /// <summary>The picture is flat: normalized correlation finds no shape in it, so it matches nothing and is never found.</summary>
    public static bool IsFlat(Mat picture)
    {
        Cv2.MeanStdDev(picture, out _, out Scalar spread);
        double variance = 0;
        int channels = picture.Channels();
        for (int c = 0; c < channels; c++)
            variance += spread[c] * spread[c];
        return variance <= MaxVariance;
    }
}
