using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The edges of components as functions: a text of only spaces for a Text condition, a found place moved past the screen's
/// limits by an offset the checks accept, the names Save as component makes at the 40-letter limit, and eleven levels of
/// Component conditions (one more than the limit).
/// </summary>
public class ComponentBoundariesTests
{
    [Fact]
    public void A_text_of_only_spaces_for_a_Text_condition_is_refused_by_the_call_as_by_the_condition()
    {
        var text = Bound(ComponentGenerator.TextCondition("Label"), ("Text", "Word"));
        var reads = Comp("Reads", new[] { Param("Word", ParameterType.Text, Text("Start")) }, If(text, Press()));
        var macro = Macro("D4", Call(reads, ("Word", Text("   "))));

        // What would play: the condition with the call's text, which a condition written in a macro can't have.
        var frame = ComponentPrograms.Evaluate(ComponentPrograms.Prepare(new[] { reads }).Find(reads.Id)!, macro.Actions[0].Arguments,
            FrameArguments.None, new Dictionary<Guid, Point>(), out _)!;
        var played = ComponentPrograms.Bound(text, frame)!.Clone();
        played.Bind = null;
        Assert.NotEmpty(MacroProblems(Macro("Played", If(played, Press()))));

        Assert.NotEmpty(Problems(new[] { reads }, macro));
    }

    [Fact]
    public void A_found_place_moved_past_the_screen_s_limits_never_plays_the_component_s_example()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(3912, 1056), ("Point", "Slot")));
        var seen = ImageFinder("Seen");
        var row = Call(strip, ("Slot", Found(seen.Id, 100_000, 0)));
        Assert.Empty(Problems(new[] { strip }, Macro("D4", If(seen, row)))); // the offset is inside its own limits

        var found = new Dictionary<Guid, Point> { [seen.Id] = new Point(5000, 10) };
        var frame = ComponentPrograms.Evaluate(ComponentPrograms.Prepare(new[] { strip }).Find(strip.Id)!, row.Arguments,
            FrameArguments.None, found, out _);
        if (frame == null)
            return; // skipped: as a "Where … was found" with no place
        var click = ComponentPrograms.Bound(strip.Actions[0], frame)!;
        Assert.False((click.X, click.Y) == (3912, 1056),
            $"the call gives the point {frame["Slot"]?.ToJsonString()}, but the click plays at the component's example 3912, 1056");
    }

    [Theory]
    [InlineData(false)] // two places of one condition (two offsets)
    [InlineData(true)] // a place and a parameter of the component around with the same 40-letter name
    public void Save_as_component_makes_valid_names_at_the_40_letter_limit(bool withOuter)
    {
        string longName = new('A', 45); // "AAAA…" made a 40-letter name
        var seen = ImageFinder(longName);
        var first = Click(found: seen.Id);
        var second = Click(found: seen.Id);
        second.FoundOffsetX = 10;
        var outer = new List<ComponentParameter> { HoldParam(new string('A', 40)) };
        var selected = withOuter ? new[] { first, Bound(Press(0x87, 30), ("HoldMs", new string('A', 40))) } : new[] { first, second };

        var (actions, parameters, _) = ComponentEdits.Lift(selected, withOuter ? outer : null, new Dictionary<Guid, string> { [seen.Id] = longName });
        var component = Comp("Two places", parameters, actions.ToArray());

        Assert.Equal(2, parameters.Count);
        Assert.All(parameters, p => Assert.True(p.Name.Length <= 40, $"\"{p.Name}\" has {p.Name.Length} letters"));
        Assert.Empty(ComponentProblems(component));
    }

    [Fact]
    public void Eleven_levels_of_Component_conditions_are_refused()
    {
        var pixel = Bound(Pixel(), ("Point", "Slot"));
        var chain = new List<LibraryComponent> { Comp("Level 1", new[] { SlotParam() }, If(pixel, Press())) };
        chain[0].Returns = new ComponentReturns { Found = pixel.Id };
        for (int level = 2; level <= ComponentSignatures.MaxDepth + 1; level++)
        {
            var condition = CallCondition(chain[^1], $"Level {level - 1}", ("Slot", From("Slot")));
            var component = Comp($"Level {level}", new[] { SlotParam() }, If(condition, Press()));
            component.Returns = new ComponentReturns { Found = condition.Id };
            chain.Add(component);
        }

        Assert.Equal(ComponentSignatures.MaxDepth + 1, new ComponentSignatures(chain).Height(chain[^1].Id));
        Assert.Contains(Problems(chain), p => p.StartsWith("Level 11: Actions:"));
        Assert.DoesNotContain(Problems(chain), p => p.StartsWith("Level 10:"));
    }
}
