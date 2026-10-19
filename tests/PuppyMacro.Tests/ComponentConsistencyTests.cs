using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// One answer for one thing, wherever it is asked: a field bound to a parameter that the action window's form accepts is
/// accepted by the same window's Code view; a row's arguments that the row's window accepts are accepted by the macro's
/// checks (also the cells a Grid value reaches); and the passing cases next to them. No input is sent.
/// </summary>
public class ComponentConsistencyTests
{
    private static JsonNode Grid(int firstX, int firstY, int lastX, int lastY, int columns, int rows) => new JsonObject
    {
        ["First"] = Point(firstX, firstY), ["Last"] = Point(lastX, lastY), ["Columns"] = columns, ["Rows"] = rows,
    };

    private static JsonNode Inventory => Grid(3912, 1056, 4647, 1268, 11, 3);

    private static ComponentParameter GridParam(string name = "Inv") => Param(name, ParameterType.Grid, Inventory);

    private static ComponentParameter CountParam(string name = "Slot", double example = 1) =>
        Param(name, ParameterType.Number, Number(example), unit: NumberUnit.Count);

    private static MacroAction CellClick(int cell = 1, string? cellFrom = null)
    {
        var click = Bound(Click(3912, 1056), ("Point", "Inv"));
        click.Cell = cell;
        if (cellFrom != null)
            click.Bind!["Cell"] = cellFrom;
        return click;
    }

    /// <summary>What the action window's Code view says of the action its Form view gives (MacroActionWindow: FormAction, ParseAction).</summary>
    private static List<string> CodeViewProblems(MacroAction action, IReadOnlyList<ComponentParameter> parameters)
    {
        MacroJson.ParseAction(MacroJson.Serialize(action), new Guid?[] { null }, new Dictionary<Guid, string>(), out var problems,
            parameters: parameters);
        return problems.Select(p => $"{p.Line}:{p.Message}").ToList();
    }

    // ================= The action window: Form and Code view =================

    [Fact]
    public void A_press_key_whose_key_comes_from_a_parameter_is_accepted_by_the_Code_view_as_by_the_form()
    {
        var parameters = new[] { Param("Hotkey", ParameterType.Key, Key(0x87)) };
        var original = new MacroAction { Type = MacroActionType.PressKey };
        var box = new ActionBoxViewModel(original);
        box.SetParameters(parameters, original);
        box.Slots.Key.Index = 1; // "From Hotkey": the key field hides, no key is chosen

        Assert.Null(box.Problem);
        var action = box.ToMacroAction(original);
        Assert.Equal("Hotkey", action.Bind!["Key"]);
        Assert.Empty(CodeViewProblems(action, parameters));
    }

    [Fact]
    public void A_paste_whose_text_comes_from_a_parameter_is_accepted_by_the_Code_view_as_by_the_form()
    {
        var parameters = new[] { Param("Words", ParameterType.Text, Text("hello")) };
        var original = new MacroAction { Type = MacroActionType.PasteText };
        var box = new ActionBoxViewModel(original);
        box.SetParameters(parameters, original);
        box.Slots.Text.Index = 1;

        Assert.Null(box.Problem);
        Assert.Empty(CodeViewProblems(box.ToMacroAction(original), parameters));
    }

    [Fact]
    public void A_pixel_whose_color_comes_from_a_parameter_is_accepted_by_the_Code_view_as_by_the_form()
    {
        var parameters = new[] { Param("Shade", ParameterType.Color, Text("#FF0000")) };
        var pixel = new ConditionViewModel(new MacroCondition { Kind = ConditionKind.Pixel }, null, new ConditionContext { Parameters = parameters });
        pixel.Slots.Color.Index = 1;

        Assert.Null(pixel.Problem);
        Assert.Empty(CodeViewProblems(If(pixel.ToModel(), Press()), parameters));
    }

    [Fact]
    public void A_field_given_its_own_value_is_accepted_by_both_views()
    {
        // The passing side: a key chosen in the field, no parameter.
        var parameters = new[] { Param("Hotkey", ParameterType.Key, Key(0x87)) };
        var original = new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87 };
        var box = new ActionBoxViewModel(original);
        box.SetParameters(parameters, original);

