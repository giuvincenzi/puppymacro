using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Components;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Components with Grid parameters made at random from a seed, the same every run (what <see cref="ComponentGenerator"/>
/// does for the other types): a grid of any shape (one cell, one row or column, either direction, negative coordinates, the
/// most columns or rows), clicks, mouse buttons, moves and pixels at one of its cells, fixed or from a Number (Count), and
/// calls that pass the grid and the cell on to another component. The values for a call are often a cell past the end.
/// </summary>
internal sealed class ComponentGridGenerator
{
    private static readonly MacroActionType[] PointTypes =
        { MacroActionType.Click, MacroActionType.MouseDown, MacroActionType.MouseUp, MacroActionType.MoveTo };

    private readonly Random _random;
    private int _names;

    public ComponentGridGenerator(int seed) => _random = new Random(seed);

    public Random Random => _random;

    /// <summary>A grid at random: from one cell to the most, either way, anywhere on a desktop of several monitors.</summary>
    public JsonNode Grid()
    {
        int columns, rows;
        switch (_random.Next(10))
        {
            case 0: (columns, rows) = (1, 1); break;
            case 1: (columns, rows) = (1, _random.Next(2, 20)); break;
            case 2: (columns, rows) = (_random.Next(2, 20), 1); break;
            case 3: (columns, rows) = _random.Next(2) == 0 ? (1000, 10) : (10, 1000); break;
            default: (columns, rows) = (_random.Next(1, 13), _random.Next(1, 7)); break;
        }
        int firstX = _random.Next(-8000, 8000), firstY = _random.Next(-3000, 3000);
        int lastX = columns == 1 ? firstX : firstX + _random.Next(-2000, 2000);
        int lastY = rows == 1 ? firstY : firstY + _random.Next(-1500, 1500);
        return new JsonObject
        {
            ["First"] = Point(firstX, firstY), ["Last"] = Point(lastX, lastY), ["Columns"] = columns, ["Rows"] = rows,
        };
    }

    public static GridValue Read(JsonNode node) => (GridValue)ComponentValues.Read(node, ParameterType.Grid, ValueRule.Any, out _)!;

    /// <summary>
    /// A component with a Grid parameter G and a Count N: 1 to 4 actions at a cell of G (fixed, inside its Example, or from
    /// N), some pixels at a cell, and sometimes a call to <see cref="Inner"/> passing G on. Its Examples keep every rule.
    /// </summary>
    public (LibraryComponent Component, List<LibraryComponent> Library) Component()
    {
        var gridExample = Grid();
        int cells = Read(gridExample).Cells;
        var parameters = new List<ComponentParameter>
        {
            Param("G", ParameterType.Grid, gridExample, _random.Next(4) == 0 ? gridExample.DeepClone() : null),
            Param("N", ParameterType.Number, Number(_random.Next(1, cells + 1)), unit: NumberUnit.Count),
        };
        var library = new List<LibraryComponent>();
        var actions = new List<MacroAction>();
        int count = _random.Next(1, 5);
        for (int i = 0; i < count; i++)
        {
            switch (_random.Next(6))
            {
                case 0:
                case 1:
                case 2:
                    actions.Add(AtCell(PointAction(), cells));
                    break;
                case 3:
                case 4:
                    actions.Add(If(AtCell(Pixel(1, 1, "#FFFFFF", not: _random.Next(3) == 0), cells), Press()));
                    break;
                default:
                {
                    var inner = library.FirstOrDefault() ?? Inner();
                    if (library.Count == 0)
                        library.Add(inner);
                    var slot = _random.Next(2) == 0 ? From("N") : Number(_random.Next(1, cells + 1));
                    actions.Add(Call(inner, ("Inv", From("G")), ("Slot", slot)));
                    break;
                }
            }
        }
        var component = Comp($"Grid generated {++_names}", parameters, actions.ToArray());
        ComponentEdits.WriteExamples(component);
        library.Add(component);
        return (component, library);
    }

    /// <summary>A component that clicks a cell of its own grid, Inv, the cell from its Slot.</summary>
    private LibraryComponent Inner()
    {
        var click = Bound(PointAction(), ("Point", "Inv"), ("Cell", "Slot"));
        click.Cell = 1;
        var inner = Comp($"Grid inner {++_names}", new[]
        {
            Param("Inv", ParameterType.Grid, new JsonObject
            {
                ["First"] = Point(0, 0), ["Last"] = Point(9000, 9000), ["Columns"] = 100, ["Rows"] = 100,
            }),
            Param("Slot", ParameterType.Number, Number(1), unit: NumberUnit.Count),
        }, click);
        ComponentEdits.WriteExamples(inner);
        return inner;
    }

    private MacroAction PointAction() => new()
    {
        Type = PointTypes[_random.Next(PointTypes.Length)], Vk = 0x01, X = 1, Y = 1, HoldMs = 10, Repeat = 1, ClickCount = 1,
    };

    private MacroAction AtCell(MacroAction action, int cells)
    {
        action.Bind = new Dictionary<string, string> { ["Point"] = "G" };
        action.Cell = _random.Next(1, cells + 1);
        if (_random.Next(2) == 0)
            action.Bind["Cell"] = "N";
        return action;
    }

    private MacroCondition AtCell(MacroCondition condition, int cells)
    {
        condition.Bind = new Dictionary<string, string> { ["Point"] = "G" };
        condition.Cell = _random.Next(1, cells + 1);
        if (_random.Next(2) == 0)
            condition.Bind["Cell"] = "N";
        return condition;
    }

    /// <summary>
    /// The values of a call: G the Example, another grid or left out for its Default; N a cell of that grid, the last, one
    /// past it, or left out (it is required: the call is then refused).
    /// </summary>
    public Dictionary<string, JsonNode?> Arguments(LibraryComponent component)
    {
        var arguments = new Dictionary<string, JsonNode?>();
        var g = component.Parameters[0];
        JsonNode grid = g.Default != null && _random.Next(3) == 0 ? g.Default
            : _random.Next(3) == 0 ? g.Example! : Grid();
        if (!ReferenceEquals(grid, g.Default))
            arguments["G"] = grid.DeepClone();
        int cells = Read(grid).Cells;
        arguments["N"] = Number(_random.Next(6) switch
        {
            0 => cells,
            1 => cells + 1,
            2 => 1,
            _ => _random.Next(1, cells + 2),
        });
        return arguments;
    }

    /// <summary>
    /// What a list of actions plays in a frame, as the runner reaches it: each action bound ("skipped" for a cell its grid
    /// doesn't have), each If's conditions bound, its Then and Else, a linked row in a frame of its own.
    /// </summary>
    public static List<string> Played(IEnumerable<MacroAction> actions, ComponentPrograms programs, FrameArguments frame)
    {
        var played = new List<string>();
        foreach (var action in actions)
        {
            if (action.Type == MacroActionType.Component)
            {
                var callee = programs.Find(action.ComponentId!.Value)!;
                var inner = ComponentPrograms.Evaluate(callee, action.Arguments, frame, new Dictionary<Guid, Point>(), out string? missing);
                if (inner == null)
                    played.Add($"missing {missing}");
                else
                    played.AddRange(Played(callee.Actions, programs, inner));
                continue;
            }
            var bound = ComponentPrograms.Bound(action, frame);
            if (bound == null)
            {
                played.Add("skipped");
                continue;
            }
            if (action.Type != MacroActionType.If)
            {
                played.Add(bound.FoundId == null && bound.Type is MacroActionType.Click or MacroActionType.MouseDown
                    or MacroActionType.MouseUp or MacroActionType.MoveTo
                    ? $"{bound.Type} {bound.X},{bound.Y}"
                    : bound.Type.ToString());
                continue;
            }
            foreach (var condition in action.If!.Conditions)
                played.Add(programs.Effective(condition, frame, new Dictionary<Guid, Point>()) is { } effective
                    ? $"{effective.Kind} {effective.X},{effective.Y}"
                    : "skipped");
            played.AddRange(Played(action.If.Then, programs, frame));
            played.AddRange(Played(action.If.Else, programs, frame));
        }
        return played;
    }
}
