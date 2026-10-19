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
}
