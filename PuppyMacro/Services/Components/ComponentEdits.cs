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
                }, out var bind))
            {
                problem = Strings.Library_UnlinkFoundPixel;
                return null;
            }
            action.Bind = bind;
            action.Arguments = Passed(action.Arguments, arguments);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                if (!Give(condition.Bind, component, arguments, (slot, value) => ComponentSlots.Write(condition, slot, value),
                        (_, _) => false, out var conditionBind))
                {
                    problem = Strings.Library_UnlinkFoundPixel;
                    return null;
                }
                condition.Bind = conditionBind;
                condition.Arguments = Passed(condition.Arguments, arguments);
            }
        }
        return actions;
    }

    /// <summary>
    /// Gives each bound field the row's argument: a value is written (<paramref name="write"/>), a {"Found"} goes to
    /// <paramref name="found"/> (false: no equivalent), a {"From"} stays a Bind to that parameter. The Bind that stays, or null.
    /// </summary>
    private static bool Give(Dictionary<string, string>? bind, LibraryComponent component, Dictionary<string, JsonNode?> arguments,
        Action<string, object> write, Func<string, ArgumentReference, bool> found, out Dictionary<string, string>? stays)
    {
        stays = null;
        if (bind == null)
            return true;
        var left = new Dictionary<string, string>();
        foreach (var (slot, parameter) in bind)
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
                    return false;
            }
            else if (ComponentValues.Read(argument, p.Type, ValueRule.Any, out _) is { } value)
                write(slot, value);
        }
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
            if (action.FoundId is Guid foundId && !inside.Contains(foundId))
            {
                string name = Place(foundId, action.FoundOffsetX, action.FoundOffsetY, new PointValue(action.X, action.Y));
                action.FoundId = null;
                action.FoundOffsetX = action.FoundOffsetY = 0;
                (action.Bind ??= new Dictionary<string, string>())[ComponentSlots.Point] = name;
            }
            action.Bind = Rebound(action.Bind, Outer);
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
        return result.Length > 40 ? result[..40].TrimEnd('_') : result;
    }

    /// <summary>The name, or "Name2", "Name3"… when a parameter already has it.</summary>
    private static string Unique(string name, List<ComponentParameter> parameters)
    {
        string candidate = name;
        for (int n = 2; parameters.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = name + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}
