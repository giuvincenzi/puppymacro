using System;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// How a monitor's capture tries again after an error: it waits longer each time (from <see cref="FirstDelayMs"/>, doubling,
/// up to <see cref="MaxDelayMs"/>) and gives up after <see cref="MaxFailures"/> errors in a row, about 15 seconds. A
/// capture that works again starts over. Never a duplication made again and again without a pause: that can exhaust the
/// system's memory.
/// </summary>
internal sealed class CaptureRetry
{
    public const int FirstDelayMs = 100;
    public const int MaxDelayMs = 3000;
    public const int MaxFailures = 8;

    /// <summary>Errors in a row.</summary>
    public int Failures { get; private set; }

    public bool GaveUp => Failures >= MaxFailures;

    /// <summary>One more error: how long to wait before the next try, in ms.</summary>
    public int Failed()
    {
        Failures++;
        return (int)Math.Min(MaxDelayMs, (long)FirstDelayMs << Math.Min(Failures - 1, 20));
    }

    /// <summary>The capture works: the next error waits the shortest time again.</summary>
    public void Succeeded() => Failures = 0;
}
