using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// What must hold for every component with a Grid parameter, on hundreds made at random from fixed seeds
/// (<see cref="ComponentGridGenerator"/>): it passes its own check; every cell of any grid is the point the rule says; a
/// call the checks accept plays every action and condition at a point, and one they refuse only for its cells skips one;
/// Unlink plays what the call plays; the JSON and the compact form AI apps read come back as they were. Each failure names
/// its seed.
/// </summary>
public class ComponentGridInvariantsTests
{
    private static MacroAction CallWith(LibraryComponent component, Dictionary<string, JsonNode?> arguments) => new()
    {
        Type = MacroActionType.Component,
        ComponentId = component.Id,
        Arguments = arguments.Count == 0 ? null : arguments,
    };

    private static string Describe(Dictionary<string, JsonNode?> arguments) =>
        string.Join(", ", arguments.Select(a => $"{a.Key} = {a.Value?.ToJsonString() ?? "null"}"));

    private static void NoFailures(List<string> failures, int cases) =>
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases fail:\n" + string.Join("\n", failures.Take(12)));

    /// <summary>The rule, counted apart: from the first to the last over the steps, the offset rounded half away from zero.</summary>
    private static int Along(int first, int last, int index, int count) =>
        count == 1 ? first : first + (int)Math.Round((decimal)(last - first) * index / (count - 1), MidpointRounding.AwayFromZero);

    [Fact]
    public void Every_cell_of_any_grid_is_its_column_s_and_row_s_point()
    {
        var failures = new List<string>();
        for (int seed = 0; seed < 300; seed++)
        {
            var generator = new ComponentGridGenerator(seed);
            var node = generator.Grid();
            var grid = ComponentGridGenerator.Read(node);
            for (int cell = 1; cell <= grid.Cells; cell++)
            {
                int column = (cell - 1) % grid.Columns, row = (cell - 1) / grid.Columns;
                var expected = new PointValue(Along(grid.First.X, grid.Last.X, column, grid.Columns), Along(grid.First.Y, grid.Last.Y, row, grid.Rows));
                if (grid.PointOf(cell) != expected)
                {
                    failures.Add($"seed {seed}: {node.ToJsonString()} cell {cell} is {grid.PointOf(cell)}, not {expected}");
                    break;
                }
            }
            if (grid.PointOf(0) != null || grid.PointOf(grid.Cells + 1) != null)
                failures.Add($"seed {seed}: a cell outside {node.ToJsonString()} has a point");
        }
        NoFailures(failures, 300);
    }

    [Fact]
    public void A_component_whose_examples_reach_their_cells_passes_its_own_check()
    {
        var failures = new List<string>();
        for (int seed = 0; seed < 400; seed++)
        {
            var (component, library) = new ComponentGridGenerator(seed).Component();
            var problems = ComponentProblems(component, library.Where(c => c != component).ToArray());
            if (problems.Count > 0)
                failures.Add($"seed {seed}: {string.Join("; ", problems)}\n{CodeJson.Serialize(component)}");
        }
        NoFailures(failures, 400);
    }

    [Fact]
    public void A_call_the_checks_accept_plays_every_point_and_one_they_refuse_for_a_cell_skips_one()
    {
        var failures = new List<string>();
        int accepted = 0, refused = 0;
        for (int seed = 1000; seed < 1600; seed++)
        {
            var generator = new ComponentGridGenerator(seed);
            var (component, library) = generator.Component();
            var arguments = generator.Arguments(component);
            var problems = Problems(library, Macro("Caller", CallWith(component, arguments)));
            var programs = ComponentPrograms.Prepare(library);
            var played = ComponentGridGenerator.Played(new[] { CallWith(component, arguments) }, programs, FrameArguments.None);
            if (problems.Count == 0)
            {
                accepted++;
                if (played.Any(p => p == "skipped" || p.StartsWith("missing", StringComparison.Ordinal)))
                    failures.Add($"seed {seed}: {Describe(arguments)} is accepted, but the call plays {string.Join(" | ", played)}");
                continue;
            }
            // Refused only because a cell isn't in its grid: then something of it is skipped at play.
            var forms = new[] { Strings.Code_CellOutside_one, Strings.Code_CellOutside_other }
                .Select(f => Regex.Split(f, @"\{\d\}").Where(t => t.Length > 2).ToList()).ToList();
            if (problems.All(p => forms.Any(pieces => pieces.All(t => p.Contains(t, StringComparison.Ordinal)))))
            {
                refused++;
                if (!played.Contains("skipped"))
                    failures.Add($"seed {seed}: {Describe(arguments)} is refused ({string.Join("; ", problems)}), but every point plays: {string.Join(" | ", played)}");
            }
        }
        Assert.True(accepted > 150, $"only {accepted} calls accepted");
        Assert.True(refused > 100, $"only {refused} calls refused for a cell");
        NoFailures(failures, accepted + refused);
    }

