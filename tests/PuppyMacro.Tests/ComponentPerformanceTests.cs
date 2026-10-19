using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// "D4 Strip Affixes" made with functions, as the user's macro is: "Remove one affix" (three Ifs that find the button, the
/// confirmation and Transmute), "Strip one slot" (the slot's pixel, then a click on it and a call to Remove one affix), and
/// 33 rows that call Strip one slot each with its slot; with 1000 such macros, how long the checks, the engine's snapshot
/// and a call's work take. The limits are wide (the GitHub machines are slow); the times are written in the test's output.
/// </summary>
public class ComponentPerformanceTests
{
    private static MacroAction FindAndClick(string name)
    {
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = name, Similarity = 90,
            Images = new() { new ConditionImage { Id = ImageA, X = 1200, Y = 900 } },
        };
        var block = If(condition, Click(found: condition.Id));
        block.If!.Wait = ConditionWait.UpTo;
        block.If.WaitSeconds = 2;
        return block;
    }

    private static (LibraryComponent Remove, LibraryComponent Strip) Components()
    {
        var remove = Comp("Remove one affix", FindAndClick("Remove Affix"), FindAndClick("Confirm"), FindAndClick("Transmute"));
        var magic = Bound(Pixel(3912, 1056, "#2B4C9C"), ("Point", "Slot"));
        var rare = Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"));
        var strip = Comp("Strip one slot", new[] { SlotParam(), HoldParam(@default: Number(30)) },
            If(new[] { magic, rare }, ConditionMatch.Any,
                Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold")),
                Call(remove)));
        return (remove, strip);
    }

    private static MacroDefinition D4(LibraryComponent strip, int n = 0) => Macro($"D4 Strip Affixes {n}",
        Enumerable.Range(0, 33).Select(i => Call(strip, ("Slot", Point(3912 + 74 * (i % 11), 1056 + 74 * (i / 11))))).ToArray());

    private static void Write(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    private static double Ms(Action action, int times = 1)
    {
        action(); // warm up
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < times; i++)
            action();
        return clock.Elapsed.TotalMilliseconds / times;
    }

    [Fact]
    public void D4_made_with_functions_is_checked_and_prepared_in_milliseconds()
    {
        var (remove, strip) = Components();
        var library = new[] { remove, strip };
        var d4 = D4(strip);
        Assert.Empty(Problems(library, d4));
        Assert.Empty(MacroProblems(d4, library));
        Assert.Empty(ComponentProblems(strip, remove));

        string text = MacroJson.Serialize(d4);
        double parse = Ms(() => MacroJson.Parse(text, d4.Id, (_, _) => null, out _, null, new ComponentSignatures(library)), 20);
        double component = Ms(() => ComponentLibrary.Problems(strip, null, library), 20);
        var macros = Enumerable.Range(0, 1000).Select(n => D4(strip, n)).ToList();
        double checkAll = Ms(() => ComponentChecks.CheckAll(new ComponentSignatures(library), macros, null), 3);
        double unplayable = Ms(() => ComponentChecks.Unplayable(new ComponentSignatures(library), macros), 3);
        double prepare = Ms(() => ComponentPrograms.Prepare(library), 20);

        Write($"D4 (33 calls) read with its checks: {parse:0.00} ms");
        Write($"Strip one slot's own checks: {component:0.00} ms");
        Write($"1000 macros of 33 calls, every call checked: {checkAll:0.0} ms");
        Write($"1000 macros: which can't play: {unplayable:0.0} ms");
        Write($"Components prepared: {prepare:0.000} ms");

        Assert.True(parse < 200, $"{parse} ms");
        Assert.True(checkAll < 5000, $"{checkAll} ms");
        Assert.True(unplayable < 5000, $"{unplayable} ms");
        Assert.True(prepare < 50, $"{prepare} ms");
    }

    [Fact]
    public void A_call_s_own_work_is_microseconds()
    {
        var (remove, strip) = Components();
        var programs = ComponentPrograms.Prepare(new[] { remove, strip });
        var prepared = programs.Find(strip.Id)!;
        var row = D4(strip).Actions[12];
        var nothing = new Dictionary<Guid, OpenCvSharp.Point>();

        double evaluate = Ms(() => ComponentPrograms.Evaluate(prepared, row.Arguments, FrameArguments.None, nothing, out _), 100_000);
        var frame = ComponentPrograms.Evaluate(prepared, row.Arguments, FrameArguments.None, nothing, out _)!;
        var click = prepared.Actions[0].If!.Then[0];
        double bind = Ms(() => ComponentPrograms.Bound(click, frame), 100_000);
        var magic = prepared.Actions[0].If!.Conditions[0];
        double condition = Ms(() => programs.Effective(magic, frame, nothing), 100_000);

        Write($"A call's values: {evaluate * 1000:0.0} µs");
        Write($"A click's bound fields: {bind * 1000:0.0} µs");
        Write($"A condition's bound fields: {condition * 1000:0.0} µs");

        // A key sent or a picture of the screen take milliseconds: a call's own work is far below.
        Assert.True(evaluate < 0.5, $"{evaluate} ms");
        Assert.True(bind < 0.5, $"{bind} ms");
        Assert.True(condition < 0.5, $"{condition} ms");
        Assert.Equal((3912 + 74, 1056 + 74), (ComponentPrograms.Bound(click, frame)!.X, ComponentPrograms.Bound(click, frame)!.Y));
    }

    [Fact]
    public void A_cell_of_a_grid_costs_as_little_as_a_point()
    {
        // The D4 inventory as a Grid and the slot as a Number: the click goes to cell 23.
        var inventory = new System.Text.Json.Nodes.JsonObject
        {
            ["First"] = Point(3912, 1056), ["Last"] = Point(4647, 1268), ["Columns"] = 11, ["Rows"] = 3,
        };
        var gridClick = Bound(Click(3912, 1056), ("Point", "Inv"), ("Cell", "Slot"));
        gridClick.Cell = 1;
        var gridFrame = new FrameArguments(new Dictionary<string, System.Text.Json.Nodes.JsonNode> { ["Inv"] = inventory, ["Slot"] = Number(23) });
        var pointClick = Bound(Click(3912, 1056), ("Point", "Slot"));
        var pointFrame = new FrameArguments(new Dictionary<string, System.Text.Json.Nodes.JsonNode> { ["Slot"] = Point(3912, 1268) });

        double grid = Ms(() => ComponentPrograms.Bound(gridClick, gridFrame), 100_000);
        double point = Ms(() => ComponentPrograms.Bound(pointClick, pointFrame), 100_000);

        Write($"A click at a cell of a grid: {grid * 1000:0.0} µs");
        Write($"A click at a point: {point * 1000:0.0} µs");

        Assert.Equal((3912, 1268), (ComponentPrograms.Bound(gridClick, gridFrame)!.X, ComponentPrograms.Bound(gridClick, gridFrame)!.Y));
        // Far below the milliseconds of a click.
        Assert.True(grid < 0.5, $"{grid} ms");
    }
}
