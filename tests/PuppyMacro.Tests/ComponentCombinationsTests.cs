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
/// Combinations the other tests take one at a time: every kind of argument (a value, a default, {"From"}, {"Found"}) in
/// every kind of call (a linked row, a Component condition) in a macro and in a component, checked and played; renames
/// and removals in one guided update (a name taken by a rename after its parameter went, two names swapped, a chain of
/// renames); a found place given to a component that passes it on, unlinked; and the four ways Not on a Component
/// condition and on what it returns combine for a "Where … was found".
/// </summary>
public class ComponentCombinationsTests
{
    private static readonly Dictionary<Guid, Point> Nothing = new();

    public enum ArgumentKind { Value, LeftOut, From, Found }

    /// <summary>A component with a Point parameter (default 5, 5) bound to the pixel it returns: a row and a condition both.</summary>
    private static LibraryComponent Target()
    {
        var pixel = Bound(Pixel(1, 1), ("Point", "Slot"));
        var target = Comp("Target", new[] { Param("Slot", ParameterType.Point, Point(1, 1), Point(5, 5)) }, If(pixel, Press()));
        target.Returns = new ComponentReturns { Found = pixel.Id };
        return target;
    }

    private static (string Name, JsonNode? Value)[] Given(ArgumentKind kind, Guid seen) => kind switch
    {
        ArgumentKind.Value => new[] { ("Slot", (JsonNode?)Point(11, 12)) },
        ArgumentKind.LeftOut => Array.Empty<(string, JsonNode?)>(),
        ArgumentKind.From => new[] { ("Slot", (JsonNode?)From("Where")) },
        _ => new[] { ("Slot", (JsonNode?)Found(seen, 3, 4)) },
    };

    public static TheoryData<ArgumentKind, bool, bool> Calls()
    {
        var data = new TheoryData<ArgumentKind, bool, bool>();
        foreach (var kind in Enum.GetValues<ArgumentKind>())
            foreach (bool asCondition in new[] { false, true })
                foreach (bool inComponent in new[] { false, true })
                    data.Add(kind, asCondition, inComponent);
        return data;
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public void Every_kind_of_argument_in_every_kind_of_call_is_checked_and_played_alike(ArgumentKind kind, bool asCondition, bool inComponent)
    {
        var target = Target();
        var seen = ImageFinder("Seen");
        var arguments = Given(kind, seen.Id);
        MacroAction call = asCondition ? If(CallCondition(target, "Target there", arguments), Press()) : Call(target, arguments);
        var around = If(seen, call);
        List<string> problems;
        if (inComponent)
        {
            var caller = Comp("Caller", new[] { SlotParam("Where") }, around);
            problems = Problems(new[] { target, caller });
        }
        else
            problems = Problems(new[] { target }, Macro("Caller", around));

        bool accepted = kind != ArgumentKind.From || inComponent;
        Assert.True(accepted == (problems.Count == 0), $"{kind}, {(asCondition ? "condition" : "row")}, {(inComponent ? "component" : "macro")}: {string.Join("; ", problems)}");
        if (!accepted)
            return;

        var programs = ComponentPrograms.Prepare(new[] { target });
        var frame = new FrameArguments(new Dictionary<string, JsonNode> { ["Where"] = Point(21, 22) });
        var found = new Dictionary<Guid, Point> { [seen.Id] = new Point(100, 200) };
        var expected = kind switch
        {
            ArgumentKind.Value => (11, 12),
            ArgumentKind.LeftOut => (5, 5),
            ArgumentKind.From => (21, 22),
            _ => (103, 204),
        };
        var given = asCondition ? call.If!.Conditions[0].Arguments : call.Arguments;
        var values = ComponentPrograms.Evaluate(programs.Find(target.Id)!, given, frame, found, out _)!;
        Assert.True(JsonNode.DeepEquals(Point(expected.Item1, expected.Item2), values["Slot"]));
        if (asCondition)
        {
            var effective = programs.Effective(call.If!.Conditions[0], frame, found)!;
            Assert.Equal(expected, (effective.X, effective.Y));
        }
    }

    [Theory]
    [InlineData(ParameterType.Number)] // the same kind: the call keeps a value of the right kind, the wrong one
    [InlineData(ParameterType.Text)] // another kind: the call is broken after the update
    public void Renaming_a_parameter_to_the_name_of_one_taken_out_keeps_the_renamed_value(ParameterType second)
    {
        var hold = HoldParam("Hold");
        var other = second == ParameterType.Number ? HoldParam("Wait") : Param("Wait", ParameterType.Text, Text("hi"));
        var before = Comp("Press for", new[] { hold, other }, Bound(Press(), ("HoldMs", "Hold")));
        var macro = Macro("D4", Call(before, ("Hold", Number(45)), ("Wait", second == ParameterType.Number ? Number(900) : Text("hello"))));

        // The editor: Wait taken out, then Hold renamed Wait.
        var after = before.Clone();
        after.Parameters.RemoveAt(1);
        ComponentEdits.Rename(after.Actions, "Hold", "Wait");
        after.Parameters[0].Name = "Wait";
        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { after }, new Dictionary<string, string> { ["Hold"] = "Wait" });
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(),
            update.NeedValue.ToDictionary(p => p.Name, p => p.Example!), after).Macros.Single();

