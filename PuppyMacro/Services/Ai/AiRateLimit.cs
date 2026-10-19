using System;
using System.Diagnostics;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The rate limit of one AI app's connection (MCP: a server MUST rate limit tool invocations): a bucket of
/// <see cref="Burst"/> calls that refills <see cref="PerSecond"/> a second. An AI app at work never empties it; a runaway
/// loop of calls does, and its calls answer "too many requests" (an error the AI app can wait on) instead of filling the
/// History and the disk.
/// </summary>
internal sealed class AiRateLimit
{
    public const int Burst = 30;
    public const int PerSecond = 10;

    private readonly Func<double> _seconds;
    private readonly object _sync = new();
    private double _tokens = Burst;
    private double _last;

    /// <param name="seconds">The clock, in seconds (the unit tests give their own).</param>
    public AiRateLimit(Func<double>? seconds = null)
    {
        var clock = Stopwatch.StartNew();
        _seconds = seconds ?? (() => clock.Elapsed.TotalSeconds);
        _last = _seconds();
    }

    /// <summary>Takes one call: null when it may run now, otherwise how long until it may.</summary>
    public TimeSpan? Wait()
    {
        lock (_sync)
        {
            double now = _seconds();
            _tokens = Math.Min(Burst, _tokens + (now - _last) * PerSecond);
            _last = now;
            if (_tokens >= 1)
            {
                _tokens--;
                return null;
            }
            return TimeSpan.FromSeconds((1 - _tokens) / PerSecond);
        }
    }
}
