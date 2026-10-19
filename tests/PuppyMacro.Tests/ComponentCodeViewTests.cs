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
