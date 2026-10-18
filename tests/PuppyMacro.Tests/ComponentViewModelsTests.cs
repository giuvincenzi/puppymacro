using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The view models of components as functions (no WPF window, only their logic): where a field's value comes from
/// (SlotChoice, SlotBindings), the action box inside a component (Position with parameters, the Bind it writes, the key
/// field, the checks skipped for a bound field), a condition inside a component and the Component condition, the If's wait
/// from a parameter, a call's arguments (ArgumentViewModel: each source, its JSON, its problem) and a parameter's value
/// (ValueEditorViewModel: every type read and written back, its rule).
/// </summary>
public class ComponentViewModelsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static ComponentParameter Ms(string name, JsonNode? @default = null) => Param(name, ParameterType.Number, Number(30), @default, NumberUnit.Ms);

    private static ComponentParameter Seconds(string name) => Param(name, ParameterType.Number, Number(2), unit: NumberUnit.Seconds);

    // ================= SlotChoice and SlotBindings =================

    [Fact]
    public void A_slot_offers_only_parameters_of_its_type_and_unit()
    {
        var parameters = new[] { Ms("Hold"), Seconds("Wait"), SlotParam(), Ms("Settle") };
        var bindings = new SlotBindings(ComponentSlots.Of(Press()), parameters, null);

        Assert.Equal(new[] { Strings.Param_AValue, "From Hold", "From Settle" }, bindings.HoldMs.Choices);
        Assert.Equal(new[] { Strings.Param_AValue, "From Hold", "From Settle" }, bindings.DelayMs.Choices);
        Assert.False(bindings.Key.Show); // no Key parameter
        Assert.True(bindings.HoldMs.Show);
        Assert.False(bindings.Point.Show); // a Press has no Point slot
        Assert.False(bindings.Images.Show);
    }

    [Fact]
    public void A_slot_starts_from_its_Bind_and_its_index_changes_what_shows()
    {
        var bindings = new SlotBindings(ComponentSlots.Of(Press()), new[] { Ms("Hold"), Ms("Settle") },
            new Dictionary<string, string> { ["HoldMs"] = "Settle" });
        var hold = bindings.HoldMs;
        Assert.Equal(2, hold.Index);
        Assert.True(hold.IsBound);
        Assert.False(hold.ShowValue);
        Assert.Equal("Settle", hold.Parameter);

        var changed = new List<string?>();
        hold.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        hold.Index = 0;
        Assert.True(hold.ShowValue);
        Assert.Null(hold.Parameter);
        Assert.Contains(nameof(SlotChoice.ShowValue), changed);

        hold.Index = 99; // clamped to the last
        Assert.Equal("Settle", hold.Parameter);
        changed.Clear();
        hold.Index = 2; // the same: no change
        Assert.Empty(changed);
    }

    [Fact]
    public void A_Bind_to_a_parameter_that_no_longer_fits_starts_from_A_value()
    {
        var bindings = new SlotBindings(ComponentSlots.Of(Press()), new[] { Ms("Hold") }, new Dictionary<string, string> { ["HoldMs"] = "Gone" });
        Assert.Equal(0, bindings.HoldMs.Index);
    }

    [Fact]
    public void Outside_a_component_no_slot_offers_anything()
    {
        Assert.False(SlotBindings.None.HoldMs.Show);
        Assert.False(new SlotBindings(ComponentSlots.Of(Press()), null, null).HoldMs.Show);
        Assert.Empty(SlotBindings.None.All);
        Assert.Null(SlotBindings.None.ToBind(ComponentSlots.Of(Press())));
        Assert.Equal("HoldMs", SlotBindings.None["HoldMs"].Slot);
    }

    [Fact]
    public void The_Bind_written_keeps_only_slots_the_action_uses_and_adds_the_extras()
    {
        var wait = If(Pixel());
        var parameters = new[] { Seconds("Wait"), Ms("Delay") };
        var bindings = new SlotBindings(ComponentSlots.Of(wait), parameters, new Dictionary<string, string> { ["WaitSeconds"] = "Wait", ["DelayMs"] = "Delay" });

        // Wait Once: WaitSeconds isn't used, so it isn't written.
        Assert.Equal(new Dictionary<string, string> { ["DelayMs"] = "Delay" }, bindings.ToBind(ComponentSlots.Of(wait)));
        wait.If!.Wait = ConditionWait.UpTo;
        Assert.Equal(2, bindings.ToBind(ComponentSlots.Of(wait))!.Count);
        var extra = bindings.ToBind(ComponentSlots.Of(wait).Where(s => s.Name == "DelayMs"), new[] { KeyValuePair.Create("Point", "Slot") })!;
        Assert.Equal("Slot", extra["Point"]);
        Assert.False(extra.ContainsKey("WaitSeconds"));
    }

    // ================= The action box =================

    private static ActionBoxViewModel Box(MacroAction action, IReadOnlyList<ComponentParameter>? parameters, IReadOnlyDictionary<Guid, string>? founds = null)
    {
        var box = new ActionBoxViewModel(action);
        if (founds != null)
            box.SetFounds(founds);
        box.SetParameters(parameters, action);
        return box;
    }

    [Fact]
    public void Position_lists_the_fixed_point_then_the_Point_parameters_then_the_found_places()
    {
        Guid found = Guid.NewGuid();
        var box = Box(Click(), new[] { SlotParam(), SlotParam("Other"), Ms("Hold") }, new Dictionary<Guid, string> { [found] = "Button" });

        Assert.Equal(new[] { Strings.Action_PositionFixed, "From Slot", "From Other", "Where Button was found" }, box.PositionChoices);
        Assert.True(box.HasPointParameters);
        Assert.True(box.ShowFoundPosition);
        Assert.False(box.ShowPositionFirst);
        Assert.True(box.ShowFixedPoint);

        box.PositionIndex = 2;
        Assert.True(box.UsesParameter);
        Assert.False(box.UsesFound);
        Assert.False(box.ShowFixedPoint);
        Assert.Equal(2, box.PositionIndex);
        Assert.Contains("at Other", box.Summary);

        box.PositionIndex = 3;
        Assert.True(box.UsesFound);
        Assert.False(box.UsesParameter);
        Assert.Equal(3, box.PositionIndex);

        box.PositionIndex = 0;
        Assert.False(box.UsesFound || box.UsesParameter);
        Assert.True(box.ShowFixedPoint);
    }

    [Fact]
    public void Position_starts_from_the_action_s_Bind_and_writes_it_back()
    {
        var click = Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold"), ("DelayMs", "Delay"));
        var box = Box(click, new[] { SlotParam(), Ms("Hold"), Ms("Delay") });

        Assert.Equal(1, box.PositionIndex);
        Assert.Equal(1, box.Slots.HoldMs.Index);

        var saved = box.ToMacroAction(click);
        Assert.Equal("Slot", saved.Bind!["Point"]);
        Assert.Equal("Hold", saved.Bind["HoldMs"]);
        Assert.Equal("Delay", saved.Bind["DelayMs"]); // the window's own slot, kept as it was
        Assert.Null(saved.FoundId);

        box.PositionIndex = 0;
        box.Slots.HoldMs.Index = 0;
        var unbound = box.ToMacroAction(click);
        Assert.False(unbound.Bind!.ContainsKey("Point"));
        Assert.False(unbound.Bind.ContainsKey("HoldMs"));
    }

    [Fact]
    public void Without_parameters_or_founds_a_macro_click_has_its_plain_Position()
    {
        var box = Box(Click(), new[] { Ms("Hold") });
        Assert.False(box.HasPointParameters);
        Assert.False(box.ShowFoundPosition);
        Assert.True(box.ShowPositionFirst);
        Assert.Null(box.ToMacroAction(Click()).Bind);

        var move = Box(new MacroAction { Type = MacroActionType.MoveTo }, new[] { SlotParam() });
        Assert.False(move.ShowHeaderPoint); // the Position list instead
        Assert.True(Box(new MacroAction { Type = MacroActionType.MoveTo }, null).ShowHeaderPoint);
    }

    [Fact]
    public void A_bound_key_hides_the_key_field_and_needs_no_key()
    {
        var press = new MacroAction { Type = MacroActionType.PressKey };
        var box = Box(press, new[] { Param("Key", ParameterType.Key, Key(0x41)) });
        Assert.True(box.KeyFieldShown);
        Assert.Equal(Strings.Action_ChooseKey, box.Problem);

        box.Slots.Key.Index = 1;
        Assert.False(box.KeyFieldShown);
        Assert.Null(box.Problem);
        Assert.Equal("Key", box.ToMacroAction(press).Bind!["Key"]);
    }

    [Fact]
    public void A_bound_text_needs_no_text()
    {
        var paste = new MacroAction { Type = MacroActionType.PasteText };
        var box = Box(paste, new[] { Param("Word", ParameterType.Text, Text("hi")) });
        Assert.Equal(Strings.Action_EnterText, box.Problem);
        box.Slots.Text.Index = 1;
        Assert.Null(box.Problem);
    }

    [Fact]
    public void Choosing_a_parameter_for_a_field_is_a_change_of_the_box()
    {
        var box = Box(Press(), new[] { Ms("Hold") });
        var changed = new List<string?>();
        box.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        box.Slots.HoldMs.Index = 1;
        Assert.Contains(nameof(ActionBoxViewModel.Problem), changed);
    }

    // ================= Conditions =================

    private MacroLibrary LibraryWith(params LibraryComponent[] components)
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        macros.Library.Collections.Add(new LibraryCollection { Name = "D4", Components = components.ToList() });
        return macros;
    }

    private static LibraryComponent Rare()
    {
        var pixel = Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"));
        var component = Comp("Rare item in slot", new[] { SlotParam() }, If(pixel));
        component.Returns = new ComponentReturns { Found = pixel.Id };
        return component;
    }

    [Fact]
    public void A_condition_s_fields_offer_the_parameters_of_their_kind()
    {
        var context = new ConditionContext { Parameters = new[] { SlotParam(), Param("Color", ParameterType.Color, Text("#112233")) } };
        var pixel = new ConditionViewModel(Pixel(), null, context);
        Assert.True(pixel.Slots.Point.Show);
        Assert.True(pixel.Slots.Color.Show);
        Assert.False(pixel.Slots.Tolerance.Show);

        var image = new ConditionViewModel(ImageFinder(), null, context);
        Assert.False(image.Slots.Images.Show);
        Assert.False(new ConditionViewModel(Pixel(), null).Slots.Point.Show); // a macro: none
    }

    [Fact]
    public void A_condition_writes_the_Bind_of_the_fields_it_uses()
    {
        var area = Param("Where", ParameterType.Area, Area(0, 0, 100, 100));
        var context = new ConditionContext { Parameters = new[] { area } };
        var image = ImageFinder();
        var vm = new ConditionViewModel(image, null, context);
        Assert.False(vm.ShowAreaChoice); // Around: no area to take
        vm.AreaIndex = 2; // An area I draw
        Assert.True(vm.ShowAreaChoice);
        Assert.True(vm.ShowDraw);
        vm.Slots.Area.Index = 1;
        Assert.False(vm.ShowDraw);
        Assert.Equal("Where", vm.ToModel().Bind!["Area"]);

        vm.AreaIndex = 0; // back to Around: the area isn't used
        Assert.Null(vm.ToModel().Bind);
    }

    [Fact]
    public void A_bound_field_of_a_condition_needs_no_value()
    {
        var context = new ConditionContext
        {
            Parameters = new[] { Param("Color", ParameterType.Color, Text("#112233")), Param("Look", ParameterType.Image, Images(ImageA)), Param("Words", ParameterType.Text, Text("x")) },
        };
        var pixel = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Pixel }, null, context);
        Assert.Equal(Strings.Code_ConditionColor, pixel.Problem);
        pixel.Slots.Color.Index = 1;
        Assert.Null(pixel.Problem);

        var image = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Image, Name = "A", Similarity = 90 }, null, context);
        Assert.Equal(Strings.Code_TakeImage, image.Problem);
        image.Slots.Images.Index = 1;
        Assert.Null(image.Problem);

        var text = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Text, Name = "T", Area = SearchArea.Drawn, AreaWidth = 100, AreaHeight = 100 }, null, context);
        Assert.Equal(Strings.Code_EnterConditionText, text.Problem);
        text.Slots.Text.Index = 1;
        Assert.Null(text.Problem);
    }

    [Fact]
    public void A_Component_condition_says_what_is_missing_until_it_can_be_saved()
    {
        var rare = Rare();
        var plain = Comp("Plain", Press());
        var macros = LibraryWith(rare, plain);
        var context = new ConditionContext { Macros = macros };

        var none = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "C" }, null, context);
        Assert.Equal(Strings.Code_ChooseComponent, none.Problem);
        Assert.Equal(Strings.Condition_NoComponent, none.ComponentName);
        Assert.False(none.CanCheck);

        var missing = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "C", ComponentId = Guid.NewGuid() }, null, context);
        Assert.Equal(Strings.Code_ComponentMissing, missing.Problem);
        Assert.Equal(Strings.Code_ComponentMissing, missing.ComponentName);

        var noReturns = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "C", ComponentId = plain.Id }, null, context);
        Assert.Contains("Plain", noReturns.Problem);

        var unnamed = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = " ", ComponentId = rare.Id }, null, context);
        Assert.Equal(Strings.Code_EnterConditionName, unnamed.Problem);
    }

    [Fact]
    public void Choosing_a_component_gives_its_arguments_from_its_examples()
    {
        var rare = Rare();
        var context = new ConditionContext { Macros = LibraryWith(rare) };
        var vm = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "Rare" }, null, context);
        vm.ChooseComponent(rare);

        Assert.True(vm.IsComponent);
        Assert.True(vm.HasArguments);
        Assert.Equal("Slot", Assert.Single(vm.Arguments).Name);
        Assert.Null(vm.Problem);
        Assert.True(vm.CanCheck);
        Assert.Equal("Rare item in slot", vm.ComponentName);
        Assert.Contains("Rare item in slot", vm.Summary);

        var model = vm.ToModel();
        Assert.Equal(rare.Id, model.ComponentId);
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), model.Arguments!["Slot"]));
        Assert.Null(model.Bind);
        Assert.Null(model.Color);

        vm.Arguments[0].Value.X = double.NaN; // still a number: NaN is written as 0
        Assert.Null(vm.Problem);
        vm.Not = true;
        Assert.Contains("Rare item in slot", vm.Summary);
        Assert.Equal(new[] { Strings.Condition_ComponentFinds, Strings.Condition_ComponentFindsNot }, vm.NotChoices);
    }

    [Fact]
    public void A_Component_condition_with_a_bad_argument_can_t_be_saved_nor_checked()
    {
        var number = Comp("Wait for", new[] { Ms("Hold") }, If(Pixel()));
        number.Returns = new ComponentReturns { Found = number.Actions[0].If!.Conditions[0].Id };
        var context = new ConditionContext { Macros = LibraryWith(number) };
        var vm = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "W", ComponentId = number.Id,
            Arguments = new() { ["Hold"] = Number(-5) } }, null, context);
        Assert.Contains("Hold", vm.Problem);
        Assert.False(vm.CanCheck);
    }

    [Fact]
    public void Kinds_have_their_names_and_menu_names()
    {
        Assert.Equal(Strings.Condition_Component, ConditionViewModel.KindName(ConditionKind.Component));
        Assert.Equal(Strings.Condition_ComponentMenu, ConditionViewModel.MenuName(ConditionKind.Component));
        Assert.Equal(Strings.Condition_TextMenu, ConditionViewModel.MenuName(ConditionKind.Text));
        Assert.Equal(Strings.Condition_Pixel, ConditionViewModel.MenuName(ConditionKind.Pixel));
        var vm = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Component, Name = "C" }, null);
        Assert.False(vm.ShowsArea);
        Assert.True(vm.Finds);
    }

    [Fact]
    public void A_non_component_condition_writes_no_call()
    {
        var pixel = Pixel();
        pixel.ComponentId = Guid.NewGuid();
        pixel.Arguments = new() { ["X"] = Number(1) };
        var model = new ConditionViewModel(pixel, null).ToModel();
        Assert.Null(model.ComponentId);
        Assert.Null(model.Arguments);
    }

    [Fact]
    public void The_If_s_wait_can_take_a_seconds_parameter()
    {
        var context = new ConditionContext { Parameters = new[] { Seconds("Wait"), Ms("NotSeconds") } };
        var block = new IfViewModel(new MacroIf { Conditions = { Pixel() }, Wait = ConditionWait.UpTo, WaitSeconds = 3 }, null, context,
            new Dictionary<string, string> { ["WaitSeconds"] = "Wait" });

        Assert.Equal(new[] { Strings.Param_AValue, "From Wait" }, block.Slots.WaitSeconds.Choices);
        Assert.True(block.ShowSecondsChoice);
        Assert.False(block.ShowSecondsValue);
        Assert.Equal("Wait", block.WaitBind()!["WaitSeconds"]);

        block.WaitSeconds = double.NaN; // the number isn't used while bound
        Assert.Null(block.Problem);
        block.Slots.WaitSeconds.Index = 0;
        Assert.True(block.ShowSecondsValue);
        Assert.Equal(Strings.Condition_WaitSeconds, block.Problem);

        block.Slots.WaitSeconds.Index = 1;
        block.WaitIndex = (int)ConditionWait.Once;
        Assert.False(block.ShowSecondsChoice);
        Assert.Null(block.WaitBind()); // no wait: nothing to take
    }

    [Fact]
    public void An_If_in_a_macro_has_no_wait_choice()
    {
        var block = new IfViewModel(new MacroIf { Conditions = { Pixel() }, Wait = ConditionWait.UpTo, WaitSeconds = 3 }, null);
        Assert.False(block.ShowSecondsChoice);
        Assert.True(block.ShowSecondsValue);
        Assert.Null(block.WaitBind());
    }

    // ================= A call's arguments =================

    private static ArgumentViewModel Argument(ComponentParameter parameter, JsonNode? argument, IReadOnlyList<ComponentParameter>? outer = null,
        IReadOnlyDictionary<Guid, string>? found = null, ValueRule? rule = null) =>
        new(parameter, rule ?? ComponentValues.BaseRule(parameter.Type, parameter.Unit), argument, outer, found ?? new Dictionary<Guid, string>(), null);

    [Fact]
    public void An_argument_offers_a_value_its_default_parameters_of_its_kind_and_for_a_point_the_found_places()
    {
        Guid button = Guid.NewGuid();
        var found = new Dictionary<Guid, string> { [button] = "Button" };
        var outer = new[] { SlotParam("Where"), Ms("Hold"), SlotParam("There") };

        var point = Argument(SlotParam(), null, outer, found);
        Assert.Equal(new[] { Strings.Param_AValue, "From Where", "From There", "Where Button was found" }, point.Choices);
        Assert.True(point.ShowChoices);

        var withDefault = Argument(Ms("Settle", Number(100)), null, outer, found);
        Assert.Equal(new[] { Strings.Param_AValue, "Its default, 100 ms", "From Hold" }, withDefault.Choices); // no Found: not a point

        var alone = Argument(Param("Word", ParameterType.Text, Text("x")), null);
        Assert.False(alone.ShowChoices);
    }

    [Fact]
    public void An_argument_starts_from_how_the_call_gives_it()
    {
        Guid button = Guid.NewGuid();
        var found = new Dictionary<Guid, string> { [button] = "Button" };
        var outer = new[] { SlotParam("Where") };

        Assert.Equal(0, Argument(SlotParam(), Point(1, 2), outer, found).Index);
        Assert.Equal(1, Argument(SlotParam(), From("Where"), outer, found).Index);
        var place = Argument(SlotParam(), Found(button, 3, -4), outer, found);
        Assert.Equal(2, place.Index);
        Assert.True(place.ShowOffset);
        Assert.Equal((3d, -4d), (place.OffsetX, place.OffsetY));
        Assert.Equal(1, Argument(Ms("Settle", Number(100)), null).Index); // left out: its default
        Assert.Equal(0, Argument(Ms("Settle"), null).Index); // required: a value
        Assert.Equal(0, Argument(SlotParam(), From("Gone"), outer, found).Index); // a parameter it can't find
    }

    [Fact]
    public void Each_source_is_written_as_the_call_writes_it()
    {
        Guid button = Guid.NewGuid();
        var found = new Dictionary<Guid, string> { [button] = "Button" };
        var point = Argument(SlotParam(), Point(5, 6), new[] { SlotParam("Where") }, found);

        Assert.True(JsonNode.DeepEquals(Point(5, 6), point.ToJson()));
        point.Index = 1;
        Assert.True(JsonNode.DeepEquals(From("Where"), point.ToJson()));
        Assert.Null(point.Problem); // a reference: no value to check
        point.Index = 2;
        point.OffsetX = 7;
        point.OffsetY = double.NaN;
        Assert.True(JsonNode.DeepEquals(Found(button, 7, 0), point.ToJson()));

        var withDefault = Argument(Ms("Settle", Number(100)), null);
        Assert.Null(withDefault.ToJson()); // left out
        withDefault.Index = 0;
        withDefault.Value.Number = 250;
        Assert.True(JsonNode.DeepEquals(Number(250), withDefault.ToJson()));
    }

    [Fact]
    public void An_argument_s_value_keeps_the_rule_of_its_parameter()
    {
        var hold = Argument(Ms("Hold"), Number(0), rule: ValueRule.Number(MacroJson.HoldMs));
        Assert.StartsWith("Check the value of Hold.", hold.Problem);
        hold.Value.Number = 40;
        Assert.Null(hold.Problem);
    }

    [Fact]
    public void An_argument_says_what_its_parameter_is()
    {
        var described = SlotParam();
        described.Description = "The inventory slot";
        Assert.Equal("The inventory slot · Point · " + Strings.Param_Required, Argument(described, null).Description);
        Assert.Equal("Number (ms) · 100 ms when left out", Argument(Ms("Settle", Number(100)), null).Description);
    }

    [Fact]
    public void A_call_s_cards_follow_its_component_and_go_back_to_arguments()
    {
        var component = Comp("Strip", new[] { SlotParam(), Ms("Settle", Number(100)) }, Press());
        var cards = ArgumentViewModel.For(component, new() { ["Slot"] = Point(1, 2) }, new ComponentSignatures(new[] { component }),
            null, new Dictionary<Guid, string>(), null);

        Assert.Equal(new[] { "Slot", "Settle" }, cards.Select(c => c.Name));
        var arguments = ArgumentViewModel.ToArguments(cards)!;
        Assert.Equal(new[] { "Slot" }, arguments.Keys); // Settle keeps its default
        Assert.Null(ArgumentViewModel.ToArguments(Array.Empty<ArgumentViewModel>()));
    }

    [Fact]
    public void A_new_call_starts_with_the_examples_of_its_required_parameters()
    {
        var component = Comp("Strip", new[] { SlotParam(), Ms("Settle", Number(100)) }, Press());
        var starting = ArgumentViewModel.Starting(component)!;
        Assert.Equal(new[] { "Slot" }, starting.Keys);
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), starting["Slot"]));
        Assert.NotSame(component.Parameters[0].Example, starting["Slot"]); // a copy
        Assert.Null(ArgumentViewModel.Starting(Comp("None", Press())));
        Assert.Null(ArgumentViewModel.Starting(Comp("Optional", new[] { Ms("Settle", Number(100)) }, Press())));
    }

    // ================= A parameter's value =================

    [Fact]
    public void Every_type_s_value_is_read_and_written_back_the_same()
    {
        foreach (var (type, unit, node) in new (ParameterType, NumberUnit?, JsonNode)[]
                 {
                     (ParameterType.Point, null, Point(3, 4)), (ParameterType.Number, NumberUnit.Ms, Number(2.5)),
                     (ParameterType.Text, null, Text("hi")), (ParameterType.Color, null, Text("#112233")),
                     (ParameterType.Image, null, Images(ImageA)), (ParameterType.Area, null, Area(1, 2, 30, 40)),
                 })
        {
            var value = new ValueEditorViewModel(type, unit, node, null);
            Assert.True(JsonNode.DeepEquals(node, value.ToJson()), $"{type}: {value.ToJson()?.ToJsonString()}");
            Assert.Null(value.Problem);
        }
        var key = new ValueEditorViewModel(ParameterType.Key, null, Key(0x53, ctrl: true), null);
        Assert.Equal(0x53, key.Key!.Vk);
        Assert.True(key.Key.Ctrl);
        Assert.NotNull(key.ToJson());
    }

    [Fact]
    public void A_value_of_another_type_starts_empty()
    {
        var point = new ValueEditorViewModel(ParameterType.Point, null, Text("x"), null);
        Assert.True(JsonNode.DeepEquals(Point(0, 0), point.ToJson()));
        var key = new ValueEditorViewModel(ParameterType.Key, null, null, null);
        Assert.Null(key.ToJson());
        Assert.Equal(Strings.Code_ValueKey, key.Problem);
        var area = new ValueEditorViewModel(ParameterType.Area, null, null, null);
        Assert.Null(area.ToJson());
        Assert.Equal(Strings.Condition_AreaNotDrawn, area.AreaText);
        var images = new ValueEditorViewModel(ParameterType.Image, null, null, null);
        Assert.Equal(Strings.Condition_ImageNotTaken, images.ImagesText);
        Assert.Equal(Strings.Code_ValueImage, images.Problem);
    }

    [Fact]
    public void A_value_keeps_its_rule_and_an_empty_number_is_a_problem()
    {
        var hold = new ValueEditorViewModel(ParameterType.Number, NumberUnit.Ms, Number(0), null, ValueRule.Number(MacroJson.HoldMs));
        Assert.NotNull(hold.Problem);
        hold.Number = double.NaN;
        Assert.Null(hold.ToJson());
        Assert.Equal(Strings.Code_ValueNumber, hold.Problem);
        hold.Number = 40;
        Assert.Null(hold.Problem);
        Assert.Equal("40 ms", hold.Description);
        Assert.Equal("ms", hold.UnitText);

        var text = new ValueEditorViewModel(ParameterType.Text, null, Text(""), null, new ValueRule { NotEmpty = true });
        Assert.Equal(Strings.Code_ValueTextEmpty, text.Problem);
    }

    [Fact]
    public void A_value_shows_its_kind_and_what_it_holds()
    {
        var images = new ValueEditorViewModel(ParameterType.Image, null, Images(ImageA, ImageB), null);
        Assert.Equal("2 images", images.ImagesText);
        Assert.True(images.IsImage);
        Assert.False(images.IsPoint || images.IsNumber || images.IsText || images.IsKey || images.IsColor || images.IsArea);

        var area = new ValueEditorViewModel(ParameterType.Area, null, Area(1, 2, 30, 40), null);
        Assert.Contains("30", area.AreaText);
        area.Area = new AreaValue(5, 6, 70, 80);
        Assert.True(JsonNode.DeepEquals(Area(5, 6, 70, 80), area.ToJson()));

        var color = new ValueEditorViewModel(ParameterType.Color, null, Text("#aabbcc"), null);
        Assert.True(JsonNode.DeepEquals(Text("#AABBCC"), color.ToJson())); // written in capitals
        Assert.Equal("", new ValueEditorViewModel(ParameterType.Point, null, null, null).UnitText);

        var point = new ValueEditorViewModel(ParameterType.Point, null, Point(1, 2), null);
        point.X = 10.6;
        Assert.True(JsonNode.DeepEquals(Point(11, 2), point.ToJson())); // whole pixels
        Assert.Equal("11, 2", point.Description);
    }

    [Fact]
    public void A_missing_image_is_a_problem_with_the_store()
    {
        var store = new ImageStore(_folder.File("images"));
        var images = new ValueEditorViewModel(ParameterType.Image, null, Images(ImageA), store);
        Assert.Equal(Strings.Code_ImageMissing, images.Problem);
    }
}
