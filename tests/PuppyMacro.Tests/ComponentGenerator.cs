using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Components made at random from a seed, the same every run: parameters of every type bound to one or two fields of their
/// kind (every slot of every action and condition), Examples and Defaults inside the rules of their fields, and values for a
/// call that are often at a limit or just past it. The tests of invariants run hundreds of them.
/// </summary>
internal sealed class ComponentGenerator
{
    /// <summary>A field a parameter can be bound to, and how to make an action that binds it to a parameter.</summary>
    public sealed record Field(string Name, ParameterType Type, NumberUnit? Unit, Func<string, string, MacroAction> Make);

    public static readonly IReadOnlyList<Field> Fields = new Field[]
    {
        new("Click.Point", ParameterType.Point, null, (p, _) => Bound(Click(10, 20), ("Point", p))),
        new("MoveTo.Point", ParameterType.Point, null, (p, _) => Bound(new MacroAction { Type = MacroActionType.MoveTo, X = 1, Y = 2 }, ("Point", p))),
        new("Pixel.Point", ParameterType.Point, null, (p, _) => If(Bound(Pixel(), ("Point", p)), Press())),
        new("Press.DelayMs", ParameterType.Number, NumberUnit.Ms, (p, _) => Bound(Press(), ("DelayMs", p))),
        new("Press.HoldMs", ParameterType.Number, NumberUnit.Ms, (p, _) => Bound(Press(), ("HoldMs", p))),
        new("Press.RepeatPauseMs", ParameterType.Number, NumberUnit.Ms, (p, _) => Bound(Press(), ("RepeatPauseMs", p))),
        new("Click.Repeat", ParameterType.Number, NumberUnit.Count, (p, _) => Bound(Click(10, 20), ("Repeat", p))),
        new("Scroll.ScrollSteps", ParameterType.Number, NumberUnit.Count,
            (p, _) => Bound(new MacroAction { Type = MacroActionType.Scroll, ScrollSteps = 1 }, ("ScrollSteps", p))),
        new("Pixel.Tolerance", ParameterType.Number, NumberUnit.Percent, (p, _) => If(Bound(Pixel(), ("Tolerance", p)), Press())),
        new("Image.Similarity", ParameterType.Number, NumberUnit.Percent, (p, n) => If(Bound(ImageFinder(n), ("Similarity", p)), Press())),
        new("If.WaitSeconds", ParameterType.Number, NumberUnit.Seconds, (p, _) =>
        {
            var block = If(Pixel(), Press());
            block.If!.Wait = ConditionWait.UpTo;
            block.If.WaitSeconds = 1;
            return Bound(block, ("WaitSeconds", p));
        }),
        new("Paste.Text", ParameterType.Text, null, (p, _) => Bound(new MacroAction { Type = MacroActionType.PasteText, Text = "x" }, ("Text", p))),
        new("TextCondition.Text", ParameterType.Text, null, (p, n) => If(Bound(TextCondition(n), ("Text", p)), Press())),
        new("Press.Key", ParameterType.Key, null, (p, _) => Bound(Press(), ("Key", p))),
        new("KeyDown.Key", ParameterType.Key, null, (p, _) => Bound(new MacroAction { Type = MacroActionType.KeyDown, Vk = 0x87 }, ("Key", p))),
        new("Pixel.Color", ParameterType.Color, null, (p, _) => If(Bound(Pixel(), ("Color", p)), Press())),
        new("Image.Images", ParameterType.Image, null, (p, n) => If(Bound(ImageFinder(n), ("Images", p)), Press())),
        new("Image.Area", ParameterType.Area, null, (p, n) =>
        {
            var image = ImageFinder(n);
            (image.Area, image.AreaWidth, image.AreaHeight) = (SearchArea.Drawn, 100, 100);
            return If(Bound(image, ("Area", p)), Press());
        }),
        new("TextCondition.Area", ParameterType.Area, null, (p, n) => If(Bound(TextCondition(n), ("Area", p)), Press())),
    };

    private readonly Random _random;
    private int _names;

    public ComponentGenerator(int seed) => _random = new Random(seed);

    public Random Random => _random;

    public static MacroCondition TextCondition(string name) =>
        new() { Kind = ConditionKind.Text, Name = name, Text = "hi", Area = SearchArea.Drawn, AreaWidth = 100, AreaHeight = 100 };

    /// <summary>
    /// A component with 1 to <paramref name="most"/> parameters, each bound to one or two fields of its kind, its Examples
    /// (and some Defaults) inside the rules those fields give, written into the fields.
    /// </summary>
    public LibraryComponent Component(int most = 4)
    {
        var parameters = new List<ComponentParameter>();
        var actions = new List<MacroAction>();
        int count = _random.Next(1, most + 1);
        for (int i = 0; i < count; i++)
        {
            var field = Fields[_random.Next(Fields.Count)];
            string name = $"P{i}";
            parameters.Add(new ComponentParameter { Name = name, Type = field.Type, Unit = field.Unit });
            actions.Add(field.Make(name, NextName()));
            if (_random.Next(3) == 0)
            {
                var same = Fields.Where(f => f.Type == field.Type && f.Unit == field.Unit).ToList();
                actions.Add(same[_random.Next(same.Count)].Make(name, NextName()));
            }
        }
        var component = Comp($"Generated {_names}", parameters, actions.ToArray());
        var signatures = new ComponentSignatures(new[] { component });
        foreach (var parameter in parameters)
        {
            var rule = signatures.RuleOf(component.Id, parameter.Name);
            parameter.Example = Valid(parameter.Type, rule);
            if (_random.Next(10) < 3)
                parameter.Default = Valid(parameter.Type, rule);
        }
        ComponentEdits.WriteExamples(component);
        return component;
    }

