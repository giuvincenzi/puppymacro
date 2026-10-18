using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The Grid parameter at its edges and where it meets the rest of components as functions: the extreme grids and their
/// cells, the arguments that can't give a grid, Component conditions whose returned pixel is at a cell, Unlink and Save as
/// component of calls with grids passed on, the guided update when a parameter becomes a Grid or stops being one, the
/// fingerprint and the Code view, and the view models' cell fields with odd numbers. No input is sent.
/// </summary>
public class ComponentGridEdgesTests
{
    private static readonly Dictionary<Guid, Point> Nothing = new();

    private static JsonNode Grid(int firstX, int firstY, int lastX, int lastY, int columns, int rows) => new JsonObject
    {
        ["First"] = Point(firstX, firstY), ["Last"] = Point(lastX, lastY), ["Columns"] = columns, ["Rows"] = rows,
    };

    /// <summary>The D4 inventory: 11 × 3, cell 1 at 3912, 1056, cell 33 at 4647, 1268.</summary>
    private static JsonNode Inventory => Grid(3912, 1056, 4647, 1268, 11, 3);

    private static GridValue Read(JsonNode node) =>
        (GridValue)ComponentValues.Read(node, ParameterType.Grid, ValueRule.Any, out _)!;

    private static ComponentParameter GridParam(string name = "Inv", JsonNode? example = null, JsonNode? @default = null) =>
        Param(name, ParameterType.Grid, example ?? Inventory, @default);

    private static ComponentParameter CountParam(string name = "Slot", double example = 1, JsonNode? @default = null) =>
        Param(name, ParameterType.Number, Number(example), @default, NumberUnit.Count);

    private static MacroAction CellClick(string grid = "Inv", int cell = 1, string? cellFrom = null)
    {
        var click = Bound(Click(3912, 1056), ("Point", grid));
        click.Cell = cell;
        if (cellFrom != null)
            click.Bind!["Cell"] = cellFrom;
        return click;
    }

    private static MacroCondition CellPixel(string grid = "Inv", int cell = 1, string? cellFrom = null, bool not = false)
    {
        var pixel = Bound(Pixel(3912, 1056, "#FFFFFF", not), ("Point", grid));
        pixel.Cell = cell;
        if (cellFrom != null)
            pixel.Bind!["Cell"] = cellFrom;
        return pixel;
    }

    private static string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static void Has(string message, IEnumerable<string> problems) =>
        Assert.Contains(problems, p => p == message || p.EndsWith(": " + message, StringComparison.Ordinal));

    /// <summary>
    /// The points a macro's actions play, as the runner reaches them: each action bound in its frame, a linked row played in
    /// a frame of its own (its arguments evaluated in the caller's). "skipped" for an action whose cell isn't in its grid.
    /// </summary>
    private static List<string> PlayedPoints(IEnumerable<MacroAction> actions, ComponentPrograms programs, FrameArguments frame)
    {
        var points = new List<string>();
        foreach (var action in actions)
        {
            if (action.Type == MacroActionType.Component)
            {
                var callee = programs.Find(action.ComponentId!.Value)!;
                var inner = ComponentPrograms.Evaluate(callee, action.Arguments, frame, Nothing, out string? missing);
                if (inner == null)
                {
                    points.Add($"missing {missing}");
                    continue;
                }
                points.AddRange(PlayedPoints(callee.Actions, programs, inner));
                continue;
            }
            var bound = ComponentPrograms.Bound(action, frame);
            points.Add(bound == null ? "skipped" : $"{bound.Type} {bound.X},{bound.Y}");
        }
        return points;
    }

    // ================= Extreme grids =================

