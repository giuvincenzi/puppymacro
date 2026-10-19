using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The condition window's texts for values at their edges.</summary>
public class ConditionViewModelEdgesTests
{
    private static IfViewModel UpTo(double seconds) => new(new MacroIf
    {
        Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" } },
        Wait = ConditionWait.UpTo,
        WaitSeconds = seconds,
    }, null);

    [Theory]
    [InlineData(0.5, "0.5")]
    [InlineData(0.4, "0.4")]
    [InlineData(2.5, "2.5")]
    [InlineData(1.5, "1.5")]
    public void If_not_true_says_the_seconds_it_waits(double seconds, string shown)
    {
        // The window takes tenths of a second (0.1 to 86400): "After waiting 0 seconds." for 0.4 s is wrong.
        Assert.Contains(shown, UpTo(seconds).OnFailDescription);
    }

    [Fact]
    public void If_not_true_says_whole_seconds_as_they_are()
    {
        Assert.Equal("After waiting 1 second.", UpTo(1).OnFailDescription);
        Assert.Equal("After waiting 30 seconds.", UpTo(30).OnFailDescription);
    }

    /// <summary>
    /// An If finds "Button" and its Then holds a row linked to a component whose Point argument is "Where Button was found"
    /// ({"Found": the condition's Id}, what the linked row's window and Save as component write). The condition window keeps a
    /// condition an action in Then goes to: it can't be removed nor turned to "is not there" (ConditionsEditor asks
    /// <see cref="IfViewModel.UsedByThen"/>). The linked row goes there as much as a click does, so the condition must be
    /// protected too; otherwise the user removes it and the macro can't be saved any more.
    /// </summary>
    [Fact]
    public void A_condition_a_linked_row_in_Then_goes_to_is_used_by_Then()
    {
        var button = new MacroCondition { Kind = ConditionKind.Image, Name = "Button", Similarity = 90 };
        var linked = new MacroAction
        {
            Type = MacroActionType.Component,
            ComponentId = Guid.NewGuid(),
            Arguments = new Dictionary<string, JsonNode?> { ["Slot"] = ComponentValues.FoundArgument(button.Id, 0, 0) },
        };
        var model = new IfViewModel(new MacroIf { Conditions = { button }, Then = { linked } }, null);

        Assert.Contains(button.Id, model.UsedByThen);
    }

    /// <summary>As above, with the place given to a Component condition of an If inside Then (its Point argument).</summary>
    [Fact]
    public void A_condition_a_component_condition_inside_Then_uses_is_used_by_Then()
    {
        var button = new MacroCondition { Kind = ConditionKind.Image, Name = "Button", Similarity = 90 };
        var inner = new MacroAction
        {
            Type = MacroActionType.If,
            If = new MacroIf
            {
                Conditions =
                {
                    new MacroCondition
                    {
                        Kind = ConditionKind.Component, Name = "Rare", ComponentId = Guid.NewGuid(),
                        Arguments = new Dictionary<string, JsonNode?> { ["Slot"] = ComponentValues.FoundArgument(button.Id, 5, 0) },
                    },
                },
            },
        };
        var model = new IfViewModel(new MacroIf { Conditions = { button }, Then = { inner } }, null);

        Assert.Contains(button.Id, model.UsedByThen);
    }
}
