using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The Code views and the windows' checks of components and calls, from sides the other classes don't cover: text a person
/// or an AI app can write (a value with a field written twice), and what an action's window offers against what its macro's
/// checks then accept.
/// </summary>
public class ComponentCodeViewTests
{
    /// <summary>"Health low": its If looks at a pixel and it returns that pixel, so a call to it finds no place.</summary>
    private static LibraryComponent PixelReturner()
    {
        var pixel = Pixel(5, 5, "#FF0000");
        var component = Comp("Health low", If(pixel, Press()));
        component.Purpose = ComponentPurpose.State;
        component.Returns = new ComponentReturns { Found = pixel.Id };
        return component;
    }

    /// <summary>
    /// A macro's Code view where a call's Point value has "X" written twice ({"X": 1, "X": 2, "Y": 3}), as a person or an
    /// AI app can type it: a problem of the Code view, never an exception out of it.
    /// </summary>
    [Fact]
    public void A_value_with_a_field_written_twice_is_a_problem_never_a_crash()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        var macro = Macro("Strip all", Call(strip, ("Slot", Point(1, 2))));
        string text = MacroJson.Serialize(macro).Replace("\"X\": 1,", "\"X\": 1, \"X\": 7,");
        Assert.Contains("\"X\": 7", text);

        List<CodeProblem>? problems = null;
        var thrown = Record.Exception(() => MacroJson.Parse(text, macro.Id, (_, _) => null, out problems,
            components: new ComponentSignatures(new[] { strip })));

        Assert.Null(thrown);
        Assert.NotEmpty(problems!);
    }

    /// <summary>
    /// The same in a component's own Code view: an Example written with a field twice is a problem, never an exception.
    /// </summary>
    [Fact]
    public void An_example_with_a_field_written_twice_is_a_problem_never_a_crash()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        ComponentEdits.WriteExamples(strip);
        string text = CodeJson.Serialize(strip).Replace("\"X\": 3912,", "\"X\": 3912, \"X\": 1,");
        Assert.Contains("\"X\": 1,", text);

        List<CodeProblem>? problems = null;
        var thrown = Record.Exception(() => MacroJson.ParseComponent(text, strip.Id, out problems, null, Array.Empty<LibraryComponent>()));

        Assert.Null(thrown);
        Assert.NotEmpty(problems!);
    }

    /// <summary>
    /// An If whose condition is a Component condition calling "Health low" (it returns a pixel, which finds no place), and a
    /// click in its Then. The action window offers that condition in "Where … was found" (MacroJson.FoundAt) and its Code
    /// view accepts a click that goes there, while the macro's own checks (its Code view, its Save, the Library's) refuse
    /// it: the action window and the macro must agree.
    /// </summary>
    [Fact]
    public void The_action_window_offers_no_place_its_macro_then_refuses()
    {
        var low = PixelReturner();
        var signatures = new ComponentSignatures(new[] { low });
        var condition = CallCondition(low, "Low");
        var click = Click(10, 20, condition.Id);
        var block = If(condition, click);
        var macro = Macro("Heal", block);

        // The macro's checks: the click can't go where a pixel was "found".
        var macroProblems = MacroProblems(macro, low);
        Assert.NotEmpty(macroProblems);

        // The action window for the click (in the If's Then): what it offers, and what its Code view accepts.
        var offered = MacroJson.FoundAt(macro.Actions, block.If!.Then, signatures);
        MacroJson.ParseAction(MacroJson.Serialize(click), new Guid?[] { null }, new Dictionary<Guid, string>(), out var actionProblems,
            offered, null, signatures);

        // Expected: neither offered nor accepted (false, true). Observed: offered and accepted (true, false).
        Assert.Equal((false, true), (offered.ContainsKey(condition.Id), actionProblems.Count > 0));
    }

    /// <summary>The passing side: a Component condition whose component returns an image is offered and accepted by both.</summary>
    [Fact]
    public void A_component_condition_that_returns_an_image_is_a_place_for_both()
    {
        var bar = ImageFinder("Health bar");
        var finder = Comp("Find bar", If(bar, Press()));
        finder.Returns = new ComponentReturns { Found = bar.Id };
        var signatures = new ComponentSignatures(new[] { finder });
        var condition = CallCondition(finder, "Bar");
        var click = Click(10, 20, condition.Id);
        var block = If(condition, click);
        var macro = Macro("Heal", block);

        Assert.Empty(MacroProblems(macro, finder));
        var offered = MacroJson.FoundAt(macro.Actions, block.If!.Then, signatures);
        MacroJson.ParseAction(MacroJson.Serialize(click), new Guid?[] { null }, new Dictionary<Guid, string>(), out var actionProblems,
            offered, null, signatures);
        Assert.Contains(condition.Id, offered.Keys);
        Assert.Empty(actionProblems);
    }
}
