using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Components;

/// <summary>
/// The Library's components as calls see them, computed once for a check: each component by id, the rule of each of its
/// parameters (its type's and unit's, joined with the rules of the fields bound to it and of the parameters it passes it on
/// to, down the calls: <see cref="RuleOf"/>), which components call which, the loops of calls and how deep each component's
/// calls go (<see cref="Height"/>). Nothing here changes a component.
/// </summary>
internal sealed class ComponentSignatures
{
    /// <summary>How many components can be inside each other: a macro, then at most this many levels of components.</summary>
    public const int MaxDepth = 10;

    private readonly Dictionary<Guid, LibraryComponent> _components = new();
    private readonly Dictionary<Guid, Dictionary<string, ValueRule>> _rules = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _calls = new();
    private readonly Dictionary<Guid, int> _height = new();
    private readonly Dictionary<Guid, List<Guid>> _cycle = new();

    public static readonly ComponentSignatures Empty = new(Array.Empty<LibraryComponent>());

    public ComponentSignatures(IEnumerable<LibraryComponent> components)
    {
        foreach (var component in components)
            _components.TryAdd(component.Id, component);
        foreach (var component in _components.Values)
            _calls[component.Id] = Callees(component.Actions).ToHashSet();
        FindCycles();
        foreach (var id in _components.Keys)
            Rules(id, new HashSet<Guid>());
    }

    /// <summary>The Library's components, with <paramref name="changed"/> in place of the one with its id (or added).</summary>
    public static ComponentSignatures Of(ComponentLibrary library, LibraryComponent? changed = null) =>
        new(changed == null ? library.AllComponents : library.AllComponents.Where(c => c.Id != changed.Id).Append(changed));

    public LibraryComponent? Find(Guid id) => _components.GetValueOrDefault(id);

    public IEnumerable<LibraryComponent> All => _components.Values;

    /// <summary>The rule a value of this parameter keeps; the type's alone when the component or parameter is unknown.</summary>
    public ValueRule RuleOf(Guid component, string parameter) =>
        _rules.TryGetValue(component, out var rules) && rules.TryGetValue(parameter, out var rule) ? rule : ValueRule.Any;

    private readonly Dictionary<Guid, bool> _usesCells = new();

    /// <summary>
    /// Whether a call to it can reach a cell of a grid: one of its actions or conditions has a Cell, or a component it calls
    /// does. Only then are a call's cells checked (<see cref="ComponentChecks.CheckCells"/>).
    /// </summary>
    public bool UsesCells(Guid component)
    {
        if (_usesCells.TryGetValue(component, out bool known))
            return known;
        _usesCells[component] = false; // a loop counts once (a loop is refused anyway)
        bool uses = _components.TryGetValue(component, out var c)
                    && (MacroDefinition.Walk(c.Actions).Any(a => a.Cell != null || (a.If?.Conditions.Any(x => x.Cell != null) ?? false))
                        || CallsOf(component).Any(UsesCells));
        _usesCells[component] = uses;
        return uses;
    }

    /// <summary>The components a component calls, its linked rows and its Component conditions, directly.</summary>
    public IReadOnlySet<Guid> CallsOf(Guid component) => _calls.GetValueOrDefault(component) ?? new HashSet<Guid>();

    /// <summary>
    /// How many levels of components a call to it plays: 1 when it calls none, else one more than the deepest it calls.
    /// Counted along the calls that are not part of a loop.
    /// </summary>
    public int Height(Guid component)
    {
        if (_height.TryGetValue(component, out int known))
            return known;
        _height[component] = 1; // a loop counts once (a loop is refused anyway)
        int height = 1 + CallsOf(component).Where(_components.ContainsKey).Select(Height).DefaultIfEmpty(0).Max();
        _height[component] = height;
        return height;
    }

    /// <summary>The components of a loop of calls this one is in, from it back to it (A, B, A), or null.</summary>
    public IReadOnlyList<Guid>? CycleOf(Guid component)
    {
        if (!_cycle.TryGetValue(component, out var loop))
            return null;
        // The loop as found (B, A, B), turned to start from this component (A, B, A).
        var members = loop.Take(loop.Count - 1).ToList();
        int start = members.IndexOf(component);
        if (start < 0)
            return loop;
        var turned = members.Skip(start).Concat(members.Take(start)).ToList();
        turned.Add(component);
        return turned;
    }