    [Fact]
    public void Every_cell_of_a_grid_across_the_whole_coordinate_range_is_inside_it_and_inside_the_limits()
    {
        foreach (var node in new[]
                 {
                     Grid(-100_000, -100_000, 100_000, 100_000, 1000, 10),
                     Grid(100_000, 100_000, -100_000, -100_000, 10, 1000),
                     Grid(-100_000, 100_000, 100_000, -100_000, 100, 100),
                 })
        {
            var grid = Read(node);
            Assert.Equal(GridValue.MaxCells, grid.Cells);
            Assert.Equal(grid.First, grid.PointOf(1));
            Assert.Equal(grid.Last, grid.PointOf(grid.Cells));
            Assert.Equal(new PointValue(grid.Last.X, grid.First.Y), grid.PointOf(grid.Columns));
            Assert.Equal(new PointValue(grid.First.X, grid.Last.Y), grid.PointOf(grid.Cells - grid.Columns + 1));
            for (int cell = 1; cell <= grid.Cells; cell++)
            {
                var p = grid.PointOf(cell)!.Value;
                Assert.InRange(p.X, Math.Min(grid.First.X, grid.Last.X), Math.Max(grid.First.X, grid.Last.X));
                Assert.InRange(p.Y, Math.Min(grid.First.Y, grid.Last.Y), Math.Max(grid.First.Y, grid.Last.Y));
            }
            // Each cell is read back in the JSON of the value it came from.
            Assert.Equal(grid, Read(ComponentValues.ToJson(grid)));
        }
    }

    [Fact]
    public void A_thousand_columns_and_a_thousand_rows_count_their_cells_row_by_row()
    {
        var wide = Read(Grid(0, 0, 999, 9, 1000, 10));
        var tall = Read(Grid(0, 0, 9, 999, 10, 1000));
        Assert.Equal(new PointValue(999, 0), wide.PointOf(1000));
        Assert.Equal(new PointValue(0, 1), wide.PointOf(1001));
        Assert.Equal(new PointValue(9, 0), tall.PointOf(10));
        Assert.Equal(new PointValue(0, 1), tall.PointOf(11));
        Assert.Equal(new PointValue(9, 999), tall.PointOf(10_000));
        Assert.Null(tall.PointOf(10_001));
        // 1000 × 11 is past the most cells, 1000 × 10 the most.
        Assert.NotNull(ComponentValues.Read(Grid(0, 0, 999, 9, 1000, 10), ParameterType.Grid, ValueRule.Any, out _));
        Assert.Null(ComponentValues.Read(Grid(0, 0, 999, 10, 1000, 11), ParameterType.Grid, ValueRule.Any, out _));
    }

    [Fact]
    public void A_grid_with_whole_numbers_written_as_decimals_is_read_as_the_same_grid()
    {
        var node = new JsonObject
        {
            ["First"] = new JsonObject { ["X"] = 3912.0, ["Y"] = 1056.0 },
            ["Last"] = new JsonObject { ["X"] = 4647.0, ["Y"] = 1268.0 },
            ["Columns"] = 11.0,
            ["Rows"] = 3.0,
        };
        Assert.Equal(Read(Inventory), Read(node));
    }

    // ================= A component's own values =================

    [Fact]
    public void A_default_grid_without_a_fixed_cell_of_the_component_is_refused_as_its_example_would_be()
    {
        // Every call that leaves the grid out would reach cell 9 of a 2 × 2 grid: none of them could play.
        var component = Comp("Ninth", new[] { GridParam(@default: Grid(0, 0, 10, 10, 2, 2)) }, CellClick(cell: 9));
        Assert.NotEmpty(Problems(new[] { component }, Macro("Leaves it out", Call(component))));
        Has(F(Strings.Code_CellOutside, 9, "Inv", 4, "Ninth"), ComponentProblems(component));
    }

    // ================= Arguments that can't give a grid =================

    [Fact]
    public void A_found_place_can_t_be_given_to_a_Grid_parameter()
    {
        var strip = Comp("Strip", new[] { GridParam() }, CellClick(cell: 2));
        var finder = ImageFinder("Bag");
        var problems = Problems(new[] { strip }, Macro("Found", If(finder, Call(strip, ("Inv", Found(finder.Id))))));
        Has(F(Strings.Code_FoundNotPoint, "Inv", ComponentChecks.TypeName(ParameterType.Grid, null)), problems);
    }

