using System;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The box of one action, the same in a loop row and in a macro's action window (no WPF: only its logic).</summary>
public class ActionBoxViewModelTests
{
    private static string Json(LoopAction action) => CodeJson.Serialize(action);

    [Theory]
    [InlineData(0x41, false, false)]   // a key every 40 ms
    [InlineData(0x01, false, false)]   // a left click
    [InlineData(0x57, true, false)]    // W held down
    [InlineData(0x04, true, false)]    // the middle button held down
    [InlineData(0, false, true)]       // a text
    public void An_older_loop_row_opened_and_saved_unchanged_is_the_same(int vk, bool holdDown, bool text)
    {
        var action = text
            ? new LoopAction { Type = ActionType.Text, Text = "Hello", EnterAfter = true, IntervalValue = 30, IntervalUnit = IntervalUnit.Seconds }
            : new LoopAction { Type = ActionType.Key, KeyVk = vk, HoldDown = holdDown, IntervalValue = 40 };

        LoopAction saved = new ActionBoxViewModel(action).ToLoopAction();

        Assert.Equal(Json(action), Json(saved));
    }

    [Fact]
    public void A_loop_click_shows_the_loop_rows_then_its_own_and_Hold_down_leaves_only_Position()
    {
        var box = new ActionBoxViewModel(new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 40 });

        Assert.Equal(ActionKind.Click, box.Kind);
        Assert.True(box.ShowRun && box.ShowEvery && box.ShowHoldFor && box.ShowModeAfterHold && box.ShowTimes && box.ShowPositionLast);
        Assert.False(box.ShowModeFirst || box.ShowPositionFirst);
        Assert.False(box.PauseEnabled);

        box.Times = 3;
        Assert.True(box.PauseEnabled);

        box.RunIndex = 1; // Hold down
        Assert.True(box.ShowRun && box.ShowPositionLast);
        Assert.False(box.ShowEvery || box.ShowHoldFor || box.ShowModeAfterHold || box.ShowTimes);
    }

    [Fact]
    public void A_macro_click_shows_Position_and_Mode_first_and_no_loop_rows()
    {
        var box = new ActionBoxViewModel(new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 10, Y = 20 });

        Assert.True(box.ShowPositionFirst && box.ShowModeFirst && box.ShowHoldFor && box.ShowTimes);
        Assert.False(box.ShowRun || box.ShowEvery || box.ShowModeAfterHold || box.ShowPositionLast);
    }

    [Theory]
    [InlineData(MacroActionType.KeyDown, false)]
    [InlineData(MacroActionType.KeyUp, false)]
    [InlineData(MacroActionType.PressKey, true)]
    [InlineData(MacroActionType.Scroll, true)]
    public void Key_down_and_Key_up_have_no_options(MacroActionType type, bool hasOptions) =>
        Assert.Equal(hasOptions, new ActionBoxViewModel(new MacroAction { Type = type, Vk = 0x41 }).HasOptions);

    [Fact]
    public void The_box_says_what_is_missing()
    {
        Assert.NotNull(ActionBoxViewModel.NewLoopRow(ActionKind.PressKey).Problem);
        Assert.NotNull(ActionBoxViewModel.NewLoopRow(ActionKind.PasteText).Problem);
        Assert.Null(ActionBoxViewModel.NewLoopRow(ActionKind.Click).Problem);

        var held = new ActionBoxViewModel(new LoopAction { Type = ActionType.Key, Key = new HotkeyBinding { Vk = 0x43, Ctrl = true }, IntervalValue = 40 })
        {
            RunIndex = 1,
        };
        Assert.Contains("Hold down", held.Problem);

        var point = ActionBoxViewModel.NewLoopRow(ActionKind.Click);
        point.FixedPoint = true;
        point.X = double.NaN;
        Assert.Contains("position of each Click", point.Problem);

        var mouseKey = new ActionBoxViewModel(new MacroAction { Type = MacroActionType.PressKey, Vk = 0x01 });
        Assert.Contains("mouse buttons", mouseKey.Problem);
    }

    [Fact]
    public void A_loop_row_keeps_its_new_options()
    {
        var box = ActionBoxViewModel.NewLoopRow(ActionKind.Click);
        box.ClickModeIndex = 1;
        box.Times = 2;
        box.PauseMs = 200;
        box.HoldMs = 80;
        box.FixedPoint = true;
        box.X = 640;
        box.Y = 360;

        LoopAction action = box.ToLoopAction();

        Assert.True(action.IsDoubleClick);
        Assert.Equal(2, action.Repeat);
        Assert.Equal(200, action.RepeatPauseMs);
        Assert.Equal(80, action.HoldMs);
        Assert.Equal(640, action.X);
        Assert.Equal(360, action.Y);
        Assert.Equal("Left double click 2 times, at 640, 360, every 40 ms", box.Summary);
    }

    [Fact]
    public void A_macro_action_keeps_what_the_box_does_not_show()
    {
        var original = new MacroAction { Type = MacroActionType.KeyDown, Vk = KeyNames.VK_LCONTROL, HoldMs = 77, Name = "Hold Ctrl", DelayMs = 120, GroupId = Guid.NewGuid() };

        MacroAction saved = new ActionBoxViewModel(original).ToMacroAction(original);

        Assert.Equal(MacroJson.Serialize(original), MacroJson.Serialize(saved));
    }

    [Fact]
    public void The_code_schema_describes_the_modifiers_of_a_loop_action()
    {
        JsonObject schema = JsonNode.Parse(CodeSchema.ForLoop(Guid.NewGuid()))!.AsObject();
        string text = schema.ToJsonString();

        Assert.Contains("Ctrl held with the key", text);
        Assert.Contains("the fixed point clicked", text);
    }
}
