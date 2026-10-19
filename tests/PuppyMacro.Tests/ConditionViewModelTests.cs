using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The condition window's model: what counts as a change of a condition (the window checks it again on the screen).</summary>
public class ConditionViewModelTests
{
    private static (IfViewModel Block, ConditionViewModel Condition) Pixel()
    {
        var block = new IfViewModel(new MacroIf
        {
            Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 10, Y = 20, Color = "#3BD16F", Tolerance = 5 } },
        }, null);
        return (block, block.Conditions[0]);
    }

    [Fact]
    public void Showing_a_check_or_the_same_values_is_no_change_and_never_checks_again()
    {
        var (block, condition) = Pixel();
        int changes = 0;
        block.ConditionChanged += _ => changes++;

        condition.ShowReport(new ConditionReport(false, Color: "#000000"));
        condition.ShowReport(new ConditionReport(true, Color: "#3BD16F"));
        condition.IsExpanded = !condition.IsExpanded;
        condition.Tolerance = 5;
        condition.X = 10;

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Changing_what_it_checks_is_a_change()
    {
        var (block, condition) = Pixel();
        int changes = 0;
        block.ConditionChanged += _ => changes++;

        condition.Tolerance = 10;
        Assert.True(changes > 0, "a new tolerance is no change");
        changes = 0;
        condition.Color = "#000000";
        Assert.True(changes > 0, "a new color is no change");

        Assert.Equal("#000000", condition.ToModel().Color);
        Assert.Equal(10, condition.ToModel().Tolerance);
    }

    [Fact]
    public void Not_turns_what_it_saw_into_the_opposite_and_is_saved()
    {
        var (block, condition) = Pixel();
        int changes = 0;
        block.ConditionChanged += _ => changes++;

        condition.NotIndex = 1;

        Assert.True(changes > 0, "Not is no change");
        Assert.True(condition.ToModel().Not);
        condition.ShowReport(new ConditionReport(false, Color: "#000000"));
        Assert.True(condition.ResultMet);
        condition.ShowReport(new ConditionReport(true, Color: "#3BD16F"));
        Assert.False(condition.ResultMet);
        condition.ShowReport(new ConditionReport(false, NoMonitor: true));
        Assert.False(condition.ResultMet);
    }
}
