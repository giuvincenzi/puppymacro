using System;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

// ModifierTracker is static: every test starts and ends from "nothing held".
public class ModifierTrackerTests : IDisposable
{
    private const int LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RShift = 0xA1, LWin = 0x5B, KeyA = 0x41;

    public ModifierTrackerTests() => ModifierTracker.Reset();

    public void Dispose() => ModifierTracker.Reset();

    [Fact]
    public void A_left_or_right_key_down_sets_the_modifier()
    {
        ModifierTracker.Update(RCtrl, isDown: true);

        Assert.True(ModifierTracker.Ctrl);
        Assert.True(ModifierTracker.IsDown(RCtrl));
        Assert.False(ModifierTracker.IsDown(LCtrl));
        Assert.False(ModifierTracker.Alt);
    }

    [Fact]
    public void Ctrl_stays_down_until_both_sides_are_released()
    {
        ModifierTracker.Update(LCtrl, isDown: true);
        ModifierTracker.Update(RCtrl, isDown: true);
        ModifierTracker.Update(LCtrl, isDown: false);

        Assert.True(ModifierTracker.Ctrl);

        ModifierTracker.Update(RCtrl, isDown: false);

        Assert.False(ModifierTracker.Ctrl);
    }

    [Fact]
    public void Keys_that_are_not_modifiers_are_ignored()
    {
        ModifierTracker.Update(KeyA, isDown: true);

        Assert.False(ModifierTracker.IsDown(KeyA));
        Assert.Empty(ModifierTracker.HeldKeys());
    }

    [Fact]
    public void HeldKeys_lists_the_specific_keys_held()
    {
        ModifierTracker.Update(LAlt, isDown: true);
        ModifierTracker.Update(RShift, isDown: true);
        ModifierTracker.Update(LWin, isDown: true);

        Assert.Equal(new[] { LAlt, RShift, LWin }, ModifierTracker.HeldKeys());
    }

    [Fact]
    public void Reset_releases_everything()
    {
        ModifierTracker.Update(LCtrl, isDown: true);
        ModifierTracker.Update(LAlt, isDown: true);

        ModifierTracker.Reset();

        Assert.False(ModifierTracker.Ctrl);
        Assert.False(ModifierTracker.Alt);
    }
}