        Assert.Equal(new[] { "Wait" }, changed.Actions[0].Arguments!.Keys);
        Assert.True(JsonNode.DeepEquals(Number(45), changed.Actions[0].Arguments!["Wait"]),
            $"the call gives Wait = {changed.Actions[0].Arguments!["Wait"]?.ToJsonString()}, not the 45 it gave Hold");
        Assert.Empty(Problems(new[] { after }, changed));
    }

    [Fact]
    public void Parameters_that_swap_names_swap_their_values_and_a_chain_of_renames_follows()
    {
        var before = Comp("Press for", new[] { HoldParam("A"), HoldParam("B"), HoldParam("C") },
            Bound(Press(), ("HoldMs", "A")), Bound(Press(), ("DelayMs", "B")), Bound(Press(), ("RepeatPauseMs", "C")));
        var macro = Macro("D4", Call(before, ("A", Number(1)), ("B", Number(2)), ("C", Number(3))));

        var swapped = before.Clone();
        (swapped.Parameters[0].Name, swapped.Parameters[1].Name) = ("B", "A");
        var update = SignatureUpdate.Compare(before, swapped, new[] { macro }, new[] { swapped }, new Dictionary<string, string> { ["A"] = "B", ["B"] = "A" });
        var arguments = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), swapped).Macros.Single().Actions[0].Arguments!;
        Assert.True(JsonNode.DeepEquals(Number(2), arguments["A"]));
        Assert.True(JsonNode.DeepEquals(Number(1), arguments["B"]));
        Assert.True(JsonNode.DeepEquals(Number(3), arguments["C"]));

        var chained = before.Clone();
        (chained.Parameters[0].Name, chained.Parameters[1].Name, chained.Parameters[2].Name) = ("B", "C", "D");
        update = SignatureUpdate.Compare(before, chained, new[] { macro }, new[] { chained },
            new Dictionary<string, string> { ["A"] = "B", ["B"] = "C", ["C"] = "D" });
        arguments = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), chained).Macros.Single().Actions[0].Arguments!;
        Assert.Equal(new[] { "B", "C", "D" }, arguments.Keys.OrderBy(k => k));
        Assert.True(JsonNode.DeepEquals(Number(1), arguments["B"]));
        Assert.True(JsonNode.DeepEquals(Number(2), arguments["C"]));
        Assert.True(JsonNode.DeepEquals(Number(3), arguments["D"]));
        Assert.Empty(update.Removed);
        Assert.Empty(update.NeedValue);
    }

    [Fact]
    public void A_found_place_given_to_a_component_that_passes_it_on_is_unlinked_into_a_macro_that_still_passes()
    {
        var inner = Comp("Click there", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        var rare = Target();
        var outer = Comp("Strip", new[] { SlotParam() },
            Bound(Click(), ("Point", "Slot")),
            Call(inner, ("Slot", From("Slot"))),
            If(CallCondition(rare, "Rare there", ("Slot", From("Slot"))), Press()));
        var seen = ImageFinder("Seen");
        var row = Call(outer, ("Slot", Found(seen.Id, 7, -3)));
        var library = new[] { inner, rare, outer };
        Assert.Empty(Problems(library, Macro("D4", If(seen, row))));

        var unlinked = ComponentEdits.Unlink(row, outer, out string? problem)!;
        Assert.Null(problem);
        Assert.Equal((seen.Id, 7, -3), (unlinked[0].FoundId!.Value, unlinked[0].FoundOffsetX, unlinked[0].FoundOffsetY));
        Assert.Null(unlinked[0].Bind);
        Assert.True(JsonNode.DeepEquals(Found(seen.Id, 7, -3), unlinked[1].Arguments!["Slot"]));
        Assert.True(JsonNode.DeepEquals(Found(seen.Id, 7, -3), unlinked[2].If!.Conditions[0].Arguments!["Slot"]));

        var macro = Macro("D4", If(seen, unlinked.ToArray()));
        Assert.Empty(Problems(library, macro));
        Assert.Empty(MacroProblems(macro, library));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)] // two Nots: the image is there, and found
    public void A_Where_was_found_of_a_Component_condition_follows_its_Nots_together(bool outerNot, bool returnedNot)
    {
        var image = Bound(ImageFinder("Affix", not: returnedNot), ("Images", "Look"));
        var finds = Comp("Affix there", new[] { Param("Look", ParameterType.Image, Images(ImageA)) }, If(image, Press()));
        finds.Returns = new ComponentReturns { Found = image.Id };
        var condition = CallCondition(finds, "Affix", ("Look", Images(ImageB)));
        condition.Not = outerNot;
        var macro = Macro("D4", If(condition, new MacroAction { Type = MacroActionType.Click, Vk = 0x01, FoundId = condition.Id }));

        var played = ComponentPrograms.Prepare(new[] { finds }).Effective(condition, FrameArguments.None, Nothing)!;
        bool findsAPlace = played.GivesPlace;
        Assert.Equal(!(outerNot ^ returnedNot), findsAPlace);
        var problems = MacroProblems(macro, finds);
        Assert.True(findsAPlace == (problems.Count == 0),
            $"the play {(findsAPlace ? "finds" : "finds no")} place, the check says: {(problems.Count == 0 ? "nothing" : string.Join("; ", problems))}");
    }
}
