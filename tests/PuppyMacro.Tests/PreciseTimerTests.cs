using System;
using System.Diagnostics;
using System.Threading;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// PreciseTimer, the waits of every loop row and macro: a wait lasts its time, a stop ends it at once (also before it
/// starts), a wait that must finish (a paste before its clipboard is restored) ignores the stop, and a wait cut short by a
/// stop leaves nothing behind that would end the next one early. Times have wide margins: the machines can be slow.
/// </summary>
public sealed class PreciseTimerTests : IDisposable
{
    private readonly PreciseTimer _timer = PreciseTimer.Create();
    private readonly ManualResetEvent _never = new(false);

    public void Dispose()
    {
        _timer.Dispose();
        _never.Dispose();
    }

    private static double Ms(Action action)
    {
        var clock = Stopwatch.StartNew();
        action();
        return clock.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void A_wait_lasts_its_time_and_says_it_was_not_stopped()
    {
        bool result = false;
        double took = Ms(() => result = _timer.Wait(200, _never));

        Assert.True(result);
        Assert.InRange(took, 190, 1500);
    }

    [Fact]
    public void A_stop_set_before_the_wait_ends_it_at_once()
    {
        using var stop = new ManualResetEvent(true);
        bool result = true;
        double took = Ms(() => result = _timer.Wait(10_000, stop));

        Assert.False(result);
        Assert.True(took < 1000, $"took {took:0} ms");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void A_wait_of_no_time_only_says_whether_it_is_stopped(double ms)
    {
        using var stop = new ManualResetEvent(false);
        Assert.True(_timer.Wait(ms, stop));
        stop.Set();
        Assert.False(_timer.Wait(ms, stop));
    }

    [Fact]
    public void A_stop_during_a_wait_ends_it_at_once()
    {
        using var stop = new ManualResetEvent(false);
        var stopper = new Thread(() =>
        {
            Thread.Sleep(200);
            stop.Set();
        }) { IsBackground = true };
        stopper.Start();
        bool result = true;
        double took = Ms(() => result = _timer.Wait(60_000, stop));
        stopper.Join();

        Assert.False(result);
        Assert.InRange(took, 150, 3000);
    }

    /// <summary>A wait cut short by a stop leaves the timer armed: the next wait must last its own time, not end at the old due time.</summary>
    [Fact]
    public void A_wait_after_one_cut_short_lasts_its_own_time()
    {
        using var stop = new ManualResetEvent(true);
        Assert.False(_timer.Wait(100, stop)); // the timer stays armed for 100 ms
        Thread.Sleep(300); // and fires meanwhile

        bool result = false;
        double took = Ms(() => result = _timer.Wait(400, _never));

        Assert.True(result);
        Assert.True(took >= 380, $"the next wait ended after {took:0} ms, not 400");
    }

    [Fact]
    public void A_full_wait_ignores_a_stop()
    {
        using var stop = new ManualResetEvent(true);
        double took = Ms(() => _timer.WaitFully(200));

        Assert.InRange(took, 190, 1500);
        Assert.True(stop.WaitOne(0)); // the stop is still there for what comes after
    }

    [Fact]
    public void A_huge_wait_still_ends_at_a_stop()
    {
        using var stop = new ManualResetEvent(false);
        var stopper = new Thread(() =>
        {
            Thread.Sleep(200);
            stop.Set();
        }) { IsBackground = true };
        stopper.Start();
        bool result = true;
        double took = Ms(() => result = _timer.Wait(double.MaxValue, stop));
        stopper.Join();

        Assert.False(result);
        Assert.True(took < 3000, $"took {took:0} ms");
    }
}
