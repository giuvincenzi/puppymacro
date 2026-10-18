using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The checks of components as functions (ComponentChecks, ComponentSignatures, MacroJson.ParseComponent), each case that
/// passes and each that fails: Bind (outside a component, a slot that doesn't exist or isn't used, a parameter that doesn't
/// exist or is of another type or unit); calls (no component, a missing one, a condition whose component returns nothing,
/// arguments missing, unknown, of the wrong type or outside the limits of the fields they go to, {"From"} in a macro, of an
/// unknown parameter or of another type, {"Found"} outside its If, of a Not condition, for a parameter that isn't a point,
/// with offsets too far, written wrong); the limits rising to the signature, through Bind and through {"From"} down the calls;
/// a component's own parameters (names, units, examples, defaults) and Returns; loops of calls and the depth; a "Where … was
/// found" of a Component condition; everything at once, and which macros can't play.
/// </summary>
public class ComponentChecksTests
{
    private static string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Between((double Min, double Max) range) =>
        F(Strings.Code_Between, range.Min.ToString("0.##", CultureInfo.CurrentCulture), range.Max.ToString("0.##", CultureInfo.CurrentCulture));

    /// <summary>"Strip one slot": checks the pixel at Slot, then clicks Slot and holds it Hold (30 ms when left out).</summary>
    private static LibraryComponent Strip() => Comp("Strip one slot",
        new[] { SlotParam(), HoldParam(@default: Number(30)) },
        If(Bound(Pixel(3912, 1056), ("Point", "Slot")),
            Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold"))));

    // ================= Bind =================

    [Fact]
    public void A_component_s_fields_bound_to_its_parameters_pass()
    {
        Assert.Empty(ComponentProblems(Strip()));
        Assert.Empty(Problems(new[] { Strip() }));
    }

    [Fact]
    public void A_macro_s_action_can_t_take_a_parameter_s_value()
    {
        var macro = Macro("D4", Bound(Click(), ("Point", "Slot")));
        Assert.Contains(MacroProblems(macro), m => m.EndsWith(Strings.Code_BindOnlyComponent));
        var condition = Macro("D4", If(Bound(Pixel(), ("Point", "Slot")), Press()));
        Assert.Contains(MacroProblems(condition), m => m.EndsWith(Strings.Code_BindOnlyComponent));
    }

    [Fact]
    public void A_Bind_names_a_slot_its_action_has_and_uses()
    {
        var name = Comp("A", new[] { SlotParam() }, Bound(Click(), ("Name", "Slot")));
        Assert.Contains(ComponentProblems(name), m => m.EndsWith(F(Strings.Code_BindNoSlot, "Name", "DelayMs, Point, HoldMs, Repeat, RepeatPauseMs")));

        var found = If(ImageFinder(), Bound(Click(found: Guid.Empty), ("Point", "Slot")));
        found.If!.Then[0].FoundId = found.If.Conditions[0].Id;
        Assert.Contains(ComponentProblems(Comp("A", new[] { SlotParam() }, found)), m => m.EndsWith(F(Strings.Code_BindUnused, "Point")));

        var wait = Bound(If(Pixel(), Press()), ("WaitSeconds", "Wait"));
        var waitParam = Param("Wait", ParameterType.Number, Number(2), unit: NumberUnit.Seconds);
        Assert.Contains(ComponentProblems(Comp("A", new[] { waitParam }, wait)), m => m.EndsWith(F(Strings.Code_BindUnused, "WaitSeconds")));
        wait.If!.Wait = ConditionWait.UpTo;
        wait.If.WaitSeconds = 2;
        Assert.Empty(ComponentProblems(Comp("A", new[] { waitParam }, wait)));

        var area = ImageFinder();
        var areaParam = Param("Where", ParameterType.Area, Area(0, 0, 100, 100));
        Assert.Contains(ComponentProblems(Comp("A", new[] { areaParam }, If(Bound(area, ("Area", "Where")), Press()))),
            m => m.EndsWith(F(Strings.Code_BindUnused, "Area")));

        var row = Bound(Call(Guid.NewGuid()), ("DelayMs", "Hold"));
        Assert.Contains(Problems(new[] { Comp("A", new[] { HoldParam() }, row) }), m => m.EndsWith(Strings.Code_BindNoSlotHere));
    }

    [Fact]
    public void A_Bind_names_a_parameter_of_the_same_type_and_unit()
    {
        Assert.Contains(ComponentProblems(Comp("A", new[] { SlotParam() }, Bound(Click(), ("Point", "Place")))),
            m => m.EndsWith(F(Strings.Code_NoParameter, "Place")));

        var text = Param("Slot", ParameterType.Text, Text("x"));
        Assert.Contains(ComponentProblems(Comp("A", new[] { text }, Bound(Click(), ("Point", "Slot")))),
            m => m.EndsWith(F(Strings.Code_ParameterType, "Slot", ComponentChecks.TypeName(ParameterType.Text, null), "Point",
                ComponentChecks.TypeName(ParameterType.Point, null))));

        var seconds = Param("Hold", ParameterType.Number, Number(1), unit: NumberUnit.Seconds);
        Assert.Contains(ComponentProblems(Comp("A", new[] { seconds }, Bound(Press(), ("HoldMs", "Hold")))),
            m => m.Contains(ComponentChecks.TypeName(ParameterType.Number, NumberUnit.Seconds)));

        // Names are matched exactly.
        Assert.Contains(ComponentProblems(Comp("A", new[] { SlotParam() }, Bound(Click(), ("Point", "slot")))),
            m => m.EndsWith(F(Strings.Code_NoParameter, "slot")));
    }

    [Fact]
    public void Every_kind_of_slot_takes_its_own_type()
    {
        var parameters = new[]
        {
            Param("Key", ParameterType.Key, Key(0x53)), Param("Text", ParameterType.Text, Text("hi")),
            Param("Color", ParameterType.Color, Text("#463405")), Param("Images", ParameterType.Image, Images(ImageA)),
            Param("Tolerance", ParameterType.Number, Number(5), unit: NumberUnit.Percent),
            Param("Similarity", ParameterType.Number, Number(90), unit: NumberUnit.Percent),
            Param("Steps", ParameterType.Number, Number(3), unit: NumberUnit.Count),
            Param("Area", ParameterType.Area, Area(0, 0, 100, 100)), SlotParam(),
            Param("Delay", ParameterType.Number, Number(100), unit: NumberUnit.Ms),
        };
        var image = ImageFinder();
        image.Area = SearchArea.Drawn;
        (image.AreaWidth, image.AreaHeight) = (100, 100);
        var component = Comp("All slots", parameters,
            Bound(new MacroAction { Type = MacroActionType.KeyDown, Vk = 0x53 }, ("Key", "Key")),
            Bound(new MacroAction { Type = MacroActionType.PasteText, Text = "hi" }, ("Text", "Text"), ("DelayMs", "Delay")),
            Bound(new MacroAction { Type = MacroActionType.Scroll }, ("ScrollSteps", "Steps")),
            Bound(new MacroAction { Type = MacroActionType.MoveTo, X = 3912, Y = 1056 }, ("Point", "Slot")),
            If(new[]
            {
                Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"), ("Color", "Color"), ("Tolerance", "Tolerance")),
                Bound(image, ("Images", "Images"), ("Similarity", "Similarity"), ("Area", "Area")),
                Bound(new MacroCondition { Kind = ConditionKind.Text, Name = "Hi", Text = "hi", Area = SearchArea.Drawn, AreaWidth = 100, AreaHeight = 100 },
                    ("Text", "Text"), ("Area", "Area")),
            }, ConditionMatch.Any, Press()));
        Assert.Empty(ComponentProblems(component));
    }

    // ================= Limits rising to the signature =================

    [Fact]
    public void A_parameter_keeps_the_limits_of_every_field_bound_to_it()
    {
        // Hold goes to HoldMs (1 to 60000) and to DelayMs (0 to 86400000): together 1 to 60000.
        var component = Comp("A", new[] { HoldParam() }, Bound(Press(), ("HoldMs", "Hold"), ("DelayMs", "Hold")));
        var signatures = new ComponentSignatures(new[] { component });
        Assert.Equal(MacroJson.HoldMs, signatures.RuleOf(component.Id, "Hold").Range);

        component.Parameters[0].Example = Number(0);
        Assert.Contains(ComponentProblems(component), m => m.EndsWith(Between(MacroJson.HoldMs)));
        component.Parameters[0].Example = Number(30);
        component.Parameters[0].Default = Number(60_001);
        Assert.Contains(ComponentProblems(component), m => m.EndsWith(Between(MacroJson.HoldMs)));
    }

    [Fact]
    public void Limits_rise_through_From_down_the_calls()
    {
        var press = Comp("Press", new[] { HoldParam() }, Bound(Press(), ("HoldMs", "Hold")));
        var middle = Comp("Middle", new[] { HoldParam("Time") }, Call(press, ("Hold", From("Time"))));
        var top = Comp("Top", new[] { HoldParam("Long") }, Call(middle, ("Time", From("Long"))));
        var signatures = new ComponentSignatures(new[] { press, middle, top });

        Assert.Equal(MacroJson.HoldMs, signatures.RuleOf(middle.Id, "Time").Range);
        Assert.Equal(MacroJson.HoldMs, signatures.RuleOf(top.Id, "Long").Range);
        // A call two levels up is checked against the field at the bottom.
        var macro = Macro("Farm", Call(top, ("Long", Number(0))));
        Assert.Contains(Problems(new[] { press, middle, top }, macro), m => m.EndsWith(Between(MacroJson.HoldMs)));
        Assert.Empty(Problems(new[] { press, middle, top }, Macro("Farm", Call(top, ("Long", Number(40))))));
        // The example of a parameter passed on is checked too.
        top.Parameters[0].Example = Number(70_000);
        Assert.Contains(ComponentProblems(top, press, middle), m => m.EndsWith(Between(MacroJson.HoldMs)));
    }

    [Fact]
    public void A_parameter_bound_nowhere_keeps_its_type_s_own_limits()
    {
        var unused = Comp("A", new[] { Param("Count", ParameterType.Number, Number(3), unit: NumberUnit.Count) }, Press());
        var signatures = new ComponentSignatures(new[] { unused });
        Assert.Equal((0d, 100_000d), signatures.RuleOf(unused.Id, "Count").Range);
        Assert.True(signatures.RuleOf(unused.Id, "Count").Whole);
        Assert.Equal(ValueRule.Any, signatures.RuleOf(Guid.NewGuid(), "Count"));
    }

    // ================= Calls =================

    [Fact]
    public void A_call_gives_every_required_parameter_and_only_known_ones()
    {
        var strip = Strip();
        Assert.Empty(MacroProblems(Macro("D4", Call(strip, ("Slot", Point(3986, 1056)))), strip));
        // Hold has a default: it can be left out, or given.
        Assert.Empty(MacroProblems(Macro("D4", Call(strip, ("Slot", Point(3986, 1056)), ("Hold", Number(50)))), strip));

        Assert.Contains(MacroProblems(Macro("D4", Call(strip)), strip), m => m.EndsWith(F(Strings.Code_ArgumentMissing, "Strip one slot", "Slot")));
        Assert.Contains(MacroProblems(Macro("D4", Call(strip, ("Slot", Point(1, 2)), ("Speed", Number(3)))), strip),
            m => m.EndsWith(F(Strings.Code_ArgumentUnknown, "Strip one slot", "Speed")));
    }

    [Fact]
    public void A_value_must_be_of_its_parameter_s_type_and_inside_its_limits()
    {
        var strip = Strip();
        Assert.Contains(MacroProblems(Macro("D4", Call(strip, ("Slot", Text("3986,1056")))), strip), m => m.EndsWith(Strings.Code_ValuePoint));
        Assert.Contains(MacroProblems(Macro("D4", Call(strip, ("Slot", Point(1, 2)), ("Hold", Number(0)))), strip), m => m.EndsWith(Between(MacroJson.HoldMs)));
        Assert.Contains(MacroProblems(Macro("D4", Call(strip, ("Slot", null))), strip), m => m.EndsWith(Strings.Code_ValuePoint));
    }

    [Fact]
    public void A_call_needs_a_component_the_Library_has()
    {
        var noId = new MacroAction { Type = MacroActionType.Component };
        Assert.Contains(MacroProblems(Macro("D4", noId)), m => m.EndsWith(Strings.Code_ChooseComponent));
        Assert.Contains(MacroProblems(Macro("D4", Call(Guid.Empty))), m => m.EndsWith(Strings.Code_ChooseComponent));
        Assert.Contains(MacroProblems(Macro("D4", Call(Guid.NewGuid()))), m => m.EndsWith(Strings.Code_ComponentMissing));
        // Without the Library (a check that has none) only the id is checked.
        MacroJson.Parse(MacroJson.Serialize(Macro("D4", Call(Guid.NewGuid()))), Guid.Empty, (_, _) => null, out var problems);
        Assert.DoesNotContain(problems, p => p.Message.EndsWith(Strings.Code_ComponentMissing));
    }

    [Fact]
    public void Arguments_and_a_component_id_belong_only_to_calls()
    {
        var click = Click();
        click.Arguments = new() { ["Slot"] = Point(1, 2) };
        Assert.Contains(MacroProblems(Macro("D4", click)), m => m.EndsWith(Strings.Code_ArgumentsOnlyCalls));
        var pixel = Pixel();
        pixel.Arguments = new() { ["Slot"] = Point(1, 2) };
        pixel.ComponentId = Guid.NewGuid();
        var problems = MacroProblems(Macro("D4", If(pixel, Press())));
        Assert.Contains(problems, m => m.EndsWith(Strings.Code_ArgumentsOnlyCalls));
        Assert.Contains(problems, m => m.EndsWith(Strings.Code_ComponentIdOnlyLinks));
    }

    [Fact]
    public void From_passes_on_a_parameter_of_the_component_around_of_the_same_type()
    {
        var strip = Strip();
        var all = Comp("Strip at", new[] { SlotParam("Where") }, Call(strip, ("Slot", From("Where"))));
        Assert.Empty(ComponentProblems(all, strip));

        Assert.Contains(MacroProblems(Macro("D4", Call(strip, ("Slot", From("Where")))), strip), m => m.EndsWith(Strings.Code_FromOutside));
        Assert.Contains(ComponentProblems(Comp("B", new[] { SlotParam("Where") }, Call(strip, ("Slot", From("There")))), strip),
            m => m.EndsWith(F(Strings.Code_NoParameter, "There")));
        Assert.Contains(ComponentProblems(Comp("B", new[] { Param("Where", ParameterType.Text, Text("x")) }, Call(strip, ("Slot", From("Where")))), strip),
            m => m.Contains(ComponentChecks.TypeName(ParameterType.Text, null)));
        Assert.Contains(ComponentProblems(Comp("B", new[] { SlotParam("Where") }, Call(strip, ("Slot", new JsonObject { ["From"] = "Where", ["X"] = 1 }))), strip),
            m => m.EndsWith(Strings.Code_ArgumentReference));
    }

    [Fact]
    public void Found_passes_where_an_If_around_the_call_found_something()
    {
        var strip = Strip();
        var finder = ImageFinder("Slot found");
        var good = Macro("D4", If(finder, Call(strip, ("Slot", Found(finder.Id, 10, -5)))));
        Assert.Empty(MacroProblems(good, strip));

        // Not inside that If's Then.
        var outside = Macro("D4", If(ImageFinder("Other"), Press()), Call(strip, ("Slot", Found(finder.Id))));
        Assert.Contains(MacroProblems(outside, strip), m => m.EndsWith(Strings.Code_FoundOutside));
        // Inside its Else.
        var inElse = If(finder, Press());
        inElse.If!.Else.Add(Call(strip, ("Slot", Found(finder.Id))));
        Assert.Contains(MacroProblems(Macro("D4", inElse), strip), m => m.EndsWith(Strings.Code_FoundOutside));
        // A condition true when something is not there finds no place.
        var notThere = ImageFinder("Gone", not: true);
        Assert.Contains(MacroProblems(Macro("D4", If(notThere, Call(strip, ("Slot", Found(notThere.Id))))), strip),
            m => m.EndsWith(Strings.Code_FoundOutside) || m.EndsWith(Strings.Code_FoundNot));
        // Only a point can come from a found place.
        var hold = Macro("D4", If(finder, Call(strip, ("Slot", Point(1, 2)), ("Hold", Found(finder.Id)))));
        Assert.Contains(MacroProblems(hold, strip), m => m.EndsWith(F(Strings.Code_FoundNotPoint, "Hold", ComponentChecks.TypeName(ParameterType.Number, NumberUnit.Ms))));
        // Offsets stay on the screen's range.
        var far = Macro("D4", If(finder, Call(strip, ("Slot", Found(finder.Id, 200_000)))));
        Assert.Contains(MacroProblems(far, strip), m => m.Contains("100000") || m.Contains("100.000") || m.Contains("100,000"));
        // Written wrong.
        var wrong = Macro("D4", If(finder, Call(strip, ("Slot", new JsonObject { ["Found"] = "not an id" }))));
        Assert.Contains(MacroProblems(wrong, strip), m => m.EndsWith(Strings.Code_ArgumentReference));
    }

    [Fact]
    public void A_component_s_own_Found_uses_its_own_conditions()
    {
        var strip = Strip();
        var finder = ImageFinder();
        var inside = Comp("Find and strip", If(finder, Call(strip, ("Slot", Found(finder.Id)))));
        Assert.Empty(ComponentProblems(inside, strip));
        var outer = ImageFinder("Outer");
        var fromOutside = Comp("Strip there", Call(strip, ("Slot", Found(outer.Id))));
        Assert.Contains(ComponentProblems(fromOutside, strip), m => m.EndsWith(Strings.Code_FoundOutside));
    }

    // ================= Component conditions =================

    /// <summary>"Rare item in slot": true when the pixel at Slot has the rare color; it returns that pixel.</summary>
    private static LibraryComponent Rare()
    {
        var pixel = Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"));
        var component = Comp("Rare item in slot", new[] { SlotParam() }, If(pixel));
        component.Returns = new ComponentReturns { Found = pixel.Id };
        return component;
    }

    /// <summary>"Remove Affix button": finds the button's image; it returns where.</summary>
    private static LibraryComponent Button()
    {
        var image = ImageFinder("Remove Affix");
        var component = Comp("Remove Affix button", If(image));
        component.Returns = new ComponentReturns { Found = image.Id };
        return component;
    }

    [Fact]
    public void A_Component_condition_calls_a_component_that_says_what_it_finds()
    {
        var rare = Rare();
        Assert.Empty(MacroProblems(Macro("D4", If(CallCondition(rare, "Rare", ("Slot", Point(3986, 1056))), Press())), rare));

        var nothing = Comp("Nothing", Press());
        Assert.Contains(MacroProblems(Macro("D4", If(CallCondition(nothing, "Nothing"), Press())), nothing),
            m => m.EndsWith(F(Strings.Code_ComponentNoReturns, "Nothing")));
        Assert.Contains(MacroProblems(Macro("D4", If(CallCondition(rare, "Rare"), Press())), rare),
            m => m.EndsWith(F(Strings.Code_ArgumentMissing, "Rare item in slot", "Slot")));
        var unnamed = CallCondition(rare, " ", ("Slot", Point(1, 2)));
        Assert.Contains(MacroProblems(Macro("D4", If(unnamed, Press())), rare), m => m.EndsWith(Strings.Code_EnterConditionName));
        var bound = Bound(CallCondition(rare, "Rare", ("Slot", Point(1, 2))), ("Point", "Slot"));
        Assert.Contains(ComponentProblems(Comp("C", new[] { SlotParam() }, If(bound, Press())), rare), m => m.EndsWith(Strings.Code_BindNoSlotHere));
        var noId = new MacroCondition { Kind = ConditionKind.Component, Name = "X" };
        Assert.Contains(MacroProblems(Macro("D4", If(noId, Press())), rare), m => m.EndsWith(Strings.Code_ChooseComponent));
    }

    [Fact]
    public void A_Where_was_found_of_a_Component_condition_needs_a_place()
    {
        var button = Button();
        var found = CallCondition(button, "Button");
        Assert.Empty(MacroProblems(Macro("D4", If(found, Click(found: found.Id))), button));

        // A pixel gives no place.
        var rare = Rare();
        var pixel = CallCondition(rare, "Rare", ("Slot", Point(1, 2)));
        Assert.Contains(MacroProblems(Macro("D4", If(pixel, Click(found: pixel.Id))), rare), m => m.EndsWith(F(Strings.Code_FoundNoPlace, "Rare")));
        // Nor something that isn't there: the component's own Not…
        var gone = Button();
        FindCondition(gone).Not = true;
        var notThere = CallCondition(gone, "Gone");
        Assert.Contains(MacroProblems(Macro("D4", If(notThere, Click(found: notThere.Id))), gone), m => m.EndsWith(F(Strings.Code_FoundNoPlace, "Gone")));
        // …or the call's, which the checks of any condition already refuse.
        var called = CallCondition(button, "Button", Array.Empty<(string, JsonNode?)>());
        called.Not = true;
        Assert.Contains(MacroProblems(Macro("D4", If(called, Click(found: called.Id))), button), m => m.EndsWith(Strings.Code_FoundNot));
        // The same rule for a {"Found"} argument.
        var strip = Strip();
        Assert.Contains(MacroProblems(Macro("D4", If(pixel, Call(strip, ("Slot", Found(pixel.Id))))), rare, strip), m => m.EndsWith(F(Strings.Code_FoundNoPlace, "Rare")));
    }

    [Fact]
    public void What_a_Component_condition_checks_is_its_component_s_returned_condition_with_Not_turned()
    {
        var rare = Rare();
        var signatures = new ComponentSignatures(new[] { rare });
        Assert.Equal(ConditionKind.Pixel, ComponentChecks.ReturnedCondition(signatures, rare.Id, not: false)!.Kind);
        Assert.False(ComponentChecks.ReturnedCondition(signatures, rare.Id, not: false)!.Not);
        Assert.True(ComponentChecks.ReturnedCondition(signatures, rare.Id, not: true)!.Not);

        // A component that returns another's Component condition: down to the real one, each Not on the way turning it.
        var inner = CallCondition(rare, "Inner", ("Slot", From("Slot")));
        inner.Not = true;
        var wrapper = Comp("Not rare", new[] { SlotParam() }, If(inner));
        wrapper.Returns = new ComponentReturns { Found = inner.Id };
        signatures = new ComponentSignatures(new[] { rare, wrapper });
        Assert.True(ComponentChecks.ReturnedCondition(signatures, wrapper.Id, not: false)!.Not);
        Assert.False(ComponentChecks.ReturnedCondition(signatures, wrapper.Id, not: true)!.Not);
        Assert.Null(ComponentChecks.ReturnedCondition(signatures, Guid.NewGuid(), false));
    }

    [Fact]
    public void Returns_names_a_condition_of_the_component_that_needs_nothing_from_outside()
    {
        var rare = Rare();
        Assert.Empty(ComponentProblems(rare));
        rare.Returns!.Found = Guid.NewGuid();
        Assert.Contains(ComponentProblems(rare), m => m.EndsWith(Strings.Code_ReturnsMissing));

        // Returning a Component condition whose argument is a place found outside it.
        var button = Button();
        var image = ImageFinder("Outer");
        var strip = Strip();
        var inner = CallCondition(Rare(), "Inner", ("Slot", Found(image.Id)));
        var component = Comp("Bad", If(image, Press()), If(inner));
        component.Returns = new ComponentReturns { Found = inner.Id };
        Assert.Contains(ComponentProblems(component, Rare(), button, strip), m => m.EndsWith(Strings.Code_ReturnsFoundOutside));
    }

    private static MacroCondition FindCondition(LibraryComponent component) =>
        ComponentChecks.FindCondition(component.Actions, component.Returns!.Found)!;

    // ================= A component's own parameters =================

    [Theory]
    [InlineData("Slot", true)]
    [InlineData("slot_2", true)]
    [InlineData("S", true)]
    [InlineData("2Slot", false)]
    [InlineData("_Slot", false)]
    [InlineData("Slot X", false)]
    [InlineData("Slöt", false)]
    [InlineData("", false)]
    [InlineData("A234567890123456789012345678901234567890", true)]
    [InlineData("A2345678901234567890123456789012345678901", false)]
    public void A_parameter_s_name_starts_with_a_letter(string name, bool valid)
    {
        var component = Comp("A", new[] { SlotParam(name) }, Press());
        Assert.Equal(valid, !ComponentProblems(component).Any(m => m.EndsWith(Strings.Code_ParameterName)));
    }

    [Fact]
    public void Parameters_have_names_of_their_own_whatever_the_case()
    {
        Assert.Contains(ComponentProblems(Comp("A", new[] { SlotParam("Slot"), SlotParam("slot") }, Press())), m => m.EndsWith(Strings.Code_ParameterNameTwice));
    }

    [Fact]
    public void Only_a_number_has_a_unit_and_a_number_always_has_one()
    {
        var number = HoldParam();
        number.Unit = null;
        Assert.Contains(ComponentProblems(Comp("A", new[] { number }, Press())), m => m.EndsWith(Strings.Code_ParameterUnit));
        var point = SlotParam();
        point.Unit = NumberUnit.Pixels;
        Assert.Contains(ComponentProblems(Comp("A", new[] { point }, Press())), m => m.EndsWith(Strings.Code_ParameterNoUnit));
    }

    [Fact]
    public void A_parameter_has_an_example_and_maybe_a_default_both_values_of_its_type()
    {
        var noExample = SlotParam();
        noExample.Example = null;
        Assert.Contains(ComponentProblems(Comp("A", new[] { noExample }, Press())), m => m.EndsWith(Strings.Code_ParameterExample));
        var wrongExample = SlotParam();
        wrongExample.Example = Text("x");
        Assert.Contains(ComponentProblems(Comp("A", new[] { wrongExample }, Press())), m => m.EndsWith(Strings.Code_ValuePoint));
        var wrongDefault = SlotParam();
        wrongDefault.Default = Number(3);
        Assert.Contains(ComponentProblems(Comp("A", new[] { wrongDefault }, Press())), m => m.EndsWith(Strings.Code_ValuePoint));
        var reference = SlotParam();
        reference.Example = From("Other");
        Assert.Contains(ComponentProblems(Comp("A", new[] { reference }, Press())), m => m.EndsWith(Strings.Code_ParameterValueOnly));
        var referenceDefault = SlotParam();
        referenceDefault.Default = Found(Guid.NewGuid());
        Assert.Contains(ComponentProblems(Comp("A", new[] { referenceDefault }, Press())), m => m.EndsWith(Strings.Code_ParameterValueOnly));
        var missingImage = Param("Images", ParameterType.Image, Images(ImageA));
        Assert.Contains(ComponentLibrary.Problems(Comp("A", new[] { missingImage }, Press()), _ => false), p => p.Message.EndsWith(Strings.Code_ImageMissing));
    }

    [Fact]
    public void A_component_has_no_groups()
    {
        var press = Press();
        press.GroupId = Guid.NewGuid();
        Assert.Contains(ComponentProblems(Comp("A", press)), m => m.EndsWith(Strings.Code_NoSuchGroup));
    }

    // ================= Loops and depth =================

    [Fact]
    public void Components_never_call_each_other_in_a_loop()
    {
        var a = Comp("A", Press());
        var b = Comp("B", Call(a));
        a.Actions.Add(Call(b));
        var signatures = new ComponentSignatures(new[] { a, b });
        Assert.NotNull(signatures.CycleOf(a.Id));
        Assert.NotNull(signatures.CycleOf(b.Id));
        Assert.Contains(ComponentProblems(a, b), m => m.EndsWith(F(Strings.Code_ComponentCycle, "A → B → A")));

        var self = Comp("Self", Press());
        self.Actions.Add(Call(self));
        Assert.Contains(ComponentProblems(self), m => m.EndsWith(F(Strings.Code_ComponentCycle, "Self → Self")));

        // Through a Component condition too.
        var finder = Comp("Finder", Press());
        var asCondition = CallCondition(finder, "Me");
        finder.Actions.Add(If(asCondition, Press()));
        finder.Returns = new ComponentReturns { Found = asCondition.Id };
        Assert.NotNull(new ComponentSignatures(new[] { finder }).CycleOf(finder.Id));

        // A chain without a loop has none.
        var c = Comp("C", Press());
        var d = Comp("D", Call(c), Call(c));
        Assert.Null(new ComponentSignatures(new[] { c, d }).CycleOf(d.Id));
    }

    [Fact]
    public void Components_go_at_most_10_levels_deep()
    {
        var chain = new List<LibraryComponent> { Comp("L1", Press()) };
        for (int level = 2; level <= 11; level++)
            chain.Add(Comp($"L{level}", Call(chain[^1])));
        var signatures = new ComponentSignatures(chain);
        Assert.Equal(1, signatures.Height(chain[0].Id));
        Assert.Equal(10, signatures.Height(chain[9].Id));
        Assert.Equal(11, signatures.Height(chain[10].Id));

        Assert.DoesNotContain(ComponentProblems(chain[9], chain.Take(9).ToArray()), m => m.Contains(F(Strings.Code_ComponentTooDeep, 10, 10)));
        Assert.Contains(ComponentProblems(chain[10], chain.Take(10).ToArray()), m => m.EndsWith(F(Strings.Code_ComponentTooDeep, 10, 11)));
        // The deepest branch counts.
        var wide = Comp("Wide", Call(chain[0]), Call(chain[8]));
        Assert.Equal(10, new ComponentSignatures(chain.Append(wide)).Height(wide.Id));
    }

    [Fact]
    public void Callers_are_found_down_every_call()
    {
        var a = Comp("A", Press());
        var b = Comp("B", Call(a));
        var c = Comp("C", Call(b));
        var d = Comp("D", Press());
        var signatures = new ComponentSignatures(new[] { a, b, c, d });
        Assert.Equal(new[] { b.Id, c.Id }.ToHashSet(), signatures.CallersOf(a.Id));
        Assert.Empty(signatures.CallersOf(d.Id));
        Assert.Equal(new[] { a.Id }, signatures.CallsOf(b.Id));
    }

    // ================= Everything at once =================

    [Fact]
    public void Everything_at_once_names_where_each_problem_is()
    {
        var strip = Strip();
        var good = Macro("Good", Call(strip, ("Slot", Point(1, 2))));
        var bad = Macro("Bad", Call(strip));
        var problems = ComponentChecks.CheckAll(new ComponentSignatures(new[] { strip }), new[] { good, bad }, null);

        var only = Assert.Single(problems);
        Assert.Equal((bad.Id, "Bad", "Actions[0].Arguments"), (only.ItemId, only.Where, only.Path));
        Assert.Equal(F(Strings.Code_ArgumentMissing, "Strip one slot", "Slot"), only.Message);
    }

    [Fact]
    public void A_macro_can_t_play_when_its_calls_fail_or_call_a_component_that_does()
    {
        var press = Comp("Press", new[] { HoldParam() }, Bound(Press(), ("HoldMs", "Hold")));
        var broken = Comp("Broken", Call(press)); // Hold is missing
        var fine = Macro("Fine", Call(press, ("Hold", Number(20))));
        var wrongArgument = Macro("Wrong", Call(press, ("Hold", Number(0))));
        var callsBroken = Macro("Calls broken", Call(broken));
        var callsBrokenDeep = Macro("Deep", Call(Comp("Wrapper", Call(broken)).Id));
        var wrapper = Comp("Wrapper", Call(broken));
        callsBrokenDeep.Actions[0].ComponentId = wrapper.Id;
        var plain = Macro("Plain", Press());

        var unplayable = ComponentChecks.Unplayable(new ComponentSignatures(new[] { press, broken, wrapper }),
            new[] { fine, wrongArgument, callsBroken, callsBrokenDeep, plain });

        Assert.Equal(new[] { wrongArgument.Id, callsBroken.Id, callsBrokenDeep.Id }.ToHashSet(), unplayable);
    }

    [Fact]
    public void A_component_s_JSON_is_read_back_strictly_with_its_parameters()
    {
        var strip = Strip();
        var read = MacroJson.ParseComponent(CodeJson.Serialize(strip), strip.Id, out var problems, null, Array.Empty<LibraryComponent>());
        Assert.Empty(problems);
        Assert.Equal(CodeJson.Serialize(strip), CodeJson.Serialize(read!));

        string unknownField = CodeJson.Serialize(strip).Replace("\"Name\": \"Slot\"", "\"Name\": \"Slot\", \"Optional\": true");
        Assert.NotEqual(CodeJson.Serialize(strip), unknownField);
        Assert.Null(MacroJson.ParseComponent(unknownField, strip.Id, out problems, null, Array.Empty<LibraryComponent>()));
        Assert.NotEmpty(problems);
        Assert.Null(MacroJson.ParseComponent(CodeJson.Serialize(strip), Guid.NewGuid(), out problems, null, Array.Empty<LibraryComponent>()));
        Assert.NotEmpty(problems); // the Id can't change
    }
}