    [Fact]
    public void Unlink_plays_exactly_what_the_call_plays()
    {
        var failures = new List<string>();
        int cases = 0;
        for (int seed = 2000; seed < 2400; seed++)
        {
            var generator = new ComponentGridGenerator(seed);
            var (component, library) = generator.Component();
            var arguments = generator.Arguments(component);
            var row = CallWith(component, arguments);
            if (Problems(library, Macro("Caller", row)).Count > 0)
                continue;
            cases++;
            var unlinked = ComponentEdits.Unlink(row, component, out string? problem);
            if (unlinked == null)
            {
                failures.Add($"seed {seed}: Unlink refused ({problem}) for {Describe(arguments)}");
                continue;
            }
            if (Problems(library, Macro("Unlinked", unlinked.ToArray())) is { Count: > 0 } broken)
                failures.Add($"seed {seed}: the unlinked rows are refused: {string.Join("; ", broken)}");
            var programs = ComponentPrograms.Prepare(library);
            var linked = ComponentGridGenerator.Played(new[] { row }, programs, FrameArguments.None);
            var rows = ComponentGridGenerator.Played(unlinked, programs, FrameArguments.None);
            if (!linked.SequenceEqual(rows))
                failures.Add($"seed {seed}: the call plays {string.Join(" | ", linked)}\nbut Unlink plays {string.Join(" | ", rows)}");
            if (MacroDefinition.Walk(unlinked).Any(a => a.Bind != null || a.Cell != null
                    || (a.If?.Conditions.Any(c => c.Bind != null || c.Cell != null) ?? false)))
                failures.Add($"seed {seed}: Unlink in a macro left a Bind or a Cell");
        }
        Assert.True(cases > 150, $"only {cases} cases");
        NoFailures(failures, cases);
    }

    [Fact]
    public void Unlink_inside_a_component_plays_what_the_call_played_with_the_grid_and_cell_passed_on()
    {
        var failures = new List<string>();
        int cases = 0, refused = 0;
        for (int seed = 4000; seed < 4400; seed++)
        {
            var generator = new ComponentGridGenerator(seed);
            var random = generator.Random;
            var (component, library) = generator.Component();
            var g = component.Parameters[0];
            var grid = g.Example!;
            int cells = ComponentGridGenerator.Read(grid).Cells;
            // The wrapper's own grid and number, passed on or not.
            var outer = new List<ComponentParameter>
            {
                Param("Bag", ParameterType.Grid, grid.DeepClone()),
                Param("Which", ParameterType.Number, Number(random.Next(1, cells + 1)), unit: NumberUnit.Count),
            };
            var arguments = new Dictionary<string, JsonNode?>
            {
                ["G"] = random.Next(3) == 0 ? grid.DeepClone() : From("Bag"),
                ["N"] = random.Next(2) == 0 ? From("Which") : Number(random.Next(1, cells + 1)),
            };
            var row = CallWith(component, arguments);
            var wrapper = Comp("Wrapper", outer, row);
            if (ComponentProblems(wrapper, library.ToArray()) is { Count: > 0 })
                continue;
            var unlinked = ComponentEdits.Unlink(row, component, out string? problem);
            if (unlinked == null)
            {
                // Only a grid given as a value with the cell from the wrapper can't be one row.
                bool expected = ComponentValues.Reference(arguments["G"]) == null && ComponentValues.Reference(arguments["N"]) != null
                                && MacroDefinition.Walk(component.Actions).Any(a => a.Bind?.GetValueOrDefault("Cell") == "N"
                                    || (a.If?.Conditions.Any(c => c.Bind?.GetValueOrDefault("Cell") == "N") ?? false));
                if (!expected)
                    failures.Add($"seed {seed}: Unlink refused ({problem}) for {Describe(arguments)}");
                refused++;
                continue;
            }
            cases++;
            var after = Comp("Wrapper", outer, unlinked.ToArray());
            ComponentEdits.WriteExamples(after);
            if (ComponentProblems(after, library.ToArray()) is { Count: > 0 } broken)
                failures.Add($"seed {seed}: {Describe(arguments)} unlinked is refused: {string.Join("; ", broken)}");
            var frame = FrameArguments.Examples(outer);
            var programs = ComponentPrograms.Prepare(library);
            var linked = ComponentGridGenerator.Played(wrapper.Actions, programs, frame);
            var rows = ComponentGridGenerator.Played(after.Actions, programs, frame);
            if (!linked.SequenceEqual(rows))
                failures.Add($"seed {seed}: {Describe(arguments)}: the call plays {string.Join(" | ", linked)}\nbut Unlink plays {string.Join(" | ", rows)}");
            // With another grid and number for the wrapper too.
            var other = new FrameArguments(new Dictionary<string, JsonNode>
            {
                ["Bag"] = new JsonObject { ["First"] = Point(5, 7), ["Last"] = Point(905, 507), ["Columns"] = 100, ["Rows"] = 100 },
                ["Which"] = Number(1),
            });
            linked = ComponentGridGenerator.Played(wrapper.Actions, programs, other);
            rows = ComponentGridGenerator.Played(after.Actions, programs, other);
            if (!linked.SequenceEqual(rows))
                failures.Add($"seed {seed}: {Describe(arguments)} with another grid: the call plays {string.Join(" | ", linked)}\nbut Unlink plays {string.Join(" | ", rows)}");
        }
        Assert.True(cases > 100, $"only {cases} cases ({refused} refused)");
        NoFailures(failures, cases);
    }

    [Fact]
    public void A_component_with_grids_comes_back_from_its_JSON_and_its_compact_form_as_it_was()
    {
        var failures = new List<string>();
        for (int seed = 3000; seed < 3300; seed++)
        {
            var (component, library) = new ComponentGridGenerator(seed).Component();
            var others = library.Where(c => c != component).ToList();
            foreach (var (form, text) in new[] { ("JSON", CodeJson.Serialize(component)), ("compact", AiJson.Compact(component)) })
            {
                var read = MacroJson.ParseComponent(text, component.Id, out var problems, null, others);
                if (read == null || problems.Count > 0 || CodeJson.Serialize(read) != CodeJson.Serialize(component))
                    failures.Add($"seed {seed}: the {form} form read back is another component: {string.Join("; ", problems.Select(p => p.Message))}");
                else if (ComponentLibrary.Fingerprint(new LibraryCollection { Components = { read } })
                         != ComponentLibrary.Fingerprint(new LibraryCollection { Components = { component } }))
                    failures.Add($"seed {seed}: the fingerprint changed after the {form} form");
            }
        }
        NoFailures(failures, 300);
    }
}