    [Fact]
    public void A_grid_can_t_be_passed_on_to_a_Point_parameter_nor_a_point_to_a_Grid()
    {
        var pointTaker = Comp("Point taker", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        var gridTaker = Comp("Grid taker", new[] { GridParam() }, CellClick(cell: 2));
        var gridToPoint = Comp("Grid to point", new[] { GridParam("Bag") }, Call(pointTaker, ("Slot", From("Bag"))));
        Has(F(Strings.Code_ParameterType, "Bag", ComponentChecks.TypeName(ParameterType.Grid, null), "Slot",
            ComponentChecks.TypeName(ParameterType.Point, null)), ComponentProblems(gridToPoint, pointTaker));
        var pointToGrid = Comp("Point to grid", new[] { SlotParam("Where") }, Call(gridTaker, ("Inv", From("Where"))));
        Has(F(Strings.Code_ParameterType, "Where", ComponentChecks.TypeName(ParameterType.Point, null), "Inv",
            ComponentChecks.TypeName(ParameterType.Grid, null)), ComponentProblems(pointToGrid, gridTaker));
        // A Point given as a value to a Grid parameter, and a grid to a Point.
        Assert.NotEmpty(Problems(new[] { gridTaker }, Macro("Point value", Call(gridTaker, ("Inv", Point(1, 2))))));
        Assert.NotEmpty(Problems(new[] { pointTaker }, Macro("Grid value", Call(pointTaker, ("Slot", Inventory)))));
    }

    [Fact]
    public void A_cell_s_number_takes_only_a_count_and_never_text_a_point_or_a_grid()
    {
        foreach (var other in new[] { Param("Other", ParameterType.Text, Text("x")), SlotParam("Other"), GridParam("Other") })
        {
            var component = Comp("Cell from " + other.Type, new[] { GridParam(), other }, CellClick(cellFrom: "Other"));
            Has(F(Strings.Code_ParameterType, "Other", ComponentChecks.TypeName(other.Type, other.Unit), "Cell",
                ComponentChecks.TypeName(ParameterType.Number, NumberUnit.Count)), ComponentProblems(component));
        }
    }

    [Fact]
    public void A_cell_on_an_image_a_text_or_a_Component_condition_is_refused()
    {
        var rare = Comp("Rare", new[] { GridParam() }, If(CellPixel(cell: 1)));
        rare.Returns = new ComponentReturns { Found = rare.Actions[0].If!.Conditions[0].Id };
        var image = Bound(ImageFinder("Img"), ("Images", "Pic"));
        image.Cell = 1;
        var text = ComponentGenerator.TextCondition("Txt");
        text.Cell = 1;
        var call = CallCondition(rare, "Call", ("Inv", From("Inv")));
        call.Cell = 1;
        foreach (var condition in new[] { image, text, call })
        {
            var component = Comp("With " + condition.Kind, new[] { GridParam(), Param("Pic", ParameterType.Image, Images(ImageA)) }, If(condition));
            Has(Strings.Code_CellWithoutGrid, ComponentProblems(component, rare));
        }
    }

    [Fact]
    public void Every_action_but_a_mouse_action_or_a_move_refuses_a_cell()
    {
        foreach (var type in new[] { MacroActionType.PressKey, MacroActionType.KeyDown, MacroActionType.KeyUp, MacroActionType.Scroll,
                     MacroActionType.PasteText, MacroActionType.If })
        {
            var action = type switch
            {
                MacroActionType.PasteText => new MacroAction { Type = type, Text = "x" },
                MacroActionType.Scroll => new MacroAction { Type = type, ScrollSteps = 1 },
                MacroActionType.If => If(Pixel(), Press()),
                _ => new MacroAction { Type = type, Vk = 0x87 },
            };
            action.Cell = 2;
            Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Cell on " + type, new[] { GridParam() }, action)));
        }
    }

    // ================= Component conditions at a cell =================

    /// <summary>A component that says whether the cell a call gives is white: its pixel's point a cell of its grid.</summary>
    private static LibraryComponent WhiteSlot(bool not = false)
    {
        var pixel = CellPixel("Bag", cellFrom: "Slot", not: not);
        var component = Comp("White slot", new[] { GridParam("Bag"), CountParam() }, If(pixel));
        component.Returns = new ComponentReturns { Found = pixel.Id };
        return component;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_Component_condition_checks_the_cell_its_call_gives(bool returnedNot, bool callNot)
    {
        var white = WhiteSlot(returnedNot);
        var programs = ComponentPrograms.Prepare(new[] { white });
        var call = CallCondition(white, "Slot 13", ("Bag", Inventory), ("Slot", Number(13)));
        call.Not = callNot;
        Assert.Empty(Problems(new[] { white }, Macro("Check", If(call, Press()))));

        var effective = programs.Effective(call, FrameArguments.None, Nothing)!;
        Assert.Equal((ConditionKind.Pixel, 3986, 1162), (effective.Kind, effective.X, effective.Y));
        Assert.Equal(returnedNot ^ callNot, effective.Not);
        Assert.Equal(call.Id, effective.Id);
        Assert.Null(effective.Bind);
        // The prepared component never changes.
        Assert.Equal((3912, 1056), (programs.Find(white.Id)!.Returned!.X, programs.Find(white.Id)!.Returned!.Y));
    }

    [Fact]
    public void A_Component_condition_called_with_a_cell_its_grid_doesn_t_have_can_t_play()
    {
        var white = WhiteSlot();
        var call = CallCondition(white, "Slot 40", ("Bag", Inventory), ("Slot", Number(40)));
        var macro = Macro("Check", If(call, Press()));
        Has(F(Strings.Code_CellOutside, 40, "Bag", 33, "White slot"), Problems(new[] { white }, macro));
        Assert.Contains(macro.Id, ComponentChecks.Unplayable(new ComponentSignatures(new[] { white }), new[] { macro }));
        Assert.Null(ComponentPrograms.Prepare(new[] { white }).Effective(call, FrameArguments.None, Nothing));
    }

    [Fact]
    public void A_Component_condition_is_checked_only_for_the_cells_of_the_condition_it_returns()
    {
        // As a condition only the returned pixel (cell 1) is checked; its Then's click at cell 30 never plays then.
        var pixel = CellPixel("Bag", cell: 1);
        var white = Comp("White first slot", new[] { GridParam("Bag") }, If(pixel, CellClick("Bag", cell: 30)));
        white.Returns = new ComponentReturns { Found = pixel.Id };
        var call = CallCondition(white, "First white", ("Bag", Grid(0, 0, 10, 10, 2, 2)));
        var macro = Macro("Check", If(call, Press()));

        var effective = ComponentPrograms.Prepare(new[] { white }).Effective(call, FrameArguments.None, Nothing);
        Assert.Equal((0, 0), (effective!.X, effective.Y));
        Assert.Empty(Problems(new[] { white }, macro));
        Assert.DoesNotContain(macro.Id, ComponentChecks.Unplayable(new ComponentSignatures(new[] { white }), new[] { macro }));
        // Played as a row, the click is reached: refused.
        Assert.NotEmpty(Problems(new[] { white }, Macro("Row", Call(white, ("Bag", Grid(0, 0, 10, 10, 2, 2))))));
    }

    [Fact]
    public void A_wrapper_passing_its_grid_on_to_a_Component_condition_checks_the_cell_it_reaches()
    {
        var white = WhiteSlot();
        var inner = CallCondition(white, "Inner", ("Bag", From("Pack")), ("Slot", From("Which")));
        var wrapper = Comp("Wrapper", new[] { GridParam("Pack"), CountParam("Which") }, If(inner));
        wrapper.Returns = new ComponentReturns { Found = inner.Id };
        var library = new[] { white, wrapper };

        var good = CallCondition(wrapper, "Outer", ("Pack", Grid(0, 0, 30, 30, 4, 4)), ("Which", Number(16)));
        Assert.Empty(Problems(library, Macro("Good", If(good, Press()))));
        var effective = ComponentPrograms.Prepare(library).Effective(good, FrameArguments.None, Nothing)!;
        Assert.Equal((30, 30), (effective.X, effective.Y));

        var past = CallCondition(wrapper, "Outer", ("Pack", Grid(0, 0, 30, 30, 4, 4)), ("Which", Number(17)));
        Has(F(Strings.Code_CellOutside, 17, "Bag", 16, "White slot"), Problems(library, Macro("Past", If(past, Press()))));
    }

    [Fact]
    public void A_cell_reached_only_through_a_Component_condition_inside_a_component_is_checked_with_the_macro_s_grid()
    {
        var white = WhiteSlot();
        var inner = CallCondition(white, "Inner", ("Bag", From("Pack")), ("Slot", Number(9)));
        var user = Comp("Uses it", new[] { GridParam("Pack", example: Grid(0, 0, 20, 20, 3, 3)) }, If(inner, Press()));
        var library = new[] { white, user };
        Assert.Empty(ComponentProblems(user, white));
        var macro = Macro("Small", Call(user, ("Pack", Grid(0, 0, 10, 10, 2, 2))));
        Has(F(Strings.Code_CellOutside, 9, "Bag", 4, "White slot"), Problems(library, macro));
        Assert.Contains(macro.Id, ComponentChecks.Unplayable(new ComponentSignatures(library), new[] { macro }));
    }

    // ================= Unlink and Save as component =================

    [Fact]
    public void Unlinking_a_call_that_passes_its_grid_down_plays_the_same_points()
    {
        var strip = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var outer = Comp("Strip two", new[] { GridParam("Bag"), CountParam("Which", 2) },
            Call(strip, ("Inv", From("Bag")), ("Slot", From("Which"))),
            CellClick("Bag", cellFrom: "Which"));
        var library = new[] { strip, outer };
        var row = Call(outer, ("Bag", Grid(100, 100, 400, 200, 4, 3)), ("Which", Number(7)));
        var programs = ComponentPrograms.Prepare(library);
        var linked = PlayedPoints(new[] { row }, programs, FrameArguments.None);
        Assert.Equal(new[] { "Click 300,150", "Click 300,150" }, linked);

        var unlinked = ComponentEdits.Unlink(row, outer, out string? problem)!;
        Assert.Null(problem);
        Assert.Empty(Problems(library, Macro("Unlinked", unlinked.ToArray())));
        Assert.Equal(linked, PlayedPoints(unlinked, programs, FrameArguments.None));
    }

    [Fact]
    public void Save_as_component_inside_a_component_keeps_the_cells_and_plays_the_same_points()
    {
        var outerParameters = new List<ComponentParameter> { GridParam("Bag", example: Grid(0, 0, 90, 30, 4, 2)), CountParam("Which", 6) };
        var selected = new List<MacroAction> { CellClick("Bag", cellFrom: "Which"), CellClick("Bag", cell: 8) };
        var (actions, parameters, arguments) = ComponentEdits.Lift(selected, outerParameters, new Dictionary<Guid, string>());
        var lifted = Comp("Lifted", parameters, actions.ToArray());
        ComponentEdits.WriteExamples(lifted);
        Assert.Equal(new[] { ParameterType.Grid, ParameterType.Number }, lifted.Parameters.Select(p => p.Type));
        Assert.Empty(ComponentProblems(lifted));

        var row = new MacroAction { Type = MacroActionType.Component, ComponentId = lifted.Id, Arguments = arguments };
        var around = Comp("Around", outerParameters, row);
        Assert.Empty(ComponentProblems(around, lifted));

        var before = Comp("Before", outerParameters, selected.Select(a => a.Clone()).ToArray());
        var frame = FrameArguments.Examples(outerParameters);
        var programs = ComponentPrograms.Prepare(new[] { lifted });
        Assert.Equal(PlayedPoints(before.Actions, programs, frame), PlayedPoints(around.Actions, programs, frame));
        Assert.Equal(new[] { "Click 30,30", "Click 90,30" }, PlayedPoints(around.Actions, programs, frame));
    }

    // ================= The guided update =================

    [Fact]
    public void A_Point_parameter_that_becomes_a_Grid_asks_every_call_for_a_grid()
    {
        var before = Comp("Strip", new[] { SlotParam() }, Bound(Click(3912, 1056), ("Point", "Slot")));
        var macro = Macro("Caller", Call(before, ("Slot", Point(10, 20))));
        Assert.Empty(Problems(new[] { before }, macro));

        var after = before.Clone();
        ComponentEdits.Forget(after.Actions, after.Parameters[0]);
        after.Parameters[0].Type = ParameterType.Grid;
        after.Parameters[0].Example = Inventory;
        after.Actions[0] = CellClick("Slot", cell: 12);
        ComponentEdits.WriteExamples(after);
        Assert.Empty(ComponentProblems(after));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { after });
        Assert.Equal(new[] { "Slot" }, update.NeedValue.Select(p => p.Name));
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode> { ["Slot"] = Inventory }, after)
            .Macros.Single();
        Assert.True(JsonNode.DeepEquals(Inventory, changed.Actions[0].Arguments!["Slot"]));
        Assert.Empty(Problems(new[] { after }, changed));
    }

