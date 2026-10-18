using System;
using System.Collections.Generic;
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
/// What the component's editor and the Library do to components besides the checks already covered: examples written into
/// bound fields, a parameter renamed or removed in the editor, the guided update's less common cases, the signatures with a
/// component in place of the saved one, collections called down every call (loops and the same one twice), the components a
/// macro misses through its conditions, the images of nested calls, new ids following into a condition's arguments, the
/// component's schema, and one action's Code view inside a component and in a macro.
/// </summary>
public class ComponentEditsTests
{
    private static ComponentParameter Ms(string name, JsonNode? @default = null) => Param(name, ParameterType.Number, Number(30), @default, NumberUnit.Ms);

    // ================= Examples, rename, remove =================

    [Fact]
    public void Each_bound_field_gets_its_parameter_s_example()
    {
        var pixel = Bound(Pixel(1, 1, "#000000"), ("Point", "Slot"), ("Color", "Color"));
        var component = Comp("A", new[] { SlotParam(), Param("Color", ParameterType.Color, Text("#463405")), Ms("Hold") },
            If(pixel, Bound(Click(1, 1), ("Point", "Slot"), ("HoldMs", "Hold"))),
            Bound(Click(9, 9), ("Point", "Gone")));

        ComponentEdits.WriteExamples(component);

        var condition = component.Actions[0].If!.Conditions[0];
        Assert.Equal((3912, 1056, "#463405"), (condition.X, condition.Y, condition.Color));
        var click = component.Actions[0].If!.Then[0];
        Assert.Equal((3912, 1056, 30d), (click.X, click.Y, click.HoldMs));
        Assert.Equal((9, 9), (component.Actions[1].X, component.Actions[1].Y)); // a parameter it doesn't have: left as it is
    }

    [Fact]
    public void A_parameter_without_an_example_or_of_another_type_leaves_the_field()
    {
        var noExample = SlotParam();
        noExample.Example = null;
        var component = Comp("A", new[] { noExample, Param("Word", ParameterType.Text, Text("x")) },
            Bound(Click(5, 6), ("Point", "Slot")), Bound(Click(7, 8), ("Point", "Word")));
        ComponentEdits.WriteExamples(component);
        Assert.Equal((5, 6), (component.Actions[0].X, component.Actions[0].Y));
        Assert.Equal((7, 8), (component.Actions[1].X, component.Actions[1].Y));
    }

