using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Tests;

/// <summary>Builders for the tests of components as functions: parameters, values, calls, the actions they bind.</summary>
internal static class Kit
{
    public const string ImageA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ImageB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static JsonNode Point(int x, int y) => new JsonObject { ["X"] = x, ["Y"] = y };

    public static JsonNode Number(double n) => JsonValue.Create(n);

    public static JsonNode Text(string s) => JsonValue.Create(s);

    public static JsonNode Key(int vk, bool ctrl = false) => new JsonObject { ["Vk"] = vk, ["Ctrl"] = ctrl };

    public static JsonNode Area(int x, int y, int w, int h) => new JsonObject { ["X"] = x, ["Y"] = y, ["Width"] = w, ["Height"] = h };

    public static JsonNode Images(params string[] ids) =>
        new JsonArray(ids.Select((id, i) => (JsonNode)new JsonObject { ["Id"] = id, ["X"] = 10 * i, ["Y"] = 20 }).ToArray());

    public static JsonNode From(string parameter) => ComponentValues.FromArgument(parameter);

    public static JsonNode Found(Guid condition, int dx = 0, int dy = 0) => ComponentValues.FoundArgument(condition, dx, dy);

    public static ComponentParameter Param(string name, ParameterType type, JsonNode example, JsonNode? @default = null, NumberUnit? unit = null) =>
        new() { Name = name, Type = type, Unit = unit ?? (type == ParameterType.Number ? NumberUnit.Ms : null), Example = example, Default = @default };

    public static ComponentParameter SlotParam(string name = "Slot") => Param(name, ParameterType.Point, Point(3912, 1056));

    public static ComponentParameter HoldParam(string name = "Hold", JsonNode? @default = null) =>
        Param(name, ParameterType.Number, Number(30), @default, NumberUnit.Ms);

    public static LibraryComponent Comp(string name, IEnumerable<ComponentParameter> parameters, params MacroAction[] actions) => new()
    {
        Name = name,
        Purpose = ComponentPurpose.Act,
        Concept = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()),
        Parameters = parameters.ToList(),
        Actions = actions.ToList(),
    };

    public static LibraryComponent Comp(string name, params MacroAction[] actions) => Comp(name, Array.Empty<ComponentParameter>(), actions);

    public static MacroAction Bound(MacroAction action, params (string Slot, string Parameter)[] binds)
    {
        action.Bind = binds.ToDictionary(b => b.Slot, b => b.Parameter);
        return action;
    }

    public static MacroCondition Bound(MacroCondition condition, params (string Slot, string Parameter)[] binds)
    {
        condition.Bind = binds.ToDictionary(b => b.Slot, b => b.Parameter);
        return condition;
    }

    public static MacroAction Call(LibraryComponent component, params (string Name, JsonNode? Value)[] arguments) => Call(component.Id, arguments);

    public static MacroAction Call(Guid component, params (string Name, JsonNode? Value)[] arguments) => new()
    {
        Type = MacroActionType.Component,
        ComponentId = component,
        Arguments = arguments.Length == 0 ? null : arguments.ToDictionary(a => a.Name, a => a.Value),
    };

    public static MacroCondition CallCondition(LibraryComponent component, string name, params (string Name, JsonNode? Value)[] arguments) => new()
    {
        Kind = ConditionKind.Component,
        Name = name,
        ComponentId = component.Id,
        Arguments = arguments.Length == 0 ? null : arguments.ToDictionary(a => a.Name, a => a.Value),
    };

    public static MacroAction Press(int vk = 0x87, double hold = 10) => new() { Type = MacroActionType.PressKey, Vk = vk, HoldMs = hold };

    public static MacroAction Click(int x = 10, int y = 20, Guid? found = null) =>
        new() { Type = MacroActionType.Click, Vk = 0x01, X = x, Y = y, FoundId = found };

    public static MacroCondition Pixel(int x = 5, int y = 5, string color = "#000000", bool not = false) =>
        new() { Kind = ConditionKind.Pixel, X = x, Y = y, Color = color, Tolerance = 5, Not = not };

    public static MacroCondition ImageFinder(string name = "Remove Affix", bool not = false) => new()
    {
        Kind = ConditionKind.Image,
        Name = name,
        Images = new() { new ConditionImage { Id = ImageA, X = 100, Y = 200 } },
        Similarity = 90,
        Not = not,
    };

    public static MacroAction If(MacroCondition condition, params MacroAction[] then) => If(new[] { condition }, ConditionMatch.All, then);

    public static MacroAction If(IEnumerable<MacroCondition> conditions, ConditionMatch match, params MacroAction[] then) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf { Conditions = conditions.ToList(), Match = match, Then = then.ToList() },
    };

    public static MacroDefinition Macro(string name, params MacroAction[] actions) => new() { Name = name, Actions = actions.ToList() };

    /// <summary>Every problem of the Library and the macros' calls, as "where: path: message" lines.</summary>
    public static List<string> Problems(IEnumerable<LibraryComponent> components, params MacroDefinition[] macros) =>
        ComponentChecks.CheckAll(new ComponentSignatures(components), macros, null)
            .Select(p => $"{p.Where}: {p.Path}: {p.Message}")
            .ToList();

    /// <summary>The problems a component's own check names (its JSON read back as the Code view and every save read it).</summary>
    public static List<string> ComponentProblems(LibraryComponent component, params LibraryComponent[] library) =>
        ComponentLibrary.Problems(component, null, library).Select(p => p.Message).ToList();

    /// <summary>The problems of a macro's JSON, checked with the Library's components.</summary>
    public static List<string> MacroProblems(MacroDefinition macro, params LibraryComponent[] library)
    {
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out var problems, components: new ComponentSignatures(library));
        return problems.Select(p => p.Message).ToList();
    }
}
