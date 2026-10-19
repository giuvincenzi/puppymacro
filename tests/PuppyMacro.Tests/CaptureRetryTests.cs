using System.Linq;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>A capture that keeps failing waits longer each time and gives up: never a duplication made again and again without a pause.</summary>
public class CaptureRetryTests
{
    [Fact]
    public void Errors_in_a_row_wait_longer_each_time_and_give_up()
    {
        var retry = new CaptureRetry();

        int[] delays = Enumerable.Range(0, CaptureRetry.MaxFailures).Select(_ => retry.Failed()).ToArray();

        Assert.Equal(new[] { 100, 200, 400, 800, 1600, 3000, 3000, 3000 }, delays);
        Assert.True(retry.GaveUp);
        Assert.InRange(delays.Sum(), 8000, 20000);
    }

    [Fact]
    public void A_capture_that_works_again_starts_over()
    {
        var retry = new CaptureRetry();
        retry.Failed();
        retry.Failed();

        retry.Succeeded();

        Assert.False(retry.GaveUp);
        Assert.Equal(CaptureRetry.FirstDelayMs, retry.Failed());
    }
}
