using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Components;

/// <summary>Who calls a component: a macro or another component, and how many of its rows and conditions do.</summary>
internal sealed record CallSite(Guid OwnerId, string OwnerName, bool IsMacro, int Calls);

/// <summary>
/// What a new version of a component changes for the calls that exist (the guided update): the parameters it no longer has
/// (their arguments go), the ones renamed (their arguments follow), and the ones every call must now give a value of a new
/// kind for (new without a default, a default taken away, another type or unit), for which the user gives one value for all
/// the calls. <see cref="Apply"/> makes the new macros and components; the caller saves them with the component, all
/// together after the checks, or nothing.
/// </summary>
internal sealed class SignatureUpdate
{
    public required Guid ComponentId { get; init; }
    public required string ComponentName { get; init; }

    /// <summary>Parameters it no longer has: their arguments are taken out of every call.</summary>
    public required IReadOnlyList<string> Removed { get; init; }

    /// <summary>Old name to new name: the arguments follow.</summary>
    public required IReadOnlyDictionary<string, string> Renamed { get; init; }

    /// <summary>
    /// Parameters a call must give a value of their kind for, which some calls don't: each needs one value, given to those
    /// calls.
    /// </summary>
    public required IReadOnlyList<ComponentParameter> NeedValue { get; init; }

    /// <summary>The macros and components with calls that change.</summary>
    public required IReadOnlyList<CallSite> Sites { get; init; }

    /// <summary>Whether the calls change at all.</summary>
    public bool Needed => Sites.Count > 0 && (Removed.Count > 0 || Renamed.Count > 0 || NeedValue.Count > 0);

    public int Calls => Sites.Sum(s => s.Calls);

    /// <summary>
    /// What saving <paramref name="after"/> in place of <paramref name="before"/> changes for the calls in these macros and
    /// components. <paramref name="renamed"/>: the parameters the user renamed (old name to new), when known; a parameter
    /// that is gone otherwise counts as removed.
    /// </summary>
    public static SignatureUpdate Compare(LibraryComponent before, LibraryComponent after, IEnumerable<MacroDefinition> macros,
        IEnumerable<LibraryComponent> components, IReadOnlyDictionary<string, string>? renamed = null)
    {
        renamed = (renamed ?? new Dictionary<string, string>())
            .Where(r => r.Key != r.Value && before.Parameters.Any(p => p.Name == r.Key) && after.Parameters.Any(p => p.Name == r.Value))
            .ToDictionary(r => r.Key, r => r.Value);
        // A name a rename takes over is gone as the old parameter, even though a parameter has it now.
        var removed = before.Parameters.Select(p => p.Name)
            .Where(n => !renamed.ContainsKey(n) && (after.Parameters.All(p => p.Name != n) || renamed.Values.Contains(n)))
            .ToList();
        // The old name of a parameter of the new version: the one it was renamed from, its own name when it kept it, none
        // when it is new (also when its name was another parameter's, renamed away).
        string? OldName(string name) => renamed.FirstOrDefault(r => r.Value == name).Key
            ?? (renamed.ContainsKey(name) || removed.Contains(name) ? null : name);
        ComponentParameter? Old(string name) => OldName(name) is { } old ? before.Parameters.FirstOrDefault(p => p.Name == old) : null;
        var sites = new List<CallSite>();
        var needed = new HashSet<string>();
        foreach (var (id, name, isMacro, actions) in Owners(macros, components).Where(o => o.Id != after.Id))
        {
            int calls = 0;
            foreach (var arguments in CallsTo(actions, after.Id))
            {
                calls++;
                foreach (var parameter in after.Parameters)
                    if (Fits(OldName(parameter.Name) is { } old ? arguments?.GetValueOrDefault(old) : null, parameter, Old(parameter.Name)) == false)
                        needed.Add(parameter.Name);
            }
            if (calls > 0)
                sites.Add(new CallSite(id, name, isMacro, calls));
        }
        var typeChanged = after.Parameters
            .Where(p => Old(p.Name) is { } old && (old.Type != p.Type || old.Unit != p.Unit))
            .Select(p => p.Name)
            .ToHashSet();
        return new SignatureUpdate
        {
            ComponentId = after.Id,
            ComponentName = after.Name,
            Removed = removed,
            Renamed = renamed,
            NeedValue = after.Parameters.Where(p => needed.Contains(p.Name)).ToList(),
            Sites = sites,
            TypeChangedNames = typeChanged,
        };
    }

