using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The Grid parameter (cells all alike in rows): its value and each rule it refuses, the point of each cell (the D4
/// inventory's 33, decimals rounded half away from zero, either direction, one column or row), a Point bound to a grid with
/// its Cell (fixed or from a Number parameter) and every way that is refused, the cells each call reaches checked with its
/// values (also down the calls, and the component's own Examples), a run on the fake screen, and Unlink, removing and
/// renaming the parameter. The keys pressed are F24 only.
/// </summary>
[Collection(nameof(DebugStore))]
public class ComponentGridTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();
    private readonly ImageStore _images;

    public ComponentGridTests()
    {
        DebugStore.Root = _folder.File("debug");
        _images = new ImageStore(_folder.File("images"));
    }

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    private static JsonNode Grid(int firstX, int firstY, int lastX, int lastY, int columns, int rows) => new JsonObject
    {
        ["First"] = Point(firstX, firstY), ["Last"] = Point(lastX, lastY), ["Columns"] = columns, ["Rows"] = rows,
    };

    /// <summary>The D4 inventory: 11 × 3, the rarity pixel of the first slot at 3912, 1056, of the last at 4647, 1268.</summary>
    private static JsonNode Inventory => Grid(3912, 1056, 4647, 1268, 11, 3);

    private static GridValue Read(JsonNode node) =>
        (GridValue)ComponentValues.Read(node, ParameterType.Grid, ValueRule.Any, out string? problem)!;

    private static string? Problem(JsonNode? node)
    {
        ComponentValues.Read(node, ParameterType.Grid, ValueRule.Any, out string? problem);
        return problem;
    }

    private static string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>A problem with this message, whatever its path ("Actions[0].Cell: …").</summary>
    private static void Has(string message, IEnumerable<string> problems) =>
        Assert.Contains(problems, p => p == message || p.EndsWith(": " + message, StringComparison.Ordinal));

    private static ComponentParameter GridParam(string name = "Inv", JsonNode? example = null, JsonNode? @default = null) =>
        Param(name, ParameterType.Grid, example ?? Inventory, @default);

    private static ComponentParameter CountParam(string name = "Slot", double example = 1) =>
        Param(name, ParameterType.Number, Number(example), unit: NumberUnit.Count);

    /// <summary>A click at cell <paramref name="cell"/> of the Grid parameter <paramref name="grid"/>, or of the cell from <paramref name="cellFrom"/>.</summary>
    private static MacroAction CellClick(string grid = "Inv", int cell = 1, string? cellFrom = null)
    {
        var click = Bound(Click(3912, 1056), ("Point", grid));
        click.Cell = cell;
        if (cellFrom != null)
            click.Bind!["Cell"] = cellFrom;
        return click;
    }

    // ================= The value =================

    [Fact]
    public void The_D4_inventory_s_33_cells_are_each_slot_s_pixel()
    {
        var grid = Read(Inventory);
        Assert.Equal(33, grid.Cells);
        int[] xs = { 3912, 3986, 4059, 4133, 4206, 4280, 4353, 4427, 4500, 4574, 4647 }; // step 73.5, halves away from zero
        int[] ys = { 1056, 1162, 1268 };
        for (int cell = 1; cell <= 33; cell++)
            Assert.Equal(new PointValue(xs[(cell - 1) % 11], ys[(cell - 1) / 11]), grid.PointOf(cell));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(34)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void A_cell_outside_the_grid_has_no_point(int cell) => Assert.Null(Read(Inventory).PointOf(cell));

    [Fact]
    public void Cells_go_either_way_and_halves_round_away_from_zero()
    {
        // Last before first: right to left, bottom to top.
        var back = Read(Grid(100, 300, 0, 0, 4, 4));
        Assert.Equal(new PointValue(100, 300), back.PointOf(1));
        Assert.Equal(new PointValue(67, 300), back.PointOf(2)); // 66.67
        Assert.Equal(new PointValue(33, 200), back.PointOf(7)); // 33.33, 200
        Assert.Equal(new PointValue(0, 0), back.PointOf(16));
        // -1.5 is -2 (away from zero), 1.5 is 2.
        Assert.Equal(new PointValue(-2, 0), Read(Grid(0, 0, -3, 0, 3, 1)).PointOf(2));
        Assert.Equal(new PointValue(2, 0), Read(Grid(0, 0, 3, 0, 3, 1)).PointOf(2));
        // Negative coordinates (a monitor left of the main one).
        Assert.Equal(new PointValue(-1910, -50), Read(Grid(-1920, -100, -1900, 0, 3, 3)).PointOf(5));
    }

    [Fact]
    public void One_column_one_row_and_one_cell()
    {
        var column = Read(Grid(50, 10, 50, 40, 1, 4));
        Assert.Equal(new[] { 10, 20, 30, 40 }, Enumerable.Range(1, 4).Select(c => column.PointOf(c)!.Value.Y));
        Assert.All(Enumerable.Range(1, 4), c => Assert.Equal(50, column.PointOf(c)!.Value.X));
        var row = Read(Grid(10, 7, 40, 7, 4, 1));
        Assert.Equal(new[] { 10, 20, 30, 40 }, Enumerable.Range(1, 4).Select(c => row.PointOf(c)!.Value.X));
        var one = Read(Grid(5, 6, 5, 6, 1, 1));
        Assert.Equal(new PointValue(5, 6), one.PointOf(1));
        Assert.Null(one.PointOf(2));
    }

    [Fact]
    public void The_biggest_grid_and_its_last_cell()
    {
        var big = Read(Grid(0, 0, 999, 9, 1000, 10));
        Assert.Equal(10_000, big.Cells);
        Assert.Equal(new PointValue(999, 9), big.PointOf(10_000));
        Assert.Equal(new PointValue(0, 1), big.PointOf(1001));
    }

    [Fact]
    public void Each_wrong_grid_is_refused_with_why()
    {
        string size = F(Strings.Code_GridSize, GridValue.MaxSide, GridValue.MaxCells);
        Assert.Equal(Strings.Code_ValueGrid, Problem(null));
        Assert.Equal(Strings.Code_ValueGrid, Problem(Point(1, 2)));
        Assert.Equal(Strings.Code_ValueGrid, Problem(new JsonArray()));
        Assert.Equal(Strings.Code_ValueGrid, Problem(JsonValue.Create("11 × 3")));
        var missing = (JsonObject)Inventory.DeepClone();
        missing.Remove("Rows");
        Assert.Equal(Strings.Code_ValueGrid, Problem(missing));
        var extra = (JsonObject)Inventory.DeepClone();
        extra["Step"] = 73.5;
        Assert.Equal(Strings.Code_ValueGrid, Problem(extra));
        var decimals = (JsonObject)Inventory.DeepClone();
        decimals["Columns"] = 10.5;
        Assert.Equal(Strings.Code_ValueGrid, Problem(decimals));
        var text = (JsonObject)Inventory.DeepClone();
        text["Rows"] = "3";
        Assert.Equal(Strings.Code_ValueGrid, Problem(text));
        var badPoint = (JsonObject)Inventory.DeepClone();
        badPoint["First"] = new JsonObject { ["X"] = 1 };
        Assert.Equal(Strings.Code_ValueGrid, Problem(badPoint));
        var farPoint = (JsonObject)Inventory.DeepClone();
        farPoint["Last"] = Point(200_000, 0);
        Assert.Equal(Strings.Code_ValueGrid, Problem(farPoint));

        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, 0, 3)));
        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, 3, 0)));
        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, -1, 3)));
        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, 1001, 1)));
        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, 2, 1001)));
        Assert.Equal(size, Problem(Grid(0, 0, 10, 10, 101, 100))); // 10 100 cells
        Assert.Null(Problem(Grid(0, 0, 10, 10, 100, 100)));        // 10 000

        Assert.Equal(Strings.Code_GridOneColumn, Problem(Grid(0, 0, 10, 10, 1, 3)));
        Assert.Equal(Strings.Code_GridOneRow, Problem(Grid(0, 0, 10, 10, 3, 1)));
        Assert.Null(Problem(Grid(0, 0, 0, 10, 1, 3)));
        Assert.Null(Problem(Grid(0, 5, 10, 5, 3, 1)));
    }

    [Fact]
    public void A_grid_s_json_and_its_few_words()
    {
        var grid = Read(Inventory);
        Assert.True(JsonNode.DeepEquals(Inventory, ComponentValues.ToJson(grid)));
        Assert.Equal(grid, Read(ComponentValues.ToJson(grid)));
        Assert.Equal("11 × 3 from 3912, 1056", ComponentValues.Describe(Inventory, ParameterType.Grid, null));
        Assert.Equal("Grid", ComponentChecks.TypeName(ParameterType.Grid, null));
    }

    // ================= A point from a grid =================

    [Fact]
    public void A_click_at_a_fixed_cell_or_at_the_cell_a_call_gives_passes_the_checks()
    {
        var fixedCell = Comp("Fixed", new[] { GridParam() }, CellClick(cell: 33));
        Assert.Empty(ComponentProblems(fixedCell));
        var fromSlot = Comp("From slot", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        Assert.Empty(ComponentProblems(fromSlot));
        var pixel = Bound(Pixel(3912, 1056, "#1D2C4B"), ("Point", "Inv"), ("Cell", "Slot"));
        pixel.Cell = 1;
        Assert.Empty(ComponentProblems(Comp("Pixel", new[] { GridParam(), CountParam() }, If(pixel))));
        foreach (var type in new[] { MacroActionType.MouseDown, MacroActionType.MouseUp, MacroActionType.MoveTo })
        {
            var action = CellClick();
            action.Type = type;
            Assert.Empty(ComponentProblems(Comp(type.ToString(), new[] { GridParam() }, action)));
        }
    }

    [Fact]
    public void A_point_from_a_grid_needs_a_cell_and_a_cell_needs_a_grid()
    {
        var noCell = Bound(Click(), ("Point", "Inv"));
        Has(F(Strings.Code_GridNeedsCell, "Inv"), ComponentProblems(Comp("No cell", new[] { GridParam() }, noCell)));

        // A cell with the point from a Point parameter, with the point fixed, in a macro, on a key, on a found place.
        var pointParam = CellClick(grid: "Where");
        Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Point", new[] { SlotParam("Where") }, pointParam)));
        var unbound = Click();
        unbound.Cell = 3;
        Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Unbound", new[] { GridParam() }, unbound)));
        Has(Strings.Code_CellWithoutGrid, MacroProblems(Macro("A macro", unbound)));
        var key = Bound(Press(), ("Point", "Inv"));
        key.Cell = 1;
        Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Key", new[] { GridParam() }, key)));
        var finder = ImageFinder();
        var found = CellClick();
        found.FoundId = finder.Id;
        Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Found", new[] { GridParam() }, If(finder, found))));
        var pixel = Pixel();
        pixel.Cell = 2;
        Has(Strings.Code_CellWithoutGrid, ComponentProblems(Comp("Pixel", new[] { GridParam() }, If(pixel))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_001)]
    public void A_cell_from_1_to_10000(int cell)
    {
        var problems = ComponentProblems(Comp("Cell", new[] { GridParam() }, CellClick(cell: cell)));
        Has(F(Strings.Code_Between, 1, 10_000), problems);
    }

    [Fact]
    public void A_cell_takes_only_a_count_and_only_when_there_is_a_cell()
    {
        var ms = Comp("Ms", new[] { GridParam(), HoldParam("Slot") }, CellClick(cellFrom: "Slot"));
        Has(F(Strings.Code_ParameterType, "Slot", ComponentChecks.TypeName(ParameterType.Number, NumberUnit.Ms), "Cell",
            ComponentChecks.TypeName(ParameterType.Number, NumberUnit.Count)), ComponentProblems(ms));
        var gridForCell = Comp("Grid", new[] { GridParam(), GridParam("Other") }, CellClick(cellFrom: "Other"));
        Assert.NotEmpty(ComponentProblems(gridForCell));
        var noCell = Bound(Click(), ("Cell", "Slot"));
        Has(F(Strings.Code_BindUnused, "Cell"), ComponentProblems(Comp("No cell", new[] { CountParam() }, noCell)));
        // A grid for a Point parameter's place: a call can't give a Grid to a Point.
        var point = Comp("Point", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        Assert.NotEmpty(Problems(new[] { point }, Macro("Grid for a point", Call(point, ("Slot", Inventory)))));
    }

    [Fact]
    public void A_number_for_a_cell_is_whole_and_from_1()
    {
        var component = Comp("Slot", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        Assert.Single(Problems(new[] { component }, Macro("Zero", Call(component, ("Inv", Inventory), ("Slot", Number(0))))));
        Assert.Single(Problems(new[] { component }, Macro("Half", Call(component, ("Inv", Inventory), ("Slot", Number(2.5))))));
        Assert.Empty(Problems(new[] { component }, Macro("Five", Call(component, ("Inv", Inventory), ("Slot", Number(5))))));
    }

    // ================= Each call's cells =================

    [Fact]
    public void A_call_that_reaches_a_cell_its_grid_doesn_t_have_is_refused_and_can_t_play()
    {
        var strip = Comp("Strip one slot", new[] { GridParam(@default: Inventory), CountParam() }, CellClick(cellFrom: "Slot"));
        var ok = Macro("Ok", Call(strip, ("Slot", Number(33))));
        Assert.Empty(Problems(new[] { strip }, ok));

        var past = Macro("Past the end", Call(strip, ("Slot", Number(34))));
        Assert.Equal(new[] { $"Past the end: Actions[0].Arguments: {F(Strings.Code_CellOutside, 34, "Inv", 33, "Strip one slot")}" },
            Problems(new[] { strip }, past));
        Assert.Contains(past.Id, ComponentChecks.Unplayable(new ComponentSignatures(new[] { strip }), new[] { ok, past }));
        Assert.DoesNotContain(ok.Id, ComponentChecks.Unplayable(new ComponentSignatures(new[] { strip }), new[] { ok, past }));

        // A smaller grid given by the call, with the default cell number.
        var small = Macro("Small grid", Call(strip, ("Inv", Grid(0, 0, 10, 10, 2, 2)), ("Slot", Number(5))));
        Assert.Contains(F(Strings.Code_CellOutside, 5, "Inv", 4, "Strip one slot"), Problems(new[] { strip }, small).Single());
        // A fixed cell, a grid the call gives.
        var fixedCell = Comp("Fixed", new[] { GridParam() }, CellClick(cell: 9));
        Assert.Single(Problems(new[] { fixedCell }, Macro("Too small", Call(fixedCell, ("Inv", Grid(0, 0, 10, 10, 2, 2))))));
        Assert.Empty(Problems(new[] { fixedCell }, Macro("Big enough", Call(fixedCell, ("Inv", Grid(0, 0, 20, 20, 3, 3))))));
    }

    [Fact]
    public void The_cells_are_checked_down_the_calls_with_the_values_passed_on()
    {
        var strip = Comp("Strip one slot", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var tidy = Comp("Tidy", new[] { GridParam("Bag"), CountParam("Which", 2) },
            Call(strip, ("Inv", From("Bag")), ("Slot", From("Which"))));
        var outer = Comp("All", new[] { GridParam("Bag", @default: Grid(0, 0, 30, 0, 4, 1)) },
            Call(tidy, ("Bag", From("Bag")), ("Which", Number(5))));

        var problems = Problems(new[] { strip, tidy, outer }, Macro("Outer", Call(outer)));
        Assert.Equal(new[] { $"Outer: Actions[0].Arguments: {F(Strings.Code_CellOutside, 5, "Inv", 4, "Strip one slot")}" }, problems);
        Assert.Empty(Problems(new[] { strip, tidy, outer }, Macro("Bigger", Call(outer, ("Bag", Inventory)))));
    }

    [Fact]
    public void A_component_s_examples_must_reach_cells_its_example_grid_has()
    {
        var past = Comp("Past", new[] { GridParam(example: Grid(0, 0, 10, 0, 2, 1)) }, CellClick(cell: 3));
        Has(F(Strings.Code_CellOutside, 3, "Inv", 2, "Past"), ComponentProblems(past));
        var fromExample = Comp("From example", new[] { GridParam(example: Grid(0, 0, 10, 0, 2, 1)), CountParam(example: 3) },
            CellClick(cellFrom: "Slot"));
        Has(F(Strings.Code_CellOutside, 3, "Inv", 2, "From example"), ComponentProblems(fromExample));
    }

    [Fact]
    public void Only_the_calls_that_can_reach_a_cell_are_checked_for_cells_also_through_other_components()
    {
        var strip = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var middle = Comp("Middle", new[] { GridParam("Bag"), CountParam("Which") }, Call(strip, ("Inv", From("Bag")), ("Slot", From("Which"))));
        var top = Comp("Top", new[] { GridParam("Bag"), CountParam("Which") }, Press(), Call(middle, ("Bag", From("Bag")), ("Which", From("Which"))));
        var plain = Comp("Plain", Press());
        var signatures = new ComponentSignatures(new[] { strip, middle, top, plain });
        Assert.True(signatures.UsesCells(strip.Id));
        Assert.True(signatures.UsesCells(middle.Id));
        Assert.True(signatures.UsesCells(top.Id)); // two levels down
        Assert.False(signatures.UsesCells(plain.Id));
        Assert.False(signatures.UsesCells(Guid.NewGuid()));
        // So a cell outside the grid two levels down is still found.
        Assert.Single(Problems(new[] { strip, middle, top }, Macro("Deep", Call(top, ("Bag", Inventory), ("Which", Number(34))))));
    }

    [Fact]
    public void A_cell_problem_is_said_once_per_component_grid_and_cell()
    {
        var two = Comp("Two clicks", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"), CellClick(cellFrom: "Slot"));
        Assert.Single(Problems(new[] { two }, Macro("Twice", Call(two, ("Inv", Inventory), ("Slot", Number(40))))));
    }

    // ================= Played =================

    [Fact]
    public void A_bound_click_goes_to_its_cell_whatever_order_its_bind_is_written_in()
    {
        var click = new MacroAction
        {
            Type = MacroActionType.Click, X = 1, Y = 1, Cell = 1,
            Bind = new Dictionary<string, string> { ["Point"] = "Inv", ["Cell"] = "Slot" }, // the point first
        };
        var frame = new FrameArguments(new Dictionary<string, JsonNode> { ["Inv"] = Inventory, ["Slot"] = Number(13) });
        var bound = ComponentPrograms.Bound(click, frame)!;
        Assert.Equal((3986, 1162, 13), (bound.X, bound.Y, bound.Cell));
        Assert.Equal((1, 1, 1), (click.X, click.Y, click.Cell)); // the prepared action never changes

        var fixedCell = CellClick(cell: 33);
        var at = ComponentPrograms.Bound(fixedCell, new FrameArguments(new Dictionary<string, JsonNode> { ["Inv"] = Inventory }))!;
        Assert.Equal((4647, 1268), (at.X, at.Y));
    }

    [Fact]
    public void A_cell_its_grid_doesn_t_have_never_plays_somewhere_else()
    {
        var frame = new FrameArguments(new Dictionary<string, JsonNode> { ["Inv"] = Inventory, ["Slot"] = Number(34) });
        Assert.Null(ComponentPrograms.Bound(CellClick(cellFrom: "Slot"), frame));
        var pixel = Bound(Pixel(), ("Point", "Inv"), ("Cell", "Slot"));
        pixel.Cell = 1;
        Assert.Null(ComponentPrograms.Bound(pixel, frame));
        Assert.Null(ComponentPrograms.Empty.Effective(pixel, frame, new Dictionary<Guid, Point>()));
    }

    [Fact]
    public void A_pixel_at_the_cell_a_call_gives_decides_its_If()
    {
        // Cells 100 apart from (100, 50): cell 6 is (200, 150), cell 5 is (100, 150).
        using var picture = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        picture.Set(150, 200, new Vec3b(255, 255, 255));
        _screen.Show(picture);
        var pixel = Bound(Pixel(100, 50, "#FFFFFF"), ("Point", "Bag"), ("Cell", "Slot"));
        pixel.Cell = 1;
        var white = Comp("White slot", new[] { GridParam("Bag", Grid(100, 50, 400, 250, 4, 3)), CountParam() }, If(pixel, Press(0x87, 11)));
        var macro = Macro("Slots", Call(white, ("Bag", Grid(100, 50, 400, 250, 4, 3)), ("Slot", Number(6))),
            Call(white, ("Bag", Grid(100, 50, 400, 250, 4, 3)), ("Slot", Number(5))));

        string log = Run(macro, new[] { white });

        Assert.Contains("Pixel 200,150: saw #FFFFFF", log);
        Assert.Contains("#1 (White slot) 1 Then 1 Press F24, held 11 ms", log);
        Assert.Contains("Pixel 100,150: saw #000000", log);
        Assert.DoesNotContain("#2 (White slot) 1 Then 1", log);
    }

    private string Run(MacroDefinition macro, IEnumerable<LibraryComponent> components)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "run-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var log = new RunLog(folder, macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), _images, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) },
            ComponentPrograms.Prepare(components));
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(RunLog.WaitWritten(TimeSpan.FromSeconds(10)));
        return RunLog.Read(log.Folder)!;
    }

    // ================= Edits =================

    [Fact]
    public void The_example_s_cell_point_is_written_in_the_fields()
    {
        var component = Comp("Strip", new[] { GridParam(), CountParam(example: 12) }, CellClick(cellFrom: "Slot"));
        ComponentEdits.WriteExamples(component);
        var click = component.Actions[0];
        Assert.Equal((3912, 1162, 12), (click.X, click.Y, click.Cell));
    }

    [Fact]
    public void Unlinked_the_cell_becomes_its_point()
    {
        var strip = Comp("Strip", new[] { GridParam(@default: Inventory), CountParam() }, CellClick(cellFrom: "Slot"));
        var actions = ComponentEdits.Unlink(Call(strip, ("Slot", Number(23))), strip, out string? problem)!;
        Assert.Null(problem);
        var click = actions.Single();
        Assert.Equal((3912, 1268), (click.X, click.Y));
        Assert.Null(click.Cell);
        Assert.Null(click.Bind);
        Assert.Empty(MacroProblems(Macro("Unlinked", actions.ToArray())));
    }

    [Fact]
    public void Unlinked_in_a_component_a_grid_passed_on_keeps_the_point_bound_to_it()
    {
        var strip = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        var actions = ComponentEdits.Unlink(Call(strip, ("Inv", From("Bag")), ("Slot", Number(4))), strip, out string? problem)!;
        Assert.Null(problem);
        var click = actions.Single();
        Assert.Equal(4, click.Cell);
        Assert.Equal(new Dictionary<string, string> { ["Point"] = "Bag" }, click.Bind);

        var both = ComponentEdits.Unlink(Call(strip, ("Inv", From("Bag")), ("Slot", From("Which"))), strip, out problem)!.Single();
        Assert.Equal(new Dictionary<string, string> { ["Point"] = "Bag", ["Cell"] = "Which" }, both.Bind);
    }

    [Fact]
    public void Unlink_is_refused_when_a_fixed_grid_would_need_a_cell_from_outside_or_a_cell_isn_t_in_it()
    {
        var strip = Comp("Strip", new[] { GridParam(@default: Inventory), CountParam() }, CellClick(cellFrom: "Slot"));
        Assert.Null(ComponentEdits.Unlink(Call(strip, ("Slot", From("Which"))), strip, out string? problem));
        Assert.Equal(Strings.Library_UnlinkCellFrom, problem);
        Assert.Null(ComponentEdits.Unlink(Call(strip, ("Slot", Number(40))), strip, out problem));
        Assert.Equal(Strings.Library_UnlinkCellOutside, problem);
    }

    [Fact]
    public void Removing_the_grid_keeps_the_example_s_point_and_drops_the_cell()
    {
        var grid = GridParam();
        var component = Comp("Strip", new[] { grid, CountParam(example: 2) }, CellClick(cellFrom: "Slot"));
        ComponentEdits.WriteExamples(component);
        ComponentEdits.Forget(component.Actions, grid);
        var click = component.Actions[0];
        Assert.Equal((3986, 1056), (click.X, click.Y));
        Assert.Null(click.Cell);
        Assert.False(click.Bind?.ContainsKey("Cell") ?? false);
        Assert.False(click.Bind?.ContainsKey("Point") ?? false);

        // Removing the cell's number keeps the cell as the example's.
        var slot = CountParam(example: 7);
        var other = Comp("Strip", new[] { GridParam(), slot }, CellClick(cellFrom: "Slot"));
        ComponentEdits.WriteExamples(other);
        ComponentEdits.Forget(other.Actions, slot);
        Assert.Equal(7, other.Actions[0].Cell);
        Assert.Equal(new Dictionary<string, string> { ["Point"] = "Inv" }, other.Actions[0].Bind);
    }

    [Fact]
    public void Renaming_the_grid_or_its_cell_s_number_keeps_the_point_working()
    {
        var component = Comp("Strip", new[] { GridParam(), CountParam() }, CellClick(cellFrom: "Slot"));
        ComponentEdits.Rename(component.Actions, "Inv", "Bag");
        ComponentEdits.Rename(component.Actions, "Slot", "Which");
        Assert.Equal(new Dictionary<string, string> { ["Point"] = "Bag", ["Cell"] = "Which" }, component.Actions[0].Bind);
    }

    [Fact]
    public void A_component_with_a_grid_reads_back_from_its_json_as_it_was()
    {
        var component = Comp("Strip", new[] { GridParam(@default: Inventory), CountParam() }, CellClick(cellFrom: "Slot"));
        var read = MacroJson.ParseComponent(CodeJson.Serialize(component), component.Id, out var problems, null, Array.Empty<LibraryComponent>());
        Assert.Empty(problems);
        Assert.Equal(ParameterType.Grid, read!.Parameters[0].Type);
        Assert.True(JsonNode.DeepEquals(Inventory, read.Parameters[0].Default));
        Assert.Equal(1, read.Actions[0].Cell);
    }
}
