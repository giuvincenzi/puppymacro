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
    public void SameAs_needs_the_same_sides_but_ignores_the_side_of_a_modifier_not_held()
    {
        var leftCtrlA = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left };

        Assert.True(leftCtrlA.SameAs(new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left }));
        Assert.False(leftCtrlA.SameAs(new HotkeyBinding { Vk = 0x41, Ctrl = true }));
        Assert.True(HotkeyBinding.FromKey(0x41).SameAs(new HotkeyBinding { Vk = 0x41, AltSide = ModifierSide.Right }));
    }

    [Fact]
    public void Matches_takes_the_sides_a_hotkey_accepts()
    {
        var eitherCtrlA = new HotkeyBinding { Vk = 0x41, Ctrl = true };
        var leftCtrlA = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left };
        var pressedLeft = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left };
        var pressedRight = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Right };
        var pressedBoth = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Any };

        Assert.True(eitherCtrlA.Matches(pressedLeft));
        Assert.True(eitherCtrlA.Matches(pressedRight));
        Assert.True(eitherCtrlA.Matches(pressedBoth));
        Assert.True(leftCtrlA.Matches(pressedLeft));
        Assert.False(leftCtrlA.Matches(pressedRight));
        Assert.False(leftCtrlA.Matches(pressedBoth));
        // Exact: other modifiers or none do not match.
        Assert.False(leftCtrlA.Matches(HotkeyBinding.FromKey(0x41)));
        Assert.False(HotkeyBinding.FromKey(0x41).Matches(pressedLeft));
        Assert.False(new HotkeyBinding().Matches(new HotkeyBinding()));
    }

    [Fact]
    public void Overlaps_unless_a_modifier_is_on_opposite_sides()
    {
        var left = new HotkeyBinding { Vk = 0x41, Alt = true, AltSide = ModifierSide.Left };
        var right = new HotkeyBinding { Vk = 0x41, Alt = true, AltSide = ModifierSide.Right };
        var either = new HotkeyBinding { Vk = 0x41, Alt = true };

        Assert.False(left.Overlaps(right));
        Assert.True(left.Overlaps(either));
        Assert.True(either.Overlaps(right));
        Assert.True(left.Overlaps(left.Clone()));
        Assert.False(left.Overlaps(HotkeyBinding.FromKey(0x41)));
        Assert.False(left.Overlaps(null));
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

public class MacroTestTests
{
    [Fact]
    public void ForTest_plays_a_copy_of_the_action_alone_right_away_once_at_normal_speed()
    {
        var action = new MacroAction
        {
            Type = MacroActionType.PressKey,
            Vk = 0x87, // F24
            DelayMs = 500,
            Repeat = 3,
            RepeatPauseMs = 20,
            GroupId = System.Guid.NewGuid(),
        };

        MacroDefinition test = MacroDefinition.ForTest(action);

        MacroAction played = Assert.Single(test.Actions);
        Assert.NotSame(action, played);
        Assert.Equal(0, played.DelayMs);
        Assert.Null(played.GroupId);
        Assert.Equal(0x87, played.Vk);
        Assert.Equal(3, played.Repeat); // the action's own repeats are part of what is tested
        Assert.Equal(20, played.RepeatPauseMs);
        Assert.Equal(RepeatMode.Once, test.Repeat);
        Assert.Equal(1, test.Speed);
        Assert.False(test.SoundEnabled);
        Assert.Empty(test.Groups);
        Assert.Equal(500, action.DelayMs); // the edited action is not changed
    }
}