    private string NextName() => $"Found {++_names}";

    /// <summary>Arguments for a call: a value for each parameter (an edge value half the time), a parameter with a default sometimes left out.</summary>
    public Dictionary<string, JsonNode?> Arguments(LibraryComponent component, ComponentSignatures signatures)
    {
        var arguments = new Dictionary<string, JsonNode?>();
        foreach (var parameter in component.Parameters)
        {
            if (parameter.Default != null && _random.Next(10) < 3)
                continue;
            var rule = signatures.RuleOf(component.Id, parameter.Name);
            arguments[parameter.Name] = _random.Next(2) == 0 ? Valid(parameter.Type, rule) : Edge(parameter.Type, rule);
        }
        return arguments;
    }

    /// <summary>A value of the type inside the rule, away from the limits of the screen.</summary>
    public JsonNode Valid(ParameterType type, ValueRule rule)
    {
        switch (type)
        {
            case ParameterType.Point:
                return Point(_random.Next(-2000, 2000), _random.Next(-2000, 2000));
            case ParameterType.Number:
            {
                double min = rule.Range.Min, max = Math.Min(rule.Range.Max, rule.Range.Min + 1000);
                if (rule.Whole)
                    return Number(_random.Next((int)Math.Ceiling(min), (int)Math.Floor(max) + 1));
                double n = Math.Round(min + _random.NextDouble() * (max - min), 1);
                return Number(Math.Clamp(n, rule.Range.Min, rule.Range.Max));
            }
            case ParameterType.Text:
                return Text("text " + _random.Next(100));
            case ParameterType.Key:
                return rule.KeyAlone || _random.Next(2) == 0 ? Key(0x87) : Key(0x87, ctrl: true);
            case ParameterType.Color:
                return Text($"#{_random.Next(0x1000000):x6}");
            case ParameterType.Image:
                return _random.Next(2) == 0 ? Images(ImageA) : Images(ImageA, ImageB);
            default:
                return Area(_random.Next(-500, 500), _random.Next(-500, 500), _random.Next(8, 400), _random.Next(8, 400));
        }
    }

    private T Pick<T>(params T[] items) => items[_random.Next(items.Length)];

    /// <summary>A value at a limit, just past it, or of the right shape but odd: what a person or an AI app could write.</summary>
    public JsonNode Edge(ParameterType type, ValueRule rule)
    {
        switch (type)
        {
            case ParameterType.Point:
                return Point(Pick(0, 100_000, -100_000, 100_001, -100_001), Pick(0, 100_000, -100_001));
            case ParameterType.Number:
                return Number(Pick(rule.Range.Min, rule.Range.Max, rule.Range.Min - 1, rule.Range.Max + 1, rule.Range.Min - 0.05,
                    rule.Range.Max + 0.5, 0, -1, 0.05, 0.1, 1, 1.5, 49, 50, 100, 101, 1000, 1001, 60_000, 60_001, 86_400, 86_400_001));
            case ParameterType.Text:
                return Text(Pick("", " ", "   ", "\t", "a", " a "));
            case ParameterType.Key:
                return Key(Pick(0x87, 0x01, 0x02, 0x41, 0x11, 0x10, 0, 255), ctrl: _random.Next(2) == 0);
            case ParameterType.Color:
                return Text(Pick("#000000", "#ABCDEF", "#abcdeg", "000000", "#12345", "#1234567"));
            case ParameterType.Image:
                return Pick<Func<JsonNode>>(
                    () => new JsonArray(),
                    () => Images(ImageA),
                    () => new JsonArray(new JsonObject { ["Id"] = "zz", ["X"] = 0, ["Y"] = 0 }),
                    () => new JsonArray(new JsonObject { ["Id"] = ImageB, ["X"] = 100_001, ["Y"] = 0 }))();
            default:
                return Area(Pick(0, 100_000, 100_001, -100_001), 0, Pick(7, 8, 100_000, 100_001), Pick(8, 50));
        }
    }

    /// <summary>The actions a call plays: each with its bound fields filled from the frame, then without Bind, as the runner sees them.</summary>
    public static List<MacroAction> Played(IEnumerable<MacroAction> actions, FrameArguments frame) =>
        actions.Select(a =>
        {
            var played = ComponentPrograms.Bound(a, frame).Clone();
            played.Bind = null;
            if (played.If != null)
                played.If.Conditions = a.If!.Conditions.ConvertAll(c =>
                {
                    var condition = ComponentPrograms.Bound(c, frame).Clone();
                    condition.Bind = null;
                    return condition;
                });
            return played;
        }).ToList();

    /// <summary>Each action as JSON, its conditions' ids and every Bind left out: what plays, to compare two lists.</summary>
    public static List<string> Comparable(IEnumerable<MacroAction> actions) =>
        actions.Select(a =>
        {
            var copy = a.Clone();
            foreach (var each in MacroDefinition.Walk(new[] { copy }))
            {
                each.Bind = null;
                foreach (var condition in each.If?.Conditions ?? new List<MacroCondition>())
                {
                    condition.Id = Guid.Empty;
                    condition.Bind = null;
                }
            }
            return MacroJson.Serialize(copy);
        }).ToList();
}