    [Fact]
    public void A_renamed_parameter_s_fields_and_passed_values_follow()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Press());
        var condition = Bound(CallCondition(strip, "Inner", ("Slot", From("Slot"))), ("Point", "Slot"));
        var actions = new List<MacroAction>
        {
            Bound(Click(), ("Point", "Slot")),
            If(Bound(Pixel(), ("Point", "Slot")), Call(strip, ("Slot", From("Slot")))),
            If(condition),
            Bound(Click(), ("Point", "Other")),
        };

        ComponentEdits.Rename(actions, "Slot", "Place");

        Assert.Equal("Place", actions[0].Bind!["Point"]);
        Assert.Equal("Place", actions[1].If!.Conditions[0].Bind!["Point"]);
        Assert.True(JsonNode.DeepEquals(From("Place"), actions[1].If!.Then[0].Arguments!["Slot"]));
        Assert.True(JsonNode.DeepEquals(From("Place"), actions[2].If!.Conditions[0].Arguments!["Slot"]));
        Assert.Equal("Place", actions[2].If!.Conditions[0].Bind!["Point"]);
        Assert.Equal("Other", actions[3].Bind!["Point"]); // another parameter stays
    }

    [Fact]
    public void A_removed_parameter_leaves_its_fields_their_values_and_its_calls_its_example()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Press());
        var removed = SlotParam();
        var actions = new List<MacroAction>
        {
            Bound(Click(5, 6), ("Point", "Slot"), ("HoldMs", "Hold")),
            If(Bound(Pixel(), ("Point", "Slot")), Call(strip, ("Slot", From("Slot")))),
            If(CallCondition(strip, "Inner", ("Slot", From("Slot")))),
        };

        ComponentEdits.Forget(actions, removed);

        Assert.Equal(new[] { "HoldMs" }, actions[0].Bind!.Keys);
        Assert.Equal((5, 6), (actions[0].X, actions[0].Y));
        Assert.Empty(actions[1].If!.Conditions[0].Bind!);
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), actions[1].If!.Then[0].Arguments!["Slot"]));
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), actions[2].If!.Conditions[0].Arguments!["Slot"]));
    }

    [Fact]
    public void A_parameter_of_another_type_gives_the_calls_that_passed_it_on_its_old_example()
    {
        // The editor: Slot, a Point, renamed Where (its fields and calls follow), then made a Text.
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        var old = SlotParam("Slot");
        var actions = new List<MacroAction>
        {
            Bound(Click(5, 6), ("Point", "Where")),
            Call(strip, ("Slot", From("Where"))),
            If(CallCondition(strip, "Inner", ("Slot", From("Where"))), Press()),
        };

        ComponentEdits.Retyped(actions, old, "Where");

        Assert.Empty(actions[0].Bind!);
        Assert.Equal((5, 6), (actions[0].X, actions[0].Y));
        // The calls take a Point: they get the old Example, never the new type's.
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), actions[1].Arguments!["Slot"]));
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), actions[2].If!.Conditions[0].Arguments!["Slot"]));
    }

    // ================= The guided update =================

    private static LibraryComponent Strip(params ComponentParameter[] parameters) =>
        Comp("Strip one slot", parameters, Bound(Click(3912, 1056), ("Point", "Slot")));

    [Fact]
    public void A_rename_and_a_removal_together_follow_and_go()
    {
        var before = Strip(SlotParam(), Ms("Speed", Number(30)), Ms("Hold", Number(30)));
        var after = before.Clone();
        after.Parameters.RemoveAll(p => p.Name == "Speed");
        after.Parameters[0].Name = "Place";
        after.Actions[0].Bind!["Point"] = "Place";
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2)), ("Speed", Number(5)), ("Hold", Number(40))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, string> { ["Slot"] = "Place" });
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), after).Macros.Single();

        Assert.Equal(new[] { "Place", "Hold" }.ToHashSet(), changed.Actions[0].Arguments!.Keys.ToHashSet());
        Assert.True(JsonNode.DeepEquals(Number(40), changed.Actions[0].Arguments!["Hold"]));
    }

    [Fact]
    public void A_rename_to_a_name_that_isn_t_there_or_to_itself_is_not_a_rename()
    {
        var before = Strip(SlotParam());
        var after = before.Clone();
        var update = SignatureUpdate.Compare(before, after, Array.Empty<MacroDefinition>(), Array.Empty<LibraryComponent>(),
            new Dictionary<string, string> { ["Slot"] = "Slot", ["Ghost"] = "Slot", ["Slot "] = "Nowhere" });
        Assert.Empty(update.Renamed);
        Assert.Empty(update.Removed);
    }

    [Fact]
    public void A_value_of_the_right_kind_stays_and_only_the_others_get_the_new_one()
    {
        var before = Strip(SlotParam(), Ms("Hold", Number(30)));
        var after = before.Clone();
        after.Parameters[1].Default = null;
        var given = Call(before, ("Slot", Point(1, 2)), ("Hold", Number(80)));
        var wrong = Call(before, ("Slot", Point(1, 2)), ("Hold", Text("x")));
        var leftOut = Call(before, ("Slot", Point(1, 2)));
        var macro = Macro("D4", given, wrong, leftOut);

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());
        Assert.Empty(update.TypeChangedNames);
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode> { ["Hold"] = Number(45) }, after).Macros.Single();

        Assert.True(JsonNode.DeepEquals(Number(80), changed.Actions[0].Arguments!["Hold"]));
        Assert.True(JsonNode.DeepEquals(Number(45), changed.Actions[1].Arguments!["Hold"]));
        Assert.True(JsonNode.DeepEquals(Number(45), changed.Actions[2].Arguments!["Hold"]));
    }

    [Fact]
    public void Only_the_macros_and_components_that_call_it_change_and_a_value_not_given_leaves_the_call()
    {
        var before = Strip(SlotParam());
        var after = before.Clone();
        after.Parameters.Add(Ms("Settle"));
        var caller = Macro("D4", Call(before, ("Slot", Point(1, 2))));
        var other = Macro("Other", Press());
        var otherComponent = Comp("Other", Press());

        var update = SignatureUpdate.Compare(before, after, new[] { caller, other }, new[] { otherComponent });
        var (macros, components) = update.Apply(new[] { caller, other }, new[] { otherComponent }, new Dictionary<string, JsonNode>(), after);

        Assert.Equal(new[] { caller.Id }, macros.Select(m => m.Id));
        Assert.Empty(components);
        Assert.False(macros[0].Actions[0].Arguments!.ContainsKey("Settle")); // no value given: the checks will say it
    }

    [Fact]
    public void A_unit_changed_is_a_type_changed()
    {
        var before = Strip(SlotParam(), Ms("Hold"));
        var after = before.Clone();
        after.Parameters[1].Unit = NumberUnit.Count;
        var update = SignatureUpdate.Compare(before, after, new[] { Macro("D4", Call(before, ("Slot", Point(1, 2)), ("Hold", Number(5)))) },
            Array.Empty<LibraryComponent>());
        Assert.Contains("Hold", update.TypeChangedNames);
        Assert.Equal("Hold", Assert.Single(update.NeedValue).Name);
    }

    [Fact]
    public void A_call_with_no_arguments_left_after_the_update_has_none()
    {
        var before = Strip(SlotParam(), Ms("Speed", Number(30)));
        var after = before.Clone();
        after.Parameters.Clear();
        after.Actions[0].Bind = null;
        var macro = Macro("D4", Call(before, ("Speed", Number(3))));
        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());
        Assert.True(update.Needed);
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), after).Macros.Single();
        Assert.Null(changed.Actions[0].Arguments);
    }

    // ================= Checks and signatures =================

    [Fact]
    public void Inside_a_component_a_Where_was_found_of_its_Component_condition_needs_a_place()
    {
        var pixel = Pixel();
        var rare = Comp("Rare", If(pixel));
        rare.Returns = new ComponentReturns { Found = pixel.Id };
        var called = CallCondition(rare, "Rare");
        var outer = Comp("Use rare", If(called, Click(found: called.Id)));

        Assert.Contains(Problems(new[] { rare, outer }), p => p.Contains(string.Format(Strings.Code_FoundNoPlace, "Rare")));
    }

    [Fact]
    public void The_signatures_of_the_Library_with_a_changed_component_use_the_changed_one()
    {
        using var folder = new TempFolder();
        var macros = new MacroLibrary(folder.File("macros"));
        var strip = Strip(SlotParam());
        macros.Library.Collections.Add(new LibraryCollection { Name = "D4", Components = { strip } });

        Assert.Single(ComponentSignatures.Of(macros.Library).Find(strip.Id)!.Parameters);
        var changed = strip.Clone();
        changed.Parameters.Add(Ms("Hold"));
        Assert.Equal(2, ComponentSignatures.Of(macros.Library, changed).Find(strip.Id)!.Parameters.Count);
        var added = Comp("New", Press());
        Assert.NotNull(ComponentSignatures.Of(macros.Library, added).Find(added.Id));
        Assert.Equal(2, ComponentSignatures.Of(macros.Library, added).All.Count());
        Assert.Single(macros.Library.Signatures().All);
    }

    [Fact]
    public void A_missing_component_called_by_another_has_no_height_of_its_own()
    {
        var caller = Comp("Caller", Call(Guid.NewGuid()));
        Assert.Equal(1, new ComponentSignatures(new[] { caller }).Height(caller.Id));
        Assert.Empty(new ComponentSignatures(new[] { caller }).CallersOf(Guid.NewGuid()));
    }

    // ================= Collections, images, ids =================

    [Fact]
    public void Dependencies_go_down_every_call_once_even_with_loops()
    {
        var a = Comp("A", Press());
        var b = Comp("B", Call(a));
        a.Actions.Add(Call(b)); // a loop between two collections
        var c = Comp("C", Call(a), Call(a));
        using var folder = new TempFolder();
        var macros = new MacroLibrary(folder.File("macros"));
        var first = new LibraryCollection { Name = "First", Components = { a } };
        var second = new LibraryCollection { Name = "Second", Components = { b } };
        var third = new LibraryCollection { Name = "Third", Components = { c } };
        macros.Library.Collections.AddRange(new[] { first, second, third });

        Assert.Equal(new[] { "Third", "First", "Second" }, macros.Library.WithDependencies(new[] { third }).Select(x => x.Name));
        Assert.Equal(new[] { "First", "Second" }, macros.Library.WithDependencies(new[] { first, first }).Select(x => x.Name));
        Assert.Empty(macros.Library.WithDependencies(Array.Empty<LibraryCollection>()));
    }

    [Fact]
    public void Missing_counts_each_missing_component_once()
    {
        using var folder = new TempFolder();
        var macros = new MacroLibrary(folder.File("macros"));
        var present = Comp("Here", Press());
        macros.Library.Collections.Add(new LibraryCollection { Name = "C", Components = { present } });
        Guid gone = Guid.NewGuid();
        var macro = Macro("D4", Call(gone), Call(gone), Call(present), If(new MacroCondition { Kind = ConditionKind.Component, Name = "X", ComponentId = gone }),
            new MacroAction { Type = MacroActionType.Component });

        Assert.Equal(new[] { gone, Guid.Empty }.ToHashSet(), macros.Library.Missing(macro).ToHashSet());
    }

    [Fact]
    public void The_images_of_calls_inside_Then_and_Else_are_kept()
    {
        var finder = Comp("Find", new[] { Param("Look", ParameterType.Image, Images(ImageA)) }, Press());
        var block = If(Pixel(), Call(finder, ("Look", Images(ImageA))));
        block.If!.Else.Add(Call(finder, ("Look", Images(ImageB))));
        Assert.Equal(new[] { ImageA, ImageB }.ToHashSet(), ImageStore.IdsIn(Macro("D4", block)));
        Assert.Empty(ImageStore.IdsIn(Macro("D4", Call(finder, ("Look", From("Other"))))));
    }

    [Fact]
    public void New_condition_ids_follow_into_a_condition_s_arguments_and_leave_others_alone()
    {
        var strip = Strip(SlotParam());
        var finder = ImageFinder();
        Guid outside = Guid.NewGuid();
        var inner = CallCondition(strip, "Inner", ("Slot", Found(finder.Id, 1, 2)));
        var outer = CallCondition(strip, "Outer", ("Slot", Found(outside)));
        var actions = new List<MacroAction> { If(finder, Call(strip, ("Slot", Found(outside)))), If(new[] { inner, outer }, ConditionMatch.Any) };
        Guid before = finder.Id;

        var renewed = MacroEditList.RenewConditions(actions);

        var reference = ComponentValues.Reference(inner.Arguments!["Slot"])!;
        Assert.Equal(renewed[before], reference.Found);
        Assert.Equal((1, 2), (reference.OffsetX, reference.OffsetY));
        Assert.Equal(outside, ComponentValues.Reference(outer.Arguments!["Slot"])!.Found); // not one of these: left
        Assert.Equal(outside, ComponentValues.Reference(actions[0].If!.Then[0].Arguments!["Slot"])!.Found);
    }

    // ================= Schema and an action's Code view =================

    [Fact]
    public void The_component_s_schema_describes_its_signature_and_calls()
    {
        Guid id = Guid.NewGuid();
        var schema = JsonNode.Parse(CodeSchema.ForComponent(id))!;
        var properties = schema["properties"]!.AsObject();
        Assert.Contains("Parameters", properties.Select(p => p.Key));
        Assert.Contains("Returns", properties.Select(p => p.Key));
        Assert.Equal(id.ToString(), (string)properties["Id"]!["default"]!);
        string text = schema.ToJsonString();
        Assert.Contains("Its signature", text); // Parameters
        Assert.Contains("Inside a component only", text); // Bind
        Assert.Contains("Component rows: the value of each", text); // Arguments
        Assert.Contains("Component: a Library component that says what it finds", text); // the Kind
        Assert.Contains("Number only", text); // Unit
        Assert.Contains("\"Component\"", CodeSchema.ForMacro(Guid.Empty)); // the condition kind in a macro's schema too
    }

    [Fact]
    public void One_action_s_Code_view_accepts_Bind_only_inside_a_component()
    {
        string text = MacroJson.Serialize(Bound(Click(), ("Point", "Slot")));
        var groups = new Guid?[] { null };
        var names = new Dictionary<Guid, string>();

        Assert.NotNull(MacroJson.ParseAction(text, groups, names, out var inside, parameters: new[] { SlotParam() }));
        Assert.Empty(inside);
        Assert.Null(MacroJson.ParseAction(text, groups, names, out var macro));
        Assert.Contains(macro, p => p.Message.EndsWith(Strings.Code_BindOnlyComponent));
        Assert.Null(MacroJson.ParseAction(text, groups, names, out var wrongType, parameters: new[] { Ms("Slot") }));
        Assert.NotEmpty(wrongType);
    }

    [Fact]
    public void One_call_s_Code_view_checks_its_arguments_against_the_Library()
    {
        var strip = Strip(SlotParam());
        var groups = new Guid?[] { null };
        var names = new Dictionary<Guid, string>();
        var signatures = new ComponentSignatures(new[] { strip });

        Assert.NotNull(MacroJson.ParseAction(MacroJson.Serialize(Call(strip, ("Slot", Point(1, 2)))), groups, names, out _, components: signatures));
        Assert.Null(MacroJson.ParseAction(MacroJson.Serialize(Call(strip)), groups, names, out var missing, components: signatures));
        Assert.Contains(missing, p => p.Message.EndsWith(string.Format(Strings.Code_ArgumentMissing, "Strip one slot", "Slot")));
        // The places of the Ifs around it, as the window gives them.
        Guid around = Guid.NewGuid();
        Assert.NotNull(MacroJson.ParseAction(MacroJson.Serialize(Call(strip, ("Slot", Found(around)))), groups, names, out _,
            found: new Dictionary<Guid, string> { [around] = "Button" }, components: signatures));
        Assert.Null(MacroJson.ParseAction(MacroJson.Serialize(Call(strip, ("Slot", Found(around)))), groups, names, out _, components: signatures));
    }
}