    /// <summary>
    /// Whether a call's argument still works for the new parameter: true when it does (or a reference whose fit the checks
    /// judge), false when the call needs the new value (none and no default, a value of another type).
    /// </summary>
    private static bool? Fits(JsonNode? argument, ComponentParameter parameter, ComponentParameter? old)
    {
        if (argument == null)
            return parameter.Default != null ? true : false;
        if (ComponentValues.Reference(argument) != null)
            return null; // {"From"} / {"Found"}: the checks say whether it still fits; a value can't replace it
        bool sameKind = old == null || (old.Type == parameter.Type && old.Unit == parameter.Unit);
        return sameKind && ComponentValues.Read(argument, parameter.Type, ValueRule.Any, out _) != null;
    }

    /// <summary>
    /// Copies of the macros and components with calls to it, each call changed: arguments of removed parameters out, renamed
    /// ones under their new name, and <paramref name="values"/> (by parameter) given to the calls that need them.
    /// </summary>
    public (List<MacroDefinition> Macros, List<LibraryComponent> Components) Apply(IEnumerable<MacroDefinition> macros,
        IEnumerable<LibraryComponent> components, IReadOnlyDictionary<string, JsonNode> values, LibraryComponent after)
    {
        var changedMacros = new List<MacroDefinition>();
        var changedComponents = new List<LibraryComponent>();
        var ids = Sites.Select(s => s.OwnerId).ToHashSet();
        foreach (var macro in macros.Where(m => ids.Contains(m.Id)))
        {
            var copy = macro.Clone();
            Change(copy.Actions, after, values);
            changedMacros.Add(copy);
        }
        foreach (var component in components.Where(c => ids.Contains(c.Id) && c.Id != ComponentId))
        {
            var copy = component.Clone();
            Change(copy.Actions, after, values);
            changedComponents.Add(copy);
        }
        return (changedMacros, changedComponents);
    }

    private void Change(List<MacroAction> actions, LibraryComponent after, IReadOnlyDictionary<string, JsonNode> values)
    {
        foreach (var action in MacroDefinition.Walk(actions))
        {
            if (action.Type == MacroActionType.Component && action.ComponentId == ComponentId)
                action.Arguments = Changed(action.Arguments, after, values);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                if (condition.Kind == ConditionKind.Component && condition.ComponentId == ComponentId)
                    condition.Arguments = Changed(condition.Arguments, after, values);
        }
    }

    private Dictionary<string, JsonNode?>? Changed(Dictionary<string, JsonNode?>? arguments, LibraryComponent after,
        IReadOnlyDictionary<string, JsonNode> values)
    {
        var result = new Dictionary<string, JsonNode?>();
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonNode?>())
        {
            if (Removed.Contains(name))
                continue;
            result[Renamed.GetValueOrDefault(name) ?? name] = value?.DeepClone();
        }
        foreach (var parameter in NeedValue)
        {
            if (!values.TryGetValue(parameter.Name, out JsonNode? given))
                continue;
            var current = result.GetValueOrDefault(parameter.Name);
            // A reference stays (the checks judge it); a value stays when it is of the parameter's kind as it is now.
            bool keep = current != null && (ComponentValues.Reference(current) != null
                || (!TypeChangedNames.Contains(parameter.Name) && ComponentValues.Read(current, parameter.Type, ValueRule.Any, out _) != null));
            if (!keep)
                result[parameter.Name] = given.DeepClone();
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>The parameters whose type or unit changed: every call's value of the old kind is replaced.</summary>
    public IReadOnlySet<string> TypeChangedNames { get; init; } = new HashSet<string>();

    /// <summary>Every macro and component with actions: who can call.</summary>
    private static IEnumerable<(Guid Id, string Name, bool IsMacro, List<MacroAction> Actions)> Owners(IEnumerable<MacroDefinition> macros,
        IEnumerable<LibraryComponent> components) =>
        macros.Select(m => (m.Id, m.Name, true, m.Actions)).Concat(components.Select(c => (c.Id, c.Name, false, c.Actions)));

    /// <summary>The arguments of every call to the component in these actions: rows and Component conditions.</summary>
    public static IEnumerable<Dictionary<string, JsonNode?>?> CallsTo(IEnumerable<MacroAction> actions, Guid component)
    {
        foreach (var action in MacroDefinition.Walk(actions))
        {
            if (action.Type == MacroActionType.Component && action.ComponentId == component)
                yield return action.Arguments;
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                if (condition.Kind == ConditionKind.Component && condition.ComponentId == component)
                    yield return condition.Arguments;
        }
    }
}
