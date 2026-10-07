using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class MacroBuilderTests
{
    private const int KeyA = 0x41, KeyB = 0x42, LButton = 0x01;

    private static RawEvent Key(bool down, int vk, double t) => new(down ? RawEventKind.KeyDown : RawEventKind.KeyUp, vk, 0, 0, 0, t);
    private static RawEvent Button(bool down, int x, int y, double t) => new(down ? RawEventKind.ButtonDown : RawEventKind.ButtonUp, LButton, x, y, 0, t);
    private static RawEvent Move(int x, int y, double t) => new(RawEventKind.Move, 0, x, y, 0, t);
    private static RawEvent Wheel(int delta, double t) => new(RawEventKind.Wheel, 0, 0, 0, delta, t);

    [Fact]
    public void A_key_down_and_up_becomes_one_press_with_its_hold_time_and_delays()
    {
        List<MacroAction> actions = MacroBuilder.Build(new[]
        {
            Key(true, KeyA, 100), Key(false, KeyA, 140),
            Key(true, KeyB, 300), Key(false, KeyB, 320),
        });

        Assert.Equal(2, actions.Count);
        Assert.Equal(MacroActionType.PressKey, actions[0].Type);
        Assert.Equal(KeyA, actions[0].Vk);
        Assert.Equal(40, actions[0].HoldMs);
        Assert.Equal(0, actions[0].DelayMs);
        Assert.Equal(160, actions[1].DelayMs); // from the end of the first press (140) to 300
    }

    [Fact]
    public void Overlapping_keys_stay_separate_down_and_up_steps()
    {
        List<MacroAction> actions = MacroBuilder.Build(new[]
        {
            Key(true, KeyA, 0), Key(true, KeyB, 10), Key(false, KeyB, 20), Key(false, KeyA, 30),
        });

        Assert.Equal(
            new[] { MacroActionType.KeyDown, MacroActionType.PressKey, MacroActionType.KeyUp },
            actions.Select(a => a.Type));
        Assert.Equal(KeyA, actions[0].Vk);
        Assert.Equal(KeyB, actions[1].Vk);
    }

    [Fact]
    public void A_click_in_place_becomes_a_click_and_two_quick_clicks_a_double_click()
    {
        List<MacroAction> single = MacroBuilder.Build(new[] { Button(true, 100, 200, 0), Button(false, 101, 201, 30) });
        List<MacroAction> twice = MacroBuilder.Build(new[]
        {
            Button(true, 100, 200, 0), Button(false, 100, 200, 30),
            Button(true, 100, 200, 150), Button(false, 100, 200, 180),
        });

        MacroAction click = Assert.Single(single);
        Assert.Equal(MacroActionType.Click, click.Type);
        Assert.Equal(1, click.ClickCount);
        Assert.Equal((100, 200), (click.X, click.Y));
        Assert.Equal(2, Assert.Single(twice).ClickCount);
    }

    [Fact]
    public void Two_clicks_far_apart_in_time_stay_two_clicks()
    {
        List<MacroAction> actions = MacroBuilder.Build(new[]
        {
            Button(true, 100, 200, 0), Button(false, 100, 200, 30),
            Button(true, 100, 200, 1000), Button(false, 100, 200, 1030),
        });

        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(1, a.ClickCount));
    }

    [Fact]
    public void A_drag_becomes_button_down_and_button_up()
    {
        List<MacroAction> actions = MacroBuilder.Build(new[] { Button(true, 100, 100, 0), Button(false, 300, 100, 200) });

        Assert.Equal(new[] { MacroActionType.MouseDown, MacroActionType.MouseUp }, actions.Select(a => a.Type));
        Assert.Equal(300, actions[1].X);
    }

    [Fact]
    public void Quick_wheel_notches_in_the_same_direction_become_one_scroll()
    {
        List<MacroAction> actions = MacroBuilder.Build(new[]
        {
            Wheel(-120, 0), Wheel(-120, 50), Wheel(-120, 100),
            Wheel(120, 1000),
        });

        Assert.Equal(2, actions.Count);
        Assert.Equal(ScrollDirection.Down, actions[0].ScrollDirection);
        Assert.Equal(3, actions[0].ScrollSteps);
        Assert.Equal(ScrollDirection.Up, actions[1].ScrollDirection);
        Assert.Equal(1, actions[1].ScrollSteps);
    }

    [Fact]
    public void One_move_is_a_move_to_and_a_straight_path_keeps_only_its_ends()
    {
        MacroAction moveTo = Assert.Single(MacroBuilder.Build(new[] { Move(10, 20, 0) }));
        MacroAction path = Assert.Single(MacroBuilder.Build(new[] { Move(0, 0, 0), Move(10, 10, 10), Move(20, 20, 20), Move(30, 30, 30) }));

        Assert.Equal(MacroActionType.MoveTo, moveTo.Type);
        Assert.Equal((10, 20), (moveTo.X, moveTo.Y));
        Assert.Equal(MacroActionType.MovePath, path.Type);
        Assert.Equal(new[] { (0, 0), (30, 30) }, path.Path.Select(p => (p.X, p.Y)));
        Assert.Equal(30, path.Path[^1].T);
    }

    [Fact]
    public void A_path_keeps_its_corners()
    {
        MacroAction path = Assert.Single(MacroBuilder.Build(new[]
        {
            Move(0, 0, 0), Move(50, 0, 10), Move(100, 0, 20), Move(100, 50, 30), Move(100, 100, 40),
        }));

        Assert.Equal(new[] { (0, 0), (100, 0), (100, 100) }, path.Path.Select(p => (p.X, p.Y)));
    }
}
