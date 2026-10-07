using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.Tests;

public class HotkeyBindingTests
{
    [Fact]
    public void SameAs_needs_the_same_key_and_the_same_modifiers()
    {
        var f6 = HotkeyBinding.FromKey(0x75);

        Assert.True(f6.SameAs(HotkeyBinding.FromKey(0x75)));
        Assert.False(f6.SameAs(new HotkeyBinding { Vk = 0x75, Ctrl = true }));
        Assert.False(f6.SameAs(HotkeyBinding.FromKey(0x76)));
        Assert.False(f6.SameAs(null));
    }

    [Fact]
    public void IsSet_is_false_without_a_key()
    {
        Assert.False(new HotkeyBinding { Ctrl = true }.IsSet);
        Assert.True(HotkeyBinding.FromKey(0x41).IsSet);
    }
}

public class LoopActionTests
{
    [Theory]
    [InlineData(250, IntervalUnit.Milliseconds, 250)]
    [InlineData(2, IntervalUnit.Seconds, 2_000)]
    [InlineData(1.5, IntervalUnit.Minutes, 90_000)]
    [InlineData(24, IntervalUnit.Hours, 86_400_000)]
    public void IntervalMs_converts_the_unit(double value, IntervalUnit unit, double expectedMs)
    {
        var action = new LoopAction { IntervalValue = value, IntervalUnit = unit };

        Assert.Equal(expectedMs, action.IntervalMs);
    }

    [Fact]
    public void The_interval_limits_are_10_ms_and_24_hours()
    {
        Assert.Equal(10, LoopAction.MinIntervalMs);
        Assert.Equal(24 * 60 * 60 * 1000, LoopAction.MaxIntervalMs);
    }
}
