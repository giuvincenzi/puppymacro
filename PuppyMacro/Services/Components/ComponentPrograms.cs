using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Components;

/// <summary>
/// A component ready to play: a copy of it made once when the engine's snapshot is made, never changed after, shared by
/// every call to it (33 calls are 33 frames of one prepared component, not 33 copies).
/// </summary>
internal sealed class PreparedComponent
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ComponentParameter> Parameters { get; init; }
    public required List<MacroAction> Actions { get; init; }

    /// <summary>The condition it returns (<see cref="LibraryComponent.Returns"/>), inside <see cref="Actions"/>; null when none.</summary>
    public MacroCondition? Returned { get; init; }
}

/// <summary>
/// The values of one call's parameters, by name, as their JSON values (a <c>{"From"}</c> or <c>{"Found"}</c> argument already
/// turned into the value it refers to): what a frame holds.
/// </summary>
internal sealed class FrameArguments
{
    private readonly Dictionary<string, JsonNode> _values;

    public FrameArguments(Dictionary<string, JsonNode> values) => _values = values;

    public static readonly FrameArguments None = new(new Dictionary<string, JsonNode>());

    public int Count => _values.Count;

    public JsonNode? this[string name] => _values.GetValueOrDefault(name);

    public IEnumerable<KeyValuePair<string, JsonNode>> All => _values;

    /// <summary>The Examples of a component's parameters: the values its own Test plays with.</summary>
    public static FrameArguments Examples(IEnumerable<ComponentParameter> parameters) =>
        new(parameters.Where(p => p.Example != null).ToDictionary(p => p.Name, p => p.Example!.DeepClone()));
}

/// <summary>
/// The prepared components of the engine's snapshot (<see cref="Prepare"/>), and what playing a call needs: the values of its
/// arguments in the frame of the caller (<see cref="Evaluate"/>), an action or condition with its bound fields filled from the
/// frame (<see cref="Bound(MacroAction, FrameArguments)"/>), and what a Component condition checks (<see cref="Effective"/>).
/// Nothing here checks: the checks (<see cref="ComponentChecks"/>) ran at save and decide which macros can play.
/// </summary>
internal sealed class ComponentPrograms
{
    private readonly Dictionary<Guid, PreparedComponent> _prepared;

    private ComponentPrograms(Dictionary<Guid, PreparedComponent> prepared) => _prepared = prepared;

    public static readonly ComponentPrograms Empty = new(new Dictionary<Guid, PreparedComponent>());

    public PreparedComponent? Find(Guid id) => _prepared.GetValueOrDefault(id);

    public int Count => _prepared.Count;

    /// <summary>Each component, copied once (the copies are never changed: every call shares them).</summary>
    public static ComponentPrograms Prepare(IEnumerable<LibraryComponent> components)
    {
        var prepared = new Dictionary<Guid, PreparedComponent>();
        foreach (var component in components)
        {
            var actions = component.Actions.ConvertAll(a => a.Clone());
            prepared.TryAdd(component.Id, new PreparedComponent
            {
                Id = component.Id,
                Name = component.Name,
                Parameters = component.Parameters.ConvertAll(p => p.Clone()),
                Actions = actions,
                Returned = component.Returns is { } returns ? ComponentChecks.FindCondition(actions, returns.Found) : null,
            });
        }
        return new ComponentPrograms(prepared);
    }

    /// <summary>
    /// The values a call gives its component's parameters, in the frame of the caller: a value as it is, a <c>{"From"}</c>
    /// the caller's own parameter, a <c>{"Found"}</c> where the caller's condition found something plus the offset; a
    /// parameter left out its Default. Null, with <paramref name="missing"/>, when a <c>{"Found"}</c> condition found nothing
    /// this time (one of an Any's that wasn't true) or its offset moves the place past the screen's limits, or a value is
    /// missing: the call is skipped, as a "Where … was found" click is.
    /// </summary>
    public static FrameArguments? Evaluate(PreparedComponent callee, Dictionary<string, JsonNode?>? arguments, FrameArguments caller,
        IReadOnlyDictionary<Guid, OpenCvSharp.Point> found, out string? missing)
    {
        missing = null;
        var values = new Dictionary<string, JsonNode>();
        foreach (var parameter in callee.Parameters)
        {
            JsonNode? argument = arguments?.GetValueOrDefault(parameter.Name) ?? parameter.Default;
            JsonNode? value = argument;
            if (ComponentValues.Reference(argument) is { } reference)
            {
                value = null;
                if (reference.From is string from)
                    value = caller[from]?.DeepClone();
                else if (reference.Found is Guid id && found.TryGetValue(id, out var point))
                {
                    var at = new JsonObject { ["X"] = (long)point.X + reference.OffsetX, ["Y"] = (long)point.Y + reference.OffsetY };
                    // The offset can move the place past the screen's limits: then there is no point this time, never another one.
                    value = ComponentValues.Read(at, ParameterType.Point, ValueRule.Any, out _) != null ? at : null;
                }
            }
            else
                value = value?.DeepClone();
            if (value == null)
            {
                missing = parameter.Name;
                return null;
            }
            values[parameter.Name] = value;
        }
        return new FrameArguments(values);
    }