        Assert.Null(box.Problem);
        var action = box.ToMacroAction(original);
        Assert.Null(action.Bind);
        Assert.Empty(CodeViewProblems(action, parameters));
    }

    [Fact]
    public void A_bound_field_holds_its_example_once_the_component_is_built_so_the_component_s_Code_view_accepts_it()
    {
        // The component editor writes the Examples before its checks (BuildComponent): there a bound key needs no value.
        var parameters = new List<ComponentParameter> { Param("Hotkey", ParameterType.Key, Key(0x87)) };
        var component = Comp("Press it", parameters, Bound(new MacroAction { Type = MacroActionType.PressKey }, ("Key", "Hotkey")));
        ComponentEdits.WriteExamples(component);
        Assert.Equal(0x87, component.Actions[0].Vk);
        Assert.Empty(ComponentProblems(component));
    }

    // ================= A row's window and the macro's checks =================

    private static (LibraryComponent Component, MacroDefinition Macro) NinthCell(JsonNode grid)
    {
        var component = Comp("Ninth", new[] { GridParam() }, CellClick(cell: 9));
        var macro = Macro("Uses ninth", Call(component, ("Inv", grid)));
        return (component, macro);
    }

    [Fact]
    public void A_grid_value_whose_cells_the_component_reaches_is_accepted_by_the_row_s_window_and_the_macro()
    {
        var (component, macro) = NinthCell(Grid(0, 0, 20, 20, 3, 3)); // 9 cells: cell 9 is there
        var signatures = new ComponentSignatures(new[] { component });
        var cards = ArgumentViewModel.For(component, macro.Actions[0].Arguments, signatures, null, new Dictionary<Guid, string>(), null);

        Assert.All(cards, c => Assert.Null(c.Problem));
        Assert.Empty(MacroProblems(macro, component));
    }

    [Fact]
    public void A_grid_value_without_the_cell_the_component_clicks_is_refused_by_the_row_s_window_as_by_the_macro()
    {
        var (component, macro) = NinthCell(Grid(0, 0, 10, 10, 2, 2)); // 4 cells: the component clicks cell 9
        var signatures = new ComponentSignatures(new[] { component });

        // The macro's checks (its editor, its Code view, the Library's save) refuse the call.
        Assert.NotEmpty(MacroProblems(macro, component));
        // The row's window (ComponentWindow: the cards' Problem) must refuse it too, before its Save.
        var cards = ArgumentViewModel.For(component, macro.Actions[0].Arguments, signatures, null, new Dictionary<Guid, string>(), null);
        Assert.Contains(cards, c => c.Problem != null);
    }

    [Fact]
    public void A_cell_number_past_the_grid_is_refused_by_the_row_s_window_as_by_the_macro()
    {
        var component = Comp("Some cell", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var macro = Macro("Uses a cell", Call(component, ("Inv", Inventory), ("Slot", Number(40)))); // 33 cells
        var signatures = new ComponentSignatures(new[] { component });

        Assert.NotEmpty(MacroProblems(macro, component));
        var cards = ArgumentViewModel.For(component, macro.Actions[0].Arguments, signatures, null, new Dictionary<Guid, string>(), null);
        Assert.Contains(cards, c => c.Problem != null);
    }

    [Fact]
    public void A_cell_number_inside_the_grid_is_accepted_by_both()
    {
        var component = Comp("Some cell", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var macro = Macro("Uses a cell", Call(component, ("Inv", Inventory), ("Slot", Number(33))));
        var signatures = new ComponentSignatures(new[] { component });

        Assert.Empty(MacroProblems(macro, component));
        var cards = ArgumentViewModel.For(component, macro.Actions[0].Arguments, signatures, null, new Dictionary<Guid, string>(), null);
        Assert.All(cards, c => Assert.Null(c.Problem));
    }

    [Fact]
    public void A_value_past_a_limit_that_rose_through_From_is_refused_by_the_row_s_window_as_by_the_macro()
    {
        // Passing: the rule of Hold rises from the inner component's HoldMs (1 to 60 000) through {"From"}; the row's
        // window uses the same rule as the checks.
        var press = Comp("Press", new[] { HoldParam("Hold") }, Bound(Press(), ("HoldMs", "Hold")));
        var outer = Comp("Outer", new[] { HoldParam("Long") }, Call(press, ("Hold", From("Long"))));
        var macro = Macro("Uses outer", Call(outer, ("Long", Number(0))));
        var signatures = new ComponentSignatures(new[] { press, outer });

        Assert.NotEmpty(MacroProblems(macro, press, outer));
        var cards = ArgumentViewModel.For(outer, macro.Actions[0].Arguments, signatures, null, new Dictionary<Guid, string>(), null);
        Assert.NotNull(cards.Single().Problem);
    }
}
