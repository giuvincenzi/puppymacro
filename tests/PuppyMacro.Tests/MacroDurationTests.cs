using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>How long a macro with Ifs takes (<see cref="DurationRange"/>): the editor's totals and the macro's card show it.</summary>
public class MacroDurationTests
{
    /// <summary>
    /// An If that waits "As long as it takes" never plays its Else: the condition window says "The macro waits until they are
    /// true or you stop it." Its Then holds a Press key held 1 s, its Else is empty (Run the Else actions, the default). The
    /// shortest round is then the Then's 1 s, not 0 ms: the editor says "1 s or more", the card "1 s".
    /// </summary>
    [Fact]
    public void An_if_that_waits_as_long_as_it_takes_is_at_least_its_Then()
    {
        var press = new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41, HoldMs = 1000 };
        var macro = new MacroDefinition
        {
            Name = "Wait",
            Actions =
            {
                new MacroAction
                {
                    Type = MacroActionType.If,
                    If = new MacroIf
                    {
                        Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" } },
                        Wait = ConditionWait.Forever,
                        OnFail = ConditionFail.Else,
                        Then = { press },
                    },
                },
            },
        };

        DurationRange range = macro.Duration;

        Assert.True(range.Endless);
        Assert.Equal(1000, range.MinMs);
    }
}