    [Fact]
    public void A_Grid_parameter_that_becomes_a_Point_asks_every_call_for_a_point_and_its_actions_lose_their_cells()
    {
        var before = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"), CellClick(cell: 3));
        ComponentEdits.WriteExamples(before);
        var macro = Macro("Caller", Call(before, ("Inv", Inventory), ("Slot", Number(5))));
        Assert.Empty(Problems(new[] { before }, macro));

        var after = before.Clone();
        var grid = after.Parameters[0];
        ComponentEdits.Forget(after.Actions, grid);
        Assert.All(after.Actions, a => Assert.Null(a.Cell));
        Assert.All(after.Actions, a => Assert.False(a.Bind?.ContainsKey("Cell") ?? false));
        grid.Type = ParameterType.Point;
        grid.Example = Point(3912, 1056);
        Assert.Empty(ComponentProblems(after));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { after });
        Assert.Equal(new[] { "Inv" }, update.NeedValue.Select(p => p.Name));
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode> { ["Inv"] = Point(1, 2) }, after)
            .Macros.Single();
        Assert.Empty(Problems(new[] { after }, changed));
    }

    [Fact]
    public void A_renamed_grid_and_cell_number_keep_every_call_and_its_cells()
    {
        var before = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var macro = Macro("Caller", Call(before, ("Inv", Inventory), ("Slot", Number(30))));
        var after = before.Clone();
        ComponentEdits.Rename(after.Actions, "Inv", "Bag");
        ComponentEdits.Rename(after.Actions, "Slot", "Which");
        after.Parameters[0].Name = "Bag";
        after.Parameters[1].Name = "Which";
        var renamed = new Dictionary<string, string> { ["Inv"] = "Bag", ["Slot"] = "Which" };

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { after }, renamed);
        Assert.Empty(update.NeedValue);
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), after).Macros.Single();
        Assert.Empty(Problems(new[] { after }, changed));
        var programs = ComponentPrograms.Prepare(new[] { after });
        Assert.Equal(new[] { "Click 4427,1268" }, PlayedPoints(changed.Actions, programs, FrameArguments.None));
    }

    // ================= Files and the Code view =================

    [Fact]
    public void The_fingerprint_follows_a_cell_and_a_grid_s_example()
    {
        var component = Comp("Strip", new[] { GridParam() }, CellClick(cell: 2));
        string Fingerprint(LibraryComponent c) => ComponentLibrary.Fingerprint(new LibraryCollection { Components = { c } });
        string before = Fingerprint(component);
        var otherCell = component.Clone();
        otherCell.Actions[0].Cell = 3;
        Assert.NotEqual(before, Fingerprint(otherCell));
        var otherGrid = component.Clone();
        otherGrid.Parameters[0].Example = Grid(3912, 1056, 4647, 1268, 11, 4);
        Assert.NotEqual(before, Fingerprint(otherGrid));
    }

    [Fact]
    public void A_component_s_cells_survive_the_Code_view_and_the_compact_form_AI_apps_read()
    {
        var pixel = CellPixel(cellFrom: "Slot");
        var component = Comp("Strip", new[] { GridParam(@default: Grid(0, 0, 10, 10, 2, 2)), CountParam(example: 4) },
            CellClick(cell: 4), If(pixel, CellClick(cellFrom: "Slot")));
        ComponentEdits.WriteExamples(component);
        Assert.Empty(ComponentProblems(component));
        foreach (string text in new[] { CodeJson.Serialize(component), AiJson.Compact(component) })
        {
            var read = MacroJson.ParseComponent(text, component.Id, out var problems, null, Array.Empty<LibraryComponent>());
            Assert.Empty(problems);
            Assert.Equal(CodeJson.Serialize(component), CodeJson.Serialize(read!));
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10001")]
    [InlineData("2.5")]
    [InlineData("\"3\"")]
    [InlineData("true")]
    public void The_Code_view_refuses_a_cell_that_isn_t_a_whole_number_from_1_to_10000(string cell)
    {
        var component = Comp("Strip", new[] { GridParam() }, CellClick(cell: 2));
        string text = CodeJson.Serialize(component).Replace("\"Cell\": 2", "\"Cell\": " + cell, StringComparison.Ordinal);
        Assert.Contains("\"Cell\": " + cell, text);
        var read = MacroJson.ParseComponent(text, component.Id, out var problems, null, Array.Empty<LibraryComponent>());
        Assert.True(read == null || problems.Count > 0, $"a cell {cell} is read without a problem");
    }

    // ================= AI apps =================

    [Fact]
    public void The_AI_apps_instructions_name_every_type_a_parameter_can_have_and_a_grid_s_cell()
    {
        foreach (var type in Enum.GetValues<ParameterType>())
            Assert.Contains(type.ToString(), AiTools.Instructions);
        Assert.Contains("Cell", AiTools.Instructions);
    }

    // ================= View models =================

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    [InlineData(10_000, 10_000)]
    public void A_click_s_and_a_pixel_s_cell_field_write_the_same_cell(double typed, int expected)
    {
        var parameters = new[] { GridParam() };
        var box = new ActionBoxViewModel(Click());
        box.SetParameters(parameters, Click());
        box.PositionIndex = 1;
        box.Cell = typed;
        var action = box.ToMacroAction(Click());

        var pixel = new ConditionViewModel(Pixel(), null, new ConditionContext { Parameters = parameters });
        pixel.Slots.Point.Index = 1;
        pixel.Cell = typed;
        var condition = pixel.ToModel();

        Assert.Equal(expected, action.Cell);
        Assert.Equal(expected, condition.Cell);
    }

    [Theory]
    [InlineData(2.4)]
    [InlineData(2.5)]
    [InlineData(2.6)]
    [InlineData(3.5)]
    public void A_click_and_a_pixel_round_a_typed_cell_the_same_way(double typed)
    {
        var parameters = new[] { GridParam() };
        var box = new ActionBoxViewModel(Click());
        box.SetParameters(parameters, Click());
        box.PositionIndex = 1;
        box.Cell = typed;
        var pixel = new ConditionViewModel(Pixel(), null, new ConditionContext { Parameters = parameters });
        pixel.Slots.Point.Index = 1;
        pixel.Cell = typed;
        Assert.Equal(pixel.ToModel().Cell, box.ToMacroAction(Click()).Cell);
    }

    [Fact]
    public void A_grid_s_fields_left_empty_are_never_a_grid_with_zero_columns_that_passes()
    {
        var grid = new ValueEditorViewModel(ParameterType.Grid, null, Inventory, null);
        grid.Columns = double.NaN;
        Assert.NotNull(grid.Problem);
        Assert.Empty(grid.CellPoints);
        grid.Columns = 11;
        grid.FirstX = double.NaN;
        // An empty X is 0: a grid still, from 0.
        Assert.Null(grid.Problem);
        Assert.Equal(0, grid.CellPoints[0].X);
        grid.Rows = 1;
        Assert.Equal(Strings.Code_GridOneRow, grid.Problem);
    }

    [Fact]
    public void Switching_a_click_from_a_cell_to_a_found_place_writes_neither_a_cell_nor_the_grid()
    {
        var finder = Guid.NewGuid();
        var box = new ActionBoxViewModel(Click());
        box.SetFounds(new Dictionary<Guid, string> { [finder] = "Bag" });
        var parameters = new[] { GridParam(), CountParam() };
        box.SetParameters(parameters, Click());
        Assert.Equal(new[] { Strings.Action_PositionFixed, "A cell of Inv", F(Strings.Action_PositionFound, "Bag") }, box.PositionChoices);
        box.PositionIndex = 1;
        box.Slots.Cell.Index = 1;
        box.PositionIndex = 2;
        var action = box.ToMacroAction(Click());
        Assert.Equal(finder, action.FoundId);
        Assert.Null(action.Cell);
        Assert.Null(action.Bind);
    }
}
