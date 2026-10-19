using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Components;

/// <summary>
/// The editor's operations on calls, without UI: Unlink (a linked row becomes its component's actions, one level) and
/// Save as component (some actions become a component and a row that calls it, what came from outside them becoming
/// parameters).
/// </summary>
internal static class ComponentEdits
{
    /// <summary>
    /// The actions a linked row plays, as normal rows, one level: the component's actions with new condition ids, each
    /// bound field given the row's argument (a value is written in the field; a {"From"} keeps the field bound to that
    /// parameter of the component around the row; a {"Found"} on a click's or move's point becomes its "Where … was found"),
    /// a parameter left out its default. The calls inside stay linked, their arguments given the same way. Null, with why,
    /// when an argument has no such equivalent (a {"Found"} for a pixel's point).
    /// </summary>
    public static List<MacroAction>? Unlink(MacroAction row, LibraryComponent component, out string? problem)
    {
        problem = null;
        var actions = component.Actions.ConvertAll(a => a.Clone());
        MacroEditList.RenewConditions(actions);
        var arguments = component.Parameters.ToDictionary(p => p.Name,
            p => row.Arguments?.GetValueOrDefault(p.Name) ?? p.Default, StringComparer.Ordinal);

        foreach (var action in MacroDefinition.Walk(actions))
        {
            if (!Give(action.Bind, component, arguments, (slot, value) => ComponentSlots.Write(action, slot, value), (slot, found) =>
                {
                    if (slot != ComponentSlots.Point || action.Type is not (MacroActionType.Click or MacroActionType.MouseDown
                        or MacroActionType.MouseUp or MacroActionType.MoveTo))
                        return false;
                    action.FoundId = found.Found;
                    action.FoundOffsetX = found.OffsetX;
                    action.FoundOffsetY = found.OffsetY;
                    return true;
                }, out var bind, out problem))
                return null;
            action.Bind = bind;
            action.Cell = KeptCell(action.Cell, bind);
            action.Arguments = Passed(action.Arguments, arguments);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                if (!Give(condition.Bind, component, arguments, (slot, value) => ComponentSlots.Write(condition, slot, value),
                        (_, _) => false, out var conditionBind, out problem))
                    return null;
                condition.Bind = conditionBind;
                condition.Cell = KeptCell(condition.Cell, conditionBind);
                condition.Arguments = Passed(condition.Arguments, arguments);
            }
        }
        return actions;
    }

    /// <summary>A cell stays only while the point still comes from a Grid parameter; once the point is written, it is the point.</summary>
    private static int? KeptCell(int? cell, Dictionary<string, string>? bind) =>
        bind?.ContainsKey(ComponentSlots.Point) == true ? cell : null;

    /// <summary>
    /// Gives each bound field the row's argument: a value is written (<paramref name="write"/>), a {"Found"} goes to
    /// <paramref name="found"/> (false: no equivalent), a {"From"} stays a Bind to that parameter. The Bind that stays, or
    /// null. False, with why, when a field has no equivalent outside the component.
    /// </summary>
    private static bool Give(Dictionary<string, string>? bind, LibraryComponent component, Dictionary<string, JsonNode?> arguments,
        Func<string, object, bool> write, Func<string, ArgumentReference, bool> found, out Dictionary<string, string>? stays,
        out string? problem)
    {
        stays = null;
        problem = null;
        if (bind == null)
            return true;
        var left = new Dictionary<string, string>();
        bool pointWritten = false;
        foreach (var (slot, parameter) in ComponentSlots.InOrder(bind))
        {
            var p = component.Parameters.FirstOrDefault(x => x.Name == parameter);
            JsonNode? argument = arguments.GetValueOrDefault(parameter);
            if (p == null || argument == null)
                continue;
            if (ComponentValues.Reference(argument) is { } reference)
            {
                if (reference.From is string from)
                    left[slot] = from;
                else if (!found(slot, reference))
                {
                    problem = Strings.Library_UnlinkFoundPixel;
                    return false;
                }
            }
            else if (ComponentValues.Read(argument, p.Type, ValueRule.Any, out _) is { } value)
            {
                if (!write(slot, value))
                {
                    problem = Strings.Library_UnlinkCellOutside;
                    return false;
                }
                pointWritten |= slot == ComponentSlots.Point;
            }
        }
        // A point written from a grid is one point: a cell that still comes from outside would have nothing to choose from.
        if (pointWritten && left.ContainsKey(ComponentSlots.Cell))
        {
            problem = Strings.Library_UnlinkCellFrom;
            return false;
        }
        if (pointWritten)
            left.Remove(ComponentSlots.Cell);
        stays = left.Count == 0 ? null : left;
        return true;
    }

    /// <summary>A call inside the unlinked actions: an argument {"From": a parameter of the unlinked component} becomes the row's argument for it.</summary>
    private static Dictionary<string, JsonNode?>? Passed(Dictionary<string, JsonNode?>? inner, Dictionary<string, JsonNode?> arguments)
    {
        if (inner == null)
            return null;
        var result = new Dictionary<string, JsonNode?>();
        foreach (var (name, value) in inner)
            result[name] = ComponentValues.Reference(value) is { From: string from } && arguments.TryGetValue(from, out JsonNode? given)
                ? given?.DeepClone()
                : value?.DeepClone();
        return result;
    }

    /// <summary>
    /// What Save as component makes of some actions: the component's actions (copies, new condition ids) and parameters,
    /// and the arguments of the row that calls it in their place. What the actions took from outside them becomes a
    /// parameter: a "Where … was found" of a condition outside them a Point (the row passes {"Found"} it, with the offset);
    /// a {"Found"} argument of a call inside them the same; a field bound, or a {"From"}, to a parameter of the component
    /// around them (<paramref name="outer"/>) a parameter like it (the row passes {"From"} it).
    /// </summary>
    public static (List<MacroAction> Actions, List<ComponentParameter> Parameters, Dictionary<string, JsonNode?>? Arguments) Lift(
        IReadOnlyList<MacroAction> selected, IReadOnlyList<ComponentParameter>? outer, IReadOnlyDictionary<Guid, string> outsideNames)
    {
        var actions = selected.Select(a => a.Clone()).ToList();
        var renewed = MacroEditList.RenewConditions(actions);
        var inside = renewed.Values.ToHashSet();
        var parameters = new List<ComponentParameter>();
        var arguments = new Dictionary<string, JsonNode?>();
        var places = new Dictionary<(Guid Id, int X, int Y), string>();
        var passed = new Dictionary<string, string>();

        string Place(Guid id, int offsetX, int offsetY, PointValue example)
        {
            if (places.TryGetValue((id, offsetX, offsetY), out string? known))
                return known;
            string name = Unique(NameFrom(outsideNames.GetValueOrDefault(id), "Place"), parameters);
            parameters.Add(new ComponentParameter { Name = name, Type = ParameterType.Point, Example = ComponentValues.ToJson(example) });
            arguments[name] = ComponentValues.FoundArgument(id, offsetX, offsetY);
            places[(id, offsetX, offsetY)] = name;
            return name;
        }

        string? Outer(string parameter)
        {
            if (passed.TryGetValue(parameter, out string? known))
                return known;
            if (outer?.FirstOrDefault(p => p.Name == parameter) is not { } p)
                return null;
            var copy = p.Clone();
            copy.Name = Unique(p.Name, parameters);
            parameters.Add(copy);
            arguments[copy.Name] = ComponentValues.FromArgument(p.Name);
            passed[parameter] = copy.Name;
            return copy.Name;
        }

        foreach (var action in MacroDefinition.Walk(actions))
        {
            // The fields bound to the outer component's parameters first: a place's parameter, bound below, is already one of
            // the new component's own, even when the outer component has a parameter of the same name.
            action.Bind = Rebound(action.Bind, Outer);
            if (action.FoundId is Guid foundId && !inside.Contains(foundId))
            {
                string name = Place(foundId, action.FoundOffsetX, action.FoundOffsetY, new PointValue(action.X, action.Y));
                action.FoundId = null;
                action.FoundOffsetX = action.FoundOffsetY = 0;
                (action.Bind ??= new Dictionary<string, string>())[ComponentSlots.Point] = name;
            }
            action.Arguments = Lifted(action.Arguments, inside, Place, Outer);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                condition.Bind = Rebound(condition.Bind, Outer);
                condition.Arguments = Lifted(condition.Arguments, inside, Place, Outer);
            }
        }
        return (actions, parameters, arguments.Count == 0 ? null : arguments);
    }

    private static Dictionary<string, string>? Rebound(Dictionary<string, string>? bind, Func<string, string?> outer)
    {
        if (bind == null)
            return null;
        var result = new Dictionary<string, string>();
        foreach (var (slot, parameter) in bind)
            result[slot] = outer(parameter) ?? parameter;
        return result;
    }

    private static Dictionary<string, JsonNode?>? Lifted(Dictionary<string, JsonNode?>? arguments, HashSet<Guid> inside,
        Func<Guid, int, int, PointValue, string> place, Func<string, string?> outer)
    {
        if (arguments == null)
            return null;
        var result = new Dictionary<string, JsonNode?>();
        foreach (var (name, value) in arguments)
        {
            var reference = ComponentValues.Reference(value);
            if (reference is { Found: Guid id } && !inside.Contains(id))
                result[name] = ComponentValues.FromArgument(place(id, reference.OffsetX, reference.OffsetY, new PointValue(0, 0)));
            else if (reference is { From: string from } && outer(from) is string lifted)
                result[name] = ComponentValues.FromArgument(lifted);
            else
                result[name] = value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// Writes each parameter's Example into the fields bound to it, in the component's actions and conditions: a bound field
    /// always holds its parameter's Example (what the editor shows, what a test of the component plays).
    /// </summary>
    public static void WriteExamples(LibraryComponent component)
    {
        object? Example(string parameter, ComponentSlot slot, bool hasCell) =>
            component.Parameters.FirstOrDefault(p => p.Name == parameter) is { Example: { } example }
                ? ComponentSlots.Read(slot, example, hasCell)
                : null;
        // A cell its grid's Example doesn't have leaves the point as it is: the checks name it.
        foreach (var action in MacroDefinition.Walk(component.Actions))
        {
            foreach (var (slot, parameter) in ComponentSlots.InOrder(action.Bind))
                if (ComponentSlots.Find(action, slot) is { } s && Example(parameter, s, action.Cell != null) is { } value)
                    ComponentSlots.Write(action, slot, value);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                foreach (var (slot, parameter) in ComponentSlots.InOrder(condition.Bind))
                    if (ComponentSlots.Find(condition, slot) is { } s && Example(parameter, s, condition.Cell != null) is { } value)
                        ComponentSlots.Write(condition, slot, value);
        }
    }

    /// <summary>
    /// Whether <see cref="WriteExamples"/> replaced a value someone wrote in a bound field: <paramref name="given"/> is the
    /// component as written, <paramref name="written"/> the same after WriteExamples. A field counts when its value differs
    /// from the Example written, from the Example its parameter had in <paramref name="before"/> (a field still holding the
    /// old Example only follows a new one) and from its default (a field left out).
    /// </summary>
    public static bool ReplacedWritten(LibraryComponent given, LibraryComponent written, LibraryComponent? before)
    {
        static string Text(object? value) => System.Text.Json.JsonSerializer.Serialize(value, CodeJson.Options);
        // The same actions with the Examples the parameters had before written in.
        var old = given.Clone();
        if (before != null)
        {
            old.Parameters = before.Parameters.ConvertAll(p => p.Clone());
            WriteExamples(old);
        }
        var givenActions = MacroDefinition.Walk(given.Actions).ToList();
        var writtenActions = MacroDefinition.Walk(written.Actions).ToList();
        var oldActions = MacroDefinition.Walk(old.Actions).ToList();
        bool Mine(string mine, object? writtenValue, object? oldValue, object? defaultValue) =>
            mine != Text(writtenValue) && (before == null || mine != Text(oldValue)) && mine != Text(defaultValue);
        for (int i = 0; i < givenActions.Count && i < writtenActions.Count && i < oldActions.Count; i++)
        {
            MacroAction g = givenActions[i], w = writtenActions[i], o = oldActions[i];
            foreach (var (slot, _) in ComponentSlots.InOrder(g.Bind))
                if (Mine(Text(ComponentSlots.Value(g, slot)), ComponentSlots.Value(w, slot), ComponentSlots.Value(o, slot),
                        ComponentSlots.Value(new MacroAction { Type = g.Type }, slot)))
                    return true;
            var givenConditions = g.If?.Conditions ?? new List<MacroCondition>();
            var writtenConditions = w.If?.Conditions ?? new List<MacroCondition>();
            var oldConditions = o.If?.Conditions ?? new List<MacroCondition>();
            for (int j = 0; j < givenConditions.Count && j < writtenConditions.Count && j < oldConditions.Count; j++)
            {
                MacroCondition gc = givenConditions[j], wc = writtenConditions[j], oc = oldConditions[j];
                foreach (var (slot, _) in ComponentSlots.InOrder(gc.Bind))
                    if (Mine(Text(ComponentSlots.Value(gc, slot)), ComponentSlots.Value(wc, slot), ComponentSlots.Value(oc, slot),
                            ComponentSlots.Value(new MacroCondition { Kind = gc.Kind }, slot)))
                        return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A parameter renamed in the component's editor: its fields' Bind and the {"From"} of the calls inside follow, so the
    /// component keeps working.
    /// </summary>
    public static void Rename(List<MacroAction> actions, string from, string to)
    {
        static void Bind(Dictionary<string, string>? bind, string from, string to)
        {
            foreach (string slot in bind?.Where(b => b.Value == from).Select(b => b.Key).ToList() ?? new List<string>())
                bind![slot] = to;
        }
        static void Arguments(Dictionary<string, JsonNode?>? arguments, string from, string to)
        {
            foreach (string name in arguments?.Where(a => ComponentValues.Reference(a.Value)?.From == from).Select(a => a.Key).ToList() ?? new List<string>())
                arguments![name] = ComponentValues.FromArgument(to);
        }
        foreach (var action in MacroDefinition.Walk(actions))
        {
            Bind(action.Bind, from, to);
            Arguments(action.Arguments, from, to);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                Bind(condition.Bind, from, to);
                Arguments(condition.Arguments, from, to);
            }
        }
    }

    /// <summary>
    /// A parameter removed in the component's editor: its fields keep their values (no Bind), and the calls inside that
    /// passed it on get its Example instead.
    /// </summary>
    public static void Forget(List<MacroAction> actions, ComponentParameter parameter)
    {
        foreach (var action in MacroDefinition.Walk(actions))
        {
            Drop(action.Bind);
            action.Cell = NoGridNoCell(action.Cell, action.Bind);
            Replace(action.Arguments);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                Drop(condition.Bind);
                condition.Cell = NoGridNoCell(condition.Cell, condition.Bind);
                Replace(condition.Arguments);
            }
        }

        // The point keeps the cell's point of the Example (written there): without its grid, the cell and its Bind go.
        int? NoGridNoCell(int? cell, Dictionary<string, string>? bind)
        {
            if (cell == null || bind?.ContainsKey(ComponentSlots.Point) == true)
                return cell;
            bind?.Remove(ComponentSlots.Cell);
            return null;
        }

        void Drop(Dictionary<string, string>? bind)
        {
            foreach (string slot in bind?.Where(b => b.Value == parameter.Name).Select(b => b.Key).ToList() ?? new List<string>())
                bind!.Remove(slot);
        }

        void Replace(Dictionary<string, JsonNode?>? arguments)
        {
            foreach (string name in arguments?.Where(a => ComponentValues.Reference(a.Value)?.From == parameter.Name).Select(a => a.Key).ToList()
                                    ?? new List<string>())
                arguments![name] = parameter.Example?.DeepClone();
        }
    }

    /// <summary>
    /// A parameter whose type or unit changed in the component's editor (<paramref name="old"/> as it was, named
    /// <paramref name="name"/> now): its fields, of the old type, keep their values (no Bind), and the calls inside that
    /// passed it on get its old Example, a value of the kind they take, never one of the new type.
    /// </summary>
    public static void Retyped(List<MacroAction> actions, ComponentParameter old, string name)
    {
        var was = old.Clone();
        was.Name = name;
        Forget(actions, was);
    }

    /// <summary>A parameter's name made from a condition's name ("Remove Affix" → "Remove_Affix"), or <paramref name="fallback"/>.</summary>
    public static string NameFrom(string? text, string fallback)
    {
        var name = new StringBuilder();
        foreach (char c in (text ?? "").Normalize(NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                name.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && name.Length > 0 && name[^1] != '_')
                name.Append('_');
        }
        string result = name.ToString().Trim('_');
        if (result.Length == 0 || !char.IsAsciiLetter(result[0]))
            result = fallback + (result.Length == 0 ? "" : "_" + result);
        return result.Length > ComponentChecks.MaxParameterName ? result[..ComponentChecks.MaxParameterName].TrimEnd('_') : result;
    }

    /// <summary>
    /// The name, or "Name2", "Name3"… when a parameter already has it; the name is shortened so that the number fits in a
    /// parameter's name (<see cref="ComponentChecks.MaxParameterName"/>).
    /// </summary>
    private static string Unique(string name, List<ComponentParameter> parameters)
    {
        string candidate = name;
        for (int n = 2; parameters.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            string number = n.ToString(CultureInfo.InvariantCulture);
            int room = ComponentChecks.MaxParameterName - number.Length;
            candidate = (name.Length > room ? name[..room] : name) + number;
        }
        return candidate;
    }
}