    /// <summary>
    /// The action with its bound fields set from the frame; the action itself when nothing of it is bound. Null when its point
    /// is a cell its grid doesn't have or a value its field can't take (the checks refuse those before: it is never played
    /// somewhere else).
    /// </summary>
    public static MacroAction? Bound(MacroAction action, FrameArguments arguments)
    {
        if (action.Bind is not { Count: > 0 } bind || arguments.Count == 0)
            return action;
        MacroAction? copy = null;
        foreach (var (slot, parameter) in ComponentSlots.InOrder(bind))
        {
            if (arguments[parameter] is not { } node || ComponentSlots.Find(action, slot) is not { } s)
                continue;
            if (ComponentSlots.Read(s, node, action.Cell != null) is not { } value)
                return null; // a value its field can't take: never played with the field's own value instead
            copy ??= ShallowForBind(action);
            if (!ComponentSlots.Write(copy, slot, value))
                return null;
        }
        return copy ?? action;
    }

    /// <summary>The condition with its bound fields set from the frame, as <see cref="Bound(MacroAction, FrameArguments)"/>.</summary>
    public static MacroCondition? Bound(MacroCondition condition, FrameArguments arguments)
    {
        if (condition.Bind is not { Count: > 0 } bind || arguments.Count == 0)
            return condition;
        MacroCondition? copy = null;
        foreach (var (slot, parameter) in ComponentSlots.InOrder(bind))
        {
            if (arguments[parameter] is not { } node || ComponentSlots.Find(condition, slot) is not { } s)
                continue;
            if (ComponentSlots.Read(s, node, condition.Cell != null) is not { } value)
                return null; // a value its field can't take: never checked with the field's own value instead
            copy ??= condition.Clone();
            if (!ComponentSlots.Write(copy, slot, value))
                return null;
        }
        return copy ?? condition;
    }

    /// <summary>
    /// The action's own fields copied, its If's settings too (WaitSeconds is bound on the action), but not the actions of
    /// Then and Else: they are bound one by one when they play.
    /// </summary>
    private static MacroAction ShallowForBind(MacroAction action)
    {
        var copy = action.Clone();
        if (action.If is { } block && copy.If != null)
        {
            copy.If.Then = block.Then;
            copy.If.Else = block.Else;
        }
        return copy;
    }

    /// <summary>
    /// What a condition checks in a frame: its bound fields filled; a Component condition becomes the condition its
    /// component returns, with the call's arguments, keeping this condition's Id (so "Where … was found" names it) and its
    /// name, Not turned when this one has Not. Null when the component or a value is missing (it can't be true).
    /// </summary>
    public MacroCondition? Effective(MacroCondition condition, FrameArguments arguments, IReadOnlyDictionary<Guid, OpenCvSharp.Point> found,
        int depth = 0)
    {
        if (condition.Kind != ConditionKind.Component)
            return Bound(condition, arguments);
        if (depth > ComponentSignatures.MaxDepth || condition.ComponentId is not Guid id || Find(id) is not { Returned: { } returned } callee)
            return null;
        if (Evaluate(callee, condition.Arguments, arguments, found, out _) is not { } calleeArguments)
            return null;
        // What the component returns is checked in its own frame, where nothing was found yet.
        var inner = Effective(returned, calleeArguments, new Dictionary<Guid, OpenCvSharp.Point>(), depth + 1);
        if (inner == null)
            return null;
        var effective = ReferenceEquals(inner, returned) ? returned.Clone() : inner;
        effective.Id = condition.Id;
        effective.Name = condition.Name;
        effective.Not = condition.Not ^ inner.Not;
        effective.Bind = null;
        return effective;
    }
}
