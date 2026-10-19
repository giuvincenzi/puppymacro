using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>A recording's edge cases: what Windows sends while a key is held, a long spin of the wheel.</summary>
public class MacroBuilderEdgesTests
{
    private const int KeyA = 0x41;

    private static RawEvent Key(bool down, int vk, double t) => new(down ? RawEventKind.KeyDown : RawEventKind.KeyUp, vk, 0, 0, 0, t);

    /// <summary>The recorded actions as a macro the Code view checks.</summary>
    private static List<CodeProblem> Problems(List<MacroAction> actions)
    {
        var macro = new MacroDefinition { Name = "Recorded", Actions = actions };
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out List<CodeProblem> problems);
        return problems;
    }

    [Fact]
    public void A_long_spin_of_the_wheel_is_a_macro_the_code_view_accepts()
    {
        // A free-spinning wheel: 1500 notches down, one every 2 ms, all within the 300 ms that make one Scroll.
        var events = Enumerable.Range(0, 1500).Select(i => new RawEvent(RawEventKind.Wheel, 0, 0, 0, -120, i * 2)).ToList();

        List<MacroAction> actions = MacroBuilder.Build(events);

        Assert.Equal(1500, actions.Where(a => a.Type == MacroActionType.Scroll).Sum(a => a.ScrollSteps));
        Assert.Empty(Problems(actions));
    }

    [Fact]
    public void A_recording_of_keys_clicks_moves_and_scrolls_is_a_macro_the_code_view_accepts()
    {
        var events = new List<RawEvent>
        {
            Key(true, KeyA, 0), Key(false, KeyA, 0), // a press in the same millisecond: held the shortest time
            new(RawEventKind.Move, 0, 10, 10, 0, 50),
            new(RawEventKind.Move, 0, 20, 30, 0, 60),
            new(RawEventKind.Move, 0, 90, 30, 0, 70),
            new(RawEventKind.ButtonDown, KeyNames.VK_LBUTTON, 90, 30, 0, 100),
            new(RawEventKind.ButtonUp, KeyNames.VK_LBUTTON, 90, 30, 0, 100),
            new(RawEventKind.ButtonDown, KeyNames.VK_LBUTTON, 91, 31, 0, 200),
            new(RawEventKind.ButtonUp, KeyNames.VK_LBUTTON, 91, 31, 0, 220),
            new(RawEventKind.ButtonDown, KeyNames.VK_RBUTTON, 0, 0, 0, 300),
            new(RawEventKind.Move, 0, 50, 50, 0, 310),
            new(RawEventKind.ButtonUp, KeyNames.VK_RBUTTON, 50, 50, 0, 320),
            new(RawEventKind.HWheel, 0, 0, 0, 240, 400),
            new(RawEventKind.Wheel, 0, 0, 0, 30, 500), // a touchpad's small step
            Key(true, KeyA, 600), Key(false, KeyA, 600 + MacroJson.HoldMs.Max + 1), // held longer than a Press key can
        };

        List<MacroAction> actions = MacroBuilder.Build(events);

        Assert.Equal(2, actions.Single(a => a.Type == MacroActionType.Click).ClickCount);
        Assert.Equal(1, actions.Single(a => a.Type == MacroActionType.PressKey).HoldMs);
        Assert.Empty(Problems(actions));
    }
}