    /// <summary>Every component that calls <paramref name="component"/>, directly or through others.</summary>
    public HashSet<Guid> CallersOf(Guid component)
    {
        var callers = new HashSet<Guid>();
        var queue = new Queue<Guid>(new[] { component });
        while (queue.Count > 0)
        {
            Guid current = queue.Dequeue();
            foreach (var (id, calls) in _calls)
                if (calls.Contains(current) && callers.Add(id))
                    queue.Enqueue(id);
        }
        return callers;
    }

    /// <summary>The components these actions call: linked rows and Component conditions, inside Then and Else too.</summary>
    public static IEnumerable<Guid> Callees(IEnumerable<MacroAction> actions)
    {
        foreach (var action in MacroDefinition.Walk(actions))
        {
            if (action.Type == MacroActionType.Component && action.ComponentId is Guid row)
                yield return row;
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                if (condition.Kind == ConditionKind.Component && condition.ComponentId is Guid called)
                    yield return called;
        }
    }

    private void FindCycles()
    {
        // Depth-first, keeping the path: a call back into the path closes a loop.
        var state = new Dictionary<Guid, int>(); // 1 on the path, 2 done
        var path = new List<Guid>();
        void Visit(Guid id)
        {
            state[id] = 1;
            path.Add(id);
            foreach (Guid next in CallsOf(id).Where(_components.ContainsKey))
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    var loop = path.Skip(path.IndexOf(next)).Append(next).ToList();
                    foreach (Guid member in loop)
                        _cycle.TryAdd(member, loop);
                }
                else if (state.GetValueOrDefault(next) == 0)
                    Visit(next);
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
        foreach (Guid id in _components.Keys)
            if (state.GetValueOrDefault(id) == 0)
                Visit(id);
    }

    /// <summary>
    /// The rules of a component's parameters: each starts from its type and unit, and takes the rule of each field bound to
    /// it and of each parameter of a called component it is passed on to ({"From": it}), computed first (the calls have no
    /// loop; along a loop the called component's rules are left out).
    /// </summary>
    private Dictionary<string, ValueRule> Rules(Guid id, HashSet<Guid> visiting)
    {
        if (_rules.TryGetValue(id, out var known))
            return known;
        var component = _components[id];
        var rules = component.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => ComponentValues.BaseRule(g.First().Type, g.First().Unit), StringComparer.Ordinal);
        visiting.Add(id);
        void Take(string? parameter, ValueRule rule)
        {
            if (parameter != null && rules.TryGetValue(parameter, out var current))
                rules[parameter] = current.And(rule);
        }
        void Calls(Guid? called, Dictionary<string, System.Text.Json.Nodes.JsonNode?>? arguments)
        {
            if (called is not Guid callee || !_components.ContainsKey(callee) || visiting.Contains(callee) || arguments == null)
                return;
            var calleeRules = Rules(callee, visiting);
            foreach (var (name, argument) in arguments)
                if (ComponentValues.Reference(argument) is { From: string from } && calleeRules.TryGetValue(name, out var rule))
                    Take(from, rule);
        }
        foreach (var action in MacroDefinition.Walk(component.Actions))
        {
            foreach (var (slot, parameter) in action.Bind ?? new Dictionary<string, string>())
                if (ComponentSlots.Find(action, slot) is { } s)
                    Take(parameter, s.Rule);
            if (action.Type == MacroActionType.Component)
                Calls(action.ComponentId, action.Arguments);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                foreach (var (slot, parameter) in condition.Bind ?? new Dictionary<string, string>())
                    if (ComponentSlots.Find(condition, slot) is { } s)
                        Take(parameter, s.Rule);
                if (condition.Kind == ConditionKind.Component)
                    Calls(condition.ComponentId, condition.Arguments);
            }
        }
        visiting.Remove(id);
        _rules[id] = rules;
        return rules;
    }
}
