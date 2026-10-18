using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Components;

/// <summary>
/// Where actions are checked: a macro's (no parameters) or a component's (its <see cref="Parameters"/>), against the
/// Library's <see cref="Signatures"/> (null skips what needs the Library: whether a component exists, its arguments).
/// </summary>
internal sealed record CallScope(ComponentSignatures? Signatures, IReadOnlyList<ComponentParameter>? Parameters, Func<string, bool>? HasImage)
{
    public static readonly CallScope Macro = new(null, null, null);
}

/// <summary>
/// The checks of components and of the calls to them, in one place: every save (the editors, the Code views, the Library,
/// an import, an AI app) and the engine's snapshot call these. Each problem goes to <c>add(path, message)</c>, with the
/// path in the item's JSON ("Actions[2].Bind.Point").
/// </summary>
internal static class ComponentChecks
{
    /// <summary>The most letters a parameter's name can have.</summary>
    public const int MaxParameterName = 40;

    private static readonly Regex NamePattern = new($"^[A-Za-z][A-Za-z0-9_]{{0,{MaxParameterName - 1}}}$", RegexOptions.CultureInvariant);

    // ================= Bind =================

    /// <summary>An action's Bind: only inside a component, on a slot of its kind that it uses, to a parameter of the same type and unit.</summary>
    public static void CheckBind(MacroAction a, string prefix, CallScope scope, Action<string, string> add) =>
        CheckBind(a.Bind, ComponentSlots.Of(a), a.Cell, prefix, scope, add);

    public static void CheckBind(MacroCondition c, string prefix, CallScope scope, Action<string, string> add) =>
        CheckBind(c.Bind, ComponentSlots.Of(c), c.Cell, prefix, scope, add);

    private static void CheckBind(Dictionary<string, string>? bind, IReadOnlyList<ComponentSlot> slots, int? cell, string prefix,
        CallScope scope, Action<string, string> add)
    {
        CheckCell(bind, slots, cell, prefix, scope, add);
        if (bind == null)
            return;
        foreach (var (name, parameter) in bind)
        {
            string path = $"{prefix}Bind.{name}";
            if (scope.Parameters == null)
            {
                add(path, Strings.Code_BindOnlyComponent);
                continue;
            }
            var slot = slots.FirstOrDefault(s => s.Name == name);
            if (slot == null)
            {
                add(path, slots.Count == 0 ? Strings.Code_BindNoSlotHere
                    : Format(Strings.Code_BindNoSlot, name, string.Join(", ", slots.Where(s => s.Applies).Select(s => s.Name))));
                continue;
            }
            if (!slot.Applies)
            {
                add(path, Format(Strings.Code_BindUnused, name));
                continue;
            }
            var p = scope.Parameters.FirstOrDefault(x => x.Name == parameter);
            if (p == null)
                add(path, Format(Strings.Code_NoParameter, parameter));
            else if (name == ComponentSlots.Point && p.Type == ParameterType.Grid)
            {
                if (cell == null)
                    add(path, Format(Strings.Code_GridNeedsCell, p.Name));
            }
            else if (p.Type != slot.Type || (p.Type == ParameterType.Number && p.Unit != slot.Unit))
                add(path, Format(Strings.Code_ParameterType, p.Name, TypeName(p.Type, p.Unit), name, TypeName(slot.Type, slot.Unit)));
        }
    }

    /// <summary>
    /// A cell (<see cref="MacroAction.Cell"/>): only where the point can be bound, inside a component, with the point bound to
    /// a Grid parameter; from 1 up.
    /// </summary>
    private static void CheckCell(Dictionary<string, string>? bind, IReadOnlyList<ComponentSlot> slots, int? cell, string prefix,
        CallScope scope, Action<string, string> add)
    {
        if (cell is not int n)
            return;
        string path = $"{prefix}Cell";
        bool pointFromGrid = bind?.GetValueOrDefault(ComponentSlots.Point) is string grid
                             && scope.Parameters?.FirstOrDefault(p => p.Name == grid) is { Type: ParameterType.Grid };
        if (!slots.Any(s => s.Name == ComponentSlots.Cell && s.Applies) || !pointFromGrid)
            add(path, Strings.Code_CellWithoutGrid);
        else if (n < 1 || n > GridValue.MaxCells)
            add(path, string.Format(CultureInfo.CurrentCulture, Strings.Code_Between,
                1.ToString(CultureInfo.CurrentCulture), GridValue.MaxCells.ToString(CultureInfo.CurrentCulture)));
    }

    // ================= Calls =================

    /// <summary>
    /// A call to a component, a linked row or a Component condition: the component exists (and, for a condition, says what
    /// it finds), every argument names one of its parameters, every required one has one, and each is a value its parameter
    /// takes, a parameter of the component around it of the same type, or where a condition around it found something.
    /// </summary>
    /// <param name="found">The conditions whose place an argument here can use, by Id (null when only the Id is known).</param>
    /// <param name="notThere">The conditions around it that are true when what they look for is not there.</param>
    public static void CheckCall(Guid? componentId, Dictionary<string, JsonNode?>? arguments, string prefix, bool asCondition,
        CallScope scope, IReadOnlyDictionary<Guid, MacroCondition?> found, IReadOnlySet<Guid>? notThere, Action<string, string> add)
    {
        if (componentId is not Guid id || id == Guid.Empty)
        {
            add($"{prefix}ComponentId", Strings.Code_ChooseComponent);
            return;
        }
        if (scope.Signatures == null)
            return;
        if (scope.Signatures.Find(id) is not { } callee)
        {
            add($"{prefix}ComponentId", Strings.Code_ComponentMissing);
            return;
        }
        if (asCondition && callee.Returns == null)
            add($"{prefix}ComponentId", Format(Strings.Code_ComponentNoReturns, callee.Name));
        arguments ??= new Dictionary<string, JsonNode?>();
        foreach (var parameter in callee.Parameters.Where(p => p.Required && !arguments.ContainsKey(p.Name)))
            add($"{prefix}Arguments", Format(Strings.Code_ArgumentMissing, callee.Name, parameter.Name));
        foreach (var (name, argument) in arguments)
        {
            string path = $"{prefix}Arguments.{name}";
            var parameter = callee.Parameters.FirstOrDefault(p => p.Name == name);
            if (parameter == null)
            {
                add(path, Format(Strings.Code_ArgumentUnknown, callee.Name, name));
                continue;
            }
            CheckArgument(argument, parameter, scope.Signatures.RuleOf(id, name), path, scope, found, notThere, add);
        }
        // From a macro every value is known: each cell the call reaches must be in its grid.
        if (scope.Parameters == null)
            CheckCells(callee, name => Given(arguments.GetValueOrDefault(name) ?? callee.Parameters.FirstOrDefault(p => p.Name == name)?.Default),
                scope.Signatures, msg => add($"{prefix}Arguments", msg));
    }

    /// <summary>A value as it is; null for a {"From"} or {"Found"} (not known here).</summary>
    private static JsonNode? Given(JsonNode? argument) => ComponentValues.Reference(argument) == null ? argument : null;

    /// <summary>
    /// The cells a component reaches with these values of its parameters (<paramref name="valueOf"/>; null: not known) that
    /// their grids don't have: its points from a Grid parameter, and down the calls inside (their {"From"} given the same
    /// values, their parameters left out their Default). Each one said once.
    /// </summary>
    public static void CheckCells(LibraryComponent component, Func<string, JsonNode?> valueOf, ComponentSignatures signatures,
        Action<string> add)
    {
        var said = new HashSet<string>();
        Visit(component, valueOf, 0, new HashSet<Guid>());

        void Visit(LibraryComponent c, Func<string, JsonNode?> values, int depth, HashSet<Guid> visiting)
        {
            if (depth > ComponentSignatures.MaxDepth || !visiting.Add(c.Id))
                return;
            foreach (var action in MacroDefinition.Walk(c.Actions))
            {
                Cell(c, values, action.Bind, action.Cell);
                if (action.Type == MacroActionType.Component)
                    Inner(action.ComponentId, action.Arguments, values, depth, visiting);
                foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                {
                    Cell(c, values, condition.Bind, condition.Cell);
                    if (condition.Kind == ConditionKind.Component)
                        Inner(condition.ComponentId, condition.Arguments, values, depth, visiting);
                }
            }
            visiting.Remove(c.Id);
        }

        void Cell(LibraryComponent c, Func<string, JsonNode?> values, Dictionary<string, string>? bind, int? fixedCell)
        {
            if (fixedCell == null || bind?.GetValueOrDefault(ComponentSlots.Point) is not string gridName
                || ComponentValues.Read(values(gridName), ParameterType.Grid, ValueRule.Any, out _) is not GridValue grid)
                return;
            int? cell = fixedCell;
            if (bind.GetValueOrDefault(ComponentSlots.Cell) is string cellName)
                cell = ComponentValues.Whole(values(cellName), out int given) ? given : null;
            // A number below 1 or above the most cells is refused by the cell's own rule: said once, there.
            if (cell is int n && n >= 1 && n <= GridValue.MaxCells && grid.PointOf(n) == null && said.Add($"{c.Id}|{gridName}|{n}"))
                add(Format(Strings.Code_CellOutside, n, gridName, grid.Cells, c.Name));
        }

        void Inner(Guid? id, Dictionary<string, JsonNode?>? arguments, Func<string, JsonNode?> outer, int depth, HashSet<Guid> visiting)
        {
            if (id is not Guid called || signatures.Find(called) is not { } inner)
                return;
            JsonNode? Value(string name)
            {
                JsonNode? argument = arguments?.GetValueOrDefault(name) ?? inner.Parameters.FirstOrDefault(p => p.Name == name)?.Default;
                return ComponentValues.Reference(argument) is { From: string from } ? outer(from) : Given(argument);
            }
            Visit(inner, Value, depth + 1, visiting);
        }
    }

    private static void CheckArgument(JsonNode? argument, ComponentParameter parameter, ValueRule rule, string path, CallScope scope,
        IReadOnlyDictionary<Guid, MacroCondition?> found, IReadOnlySet<Guid>? notThere, Action<string, string> add)
    {
        if (ComponentValues.Reference(argument) is not { } reference)
        {
            ComponentValues.Read(argument, parameter.Type, rule, out string? problem, scope.HasImage);
            if (problem != null)
                add(path, problem);
            return;
        }
        if (!reference.WellFormed)
        {
            add(path, Strings.Code_ArgumentReference);
            return;
        }
        if (reference.From is string from)
        {
            var own = scope.Parameters?.FirstOrDefault(p => p.Name == from);
            if (scope.Parameters == null)
                add(path, Strings.Code_FromOutside);
            else if (own == null)
                add(path, Format(Strings.Code_NoParameter, from));
            else if (own.Type != parameter.Type || (own.Type == ParameterType.Number && own.Unit != parameter.Unit))
                add(path, Format(Strings.Code_ParameterType, own.Name, TypeName(own.Type, own.Unit), parameter.Name, TypeName(parameter.Type, parameter.Unit)));
            return;
        }
        Guid condition = reference.Found!.Value;
        if (parameter.Type != ParameterType.Point)
            add(path, Format(Strings.Code_FoundNotPoint, parameter.Name, TypeName(parameter.Type, parameter.Unit)));
        else if (notThere?.Contains(condition) == true)
            add(path, Strings.Code_FoundNot);
        else if (!found.TryGetValue(condition, out MacroCondition? at))
            add(path, Strings.Code_FoundOutside);
        else if (at != null && PlaceProblem(at, scope.Signatures) is string noPlace)
            add(path, noPlace);
        else
        {
            if (reference.OffsetX < MacroJson.Coordinate.Min || reference.OffsetX > MacroJson.Coordinate.Max
                || reference.OffsetY < MacroJson.Coordinate.Min || reference.OffsetY > MacroJson.Coordinate.Max)
                add(path, string.Format(CultureInfo.CurrentCulture, Strings.Code_Between,
                    MacroJson.Coordinate.Min.ToString("0", CultureInfo.CurrentCulture), MacroJson.Coordinate.Max.ToString("0", CultureInfo.CurrentCulture)));
        }
    }

    /// <summary>
    /// A "Where … was found" (or a Found argument) that names a Component condition gets a place only when what that
    /// component finds is an image or a text, there: null when it does, else why not.
    /// </summary>
    public static string? PlaceProblem(MacroCondition condition, ComponentSignatures? signatures)
    {
        if (condition.Kind != ConditionKind.Component || signatures == null || condition.ComponentId is not Guid id)
            return null;
        return ReturnedCondition(signatures, id, condition.Not) is { GivesPlace: true } ? null
            : Format(Strings.Code_FoundNoPlace, condition.Name ?? "");
    }

    /// <summary>
    /// Whether a condition's own Not makes it true when what it looks for is not there (an action in its Then can't go where
    /// it was found). A Component condition's Not is turned again by the Nots of what its component returns
    /// (<see cref="ReturnedCondition"/>), as it plays: with both, what it returns is there, and found. Its own Not when what
    /// it returns isn't known.
    /// </summary>
    public static bool NotThere(MacroCondition condition, ComponentSignatures? signatures)
    {
        if (!condition.Not)
            return false;
        return condition.Kind != ConditionKind.Component || signatures == null || condition.ComponentId is not Guid id
               || ReturnedCondition(signatures, id, condition.Not) is not { } returned || returned.Not;
    }

    /// <summary>
    /// Whether an action in an If's Then can name this condition in a "Where … was found" (<see cref="MacroCondition.GivesPlace"/>,
    /// with a Component condition's Nots counted as in <see cref="NotThere"/>); <see cref="PlaceProblem"/> then says whether
    /// what a Component condition returns gives a place.
    /// </summary>
    public static bool GivesPlace(MacroCondition condition, ComponentSignatures? signatures) =>
        condition.Finds && !NotThere(condition, signatures);

    /// <summary>
    /// What a Component condition checks in the end: the returned condition of the component (through Component conditions
    /// it returns in turn), with Not turned once for each Not on the way. Null when one is missing or the calls loop.
    /// </summary>
    public static MacroCondition? ReturnedCondition(ComponentSignatures signatures, Guid componentId, bool not)
    {
        var seen = new HashSet<Guid>();
        Guid id = componentId;
        while (seen.Add(id) && signatures.Find(id) is { Returns: { } returns } component)
        {
            var condition = FindCondition(component.Actions, returns.Found);
            if (condition == null)
                return null;
            not ^= condition.Not;
            if (condition.Kind != ConditionKind.Component)
            {
                var copy = condition.Clone();
                copy.Not = not;
                return copy;
            }
            if (condition.ComponentId is not Guid next)
                return null;
            id = next;
        }
        return null;
    }

    public static MacroCondition? FindCondition(IEnumerable<MacroAction> actions, Guid id) =>
        MacroDefinition.Walk(actions).SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).FirstOrDefault(c => c.Id == id);

    // ================= A component's own =================

    /// <summary>
    /// What a component is besides its actions: its parameters (names, types, units, an Example and a Default each its type
    /// and inside the rule of the fields it goes to), what it returns, and its place among the others (no loop of calls, at
    /// most <see cref="ComponentSignatures.MaxDepth"/> levels). <paramref name="signatures"/> holds the Library with this
    /// component as it would be saved.
    /// </summary>
    public static void CheckSignature(LibraryComponent component, ComponentSignatures signatures, Func<string, bool>? hasImage,
        Action<string, string> add)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < component.Parameters.Count; i++)
        {
            var p = component.Parameters[i];
            string prefix = $"Parameters[{i}].";
            if (!NamePattern.IsMatch(p.Name))
                add($"{prefix}Name", Strings.Code_ParameterName);
            else if (!names.Add(p.Name))
                add($"{prefix}Name", Strings.Code_ParameterNameTwice);
            if (p.Type == ParameterType.Number && p.Unit == null)
                add($"{prefix}Unit", Strings.Code_ParameterUnit);
            else if (p.Type != ParameterType.Number && p.Unit != null)
                add($"{prefix}Unit", Strings.Code_ParameterNoUnit);
            ValueRule rule = signatures.RuleOf(component.Id, p.Name);
            if (p.Example == null)
                add($"{prefix}Example", Strings.Code_ParameterExample);
            else if (ComponentValues.Reference(p.Example) != null)
                add($"{prefix}Example", Strings.Code_ParameterValueOnly);
            else
            {
                ComponentValues.Read(p.Example, p.Type, rule, out string? problem, hasImage);
                if (problem != null)
                    add($"{prefix}Example", problem);
            }
            if (p.Default != null)
            {
                if (ComponentValues.Reference(p.Default) != null)
                    add($"{prefix}Default", Strings.Code_ParameterValueOnly);
                else
                {
                    ComponentValues.Read(p.Default, p.Type, rule, out string? problem, hasImage);
                    if (problem != null)
                        add($"{prefix}Default", problem);
                }
            }
        }
        if (component.Returns is { } returns)
        {
            var condition = FindCondition(component.Actions, returns.Found);
            if (condition == null)
                add("Returns.Found", Strings.Code_ReturnsMissing);
            else if (condition.Kind == ConditionKind.Component
                     && (condition.Arguments ?? new Dictionary<string, JsonNode?>()).Values.Any(v => ComponentValues.Reference(v)?.Found != null))
                add("Returns.Found", Strings.Code_ReturnsFoundOutside);
        }
        // Its own Test plays the Examples: each cell they reach must be in its grid.
        CheckCells(component, name => component.Parameters.FirstOrDefault(p => p.Name == name)?.Example, signatures,
            msg => add("Actions", msg));
        if (signatures.CycleOf(component.Id) is { } cycle)
            add("Actions", Format(Strings.Code_ComponentCycle,
                string.Join(" → ", cycle.Select(id => signatures.Find(id)?.Name ?? id.ToString()))));
        else if (signatures.Height(component.Id) is int height && height > ComponentSignatures.MaxDepth)
            add("Actions", Format(Strings.Code_ComponentTooDeep, ComponentSignatures.MaxDepth, height));
    }

    // ================= Everything at once =================

    /// <summary>One problem of the Library or of a macro's calls: where (the component's or macro's name), the path, why.</summary>
    internal sealed record Problem(Guid ItemId, string Where, string Path, string Message);

    /// <summary>
    /// Every component and every macro's calls, checked against the Library as it is (or would be): what the Library window,
    /// an import and an AI app's save refuse when it is new, and what makes a macro unplayable. The checks of the rest of a
    /// macro are its editor's and are not repeated.
    /// </summary>
    public static List<Problem> CheckAll(ComponentSignatures signatures, IEnumerable<MacroDefinition> macros, Func<string, bool>? hasImage)
    {
        var problems = new List<Problem>();
        foreach (var component in signatures.All)
        {
            void Add(string path, string message) => problems.Add(new Problem(component.Id, component.Name, path, message));
            CheckSignature(component, signatures, hasImage, Add);
            CheckCalls(component.Actions, "Actions", new CallScope(signatures, component.Parameters, hasImage), Add);
        }
        foreach (var macro in macros)
            CheckCalls(macro.Actions, "Actions", new CallScope(signatures, null, hasImage),
                (path, message) => problems.Add(new Problem(macro.Id, macro.Name, path, message)));
        return problems;
    }

    /// <summary>
    /// The macros that can't play: a call of theirs fails the checks (a missing component, a missing or wrong argument), or
    /// calls a component that fails them or calls one that does. Their hotkeys pass to the app; Play does nothing.
    /// </summary>
    public static HashSet<Guid> Unplayable(ComponentSignatures signatures, IReadOnlyCollection<MacroDefinition> macros)
    {
        var problems = CheckAll(signatures, macros, hasImage: null);
        var bad = problems.Select(p => p.ItemId).ToHashSet();
        var badComponents = signatures.All.Select(c => c.Id).Where(bad.Contains).ToHashSet();
        foreach (Guid id in badComponents.ToList())
            badComponents.UnionWith(signatures.CallersOf(id));
        return macros
            .Where(m => bad.Contains(m.Id) || ComponentSignatures.Callees(m.Actions).Any(badComponents.Contains))
            .Select(m => m.Id)
            .ToHashSet();
    }

    /// <summary>The calls and binds of some actions (inside Then and Else too), with the places each one can use.</summary>
    public static void CheckCalls(List<MacroAction> actions, string path, CallScope scope, Action<string, string> add) =>
        Calls(actions, path, scope, new Dictionary<Guid, MacroCondition?>(), new HashSet<Guid>(), add);

    private static void Calls(List<MacroAction> list, string path, CallScope scope, Dictionary<Guid, MacroCondition?> found,
        HashSet<Guid> notThere, Action<string, string> add)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            string prefix = $"{path}[{i}].";
            CheckBind(a, prefix, scope, add);
            if (a.Type == MacroActionType.Component)
                CheckCall(a.ComponentId, a.Arguments, prefix, false, scope, found, notThere, add);
            if (a.FoundId is Guid foundId && found.GetValueOrDefault(foundId) is { } place && PlaceProblem(place, scope.Signatures) is string noPlace)
                add($"{prefix}FoundId", noPlace);
            if (a.If is not { } block)
                continue;
            for (int c = 0; c < block.Conditions.Count; c++)
            {
                var condition = block.Conditions[c];
                string at = $"{prefix}If.Conditions[{c}].";
                CheckBind(condition, at, scope, add);
                if (condition.Kind == ConditionKind.Component)
                    CheckCall(condition.ComponentId, condition.Arguments, at, true, scope, found, notThere, add);
            }
            var inside = new Dictionary<Guid, MacroCondition?>(found);
            foreach (var condition in block.Conditions.Where(c => GivesPlace(c, scope.Signatures)))
                inside[condition.Id] = condition;
            var notInside = new HashSet<Guid>(notThere);
            notInside.UnionWith(block.Conditions.Where(c => NotThere(c, scope.Signatures)).Select(c => c.Id));
            Calls(block.Then, $"{prefix}If.Then", scope, inside, notInside, add);
            Calls(block.Else, $"{prefix}If.Else", scope, found, notThere, add);
        }
    }

    // ================= Words =================

    /// <summary>A parameter's type as people read it: "Point", "Number (ms)".</summary>
    public static string TypeName(ParameterType type, NumberUnit? unit)
    {
        string name = type switch
        {
            ParameterType.Point => Strings.Param_TypePoint,
            ParameterType.Number => Strings.Param_TypeNumber,
            ParameterType.Text => Strings.Param_TypeText,
            ParameterType.Key => Strings.Param_TypeKey,
            ParameterType.Color => Strings.Param_TypeColor,
            ParameterType.Image => Strings.Param_TypeImage,
            ParameterType.Grid => Strings.Param_TypeGrid,
            _ => Strings.Param_TypeArea,
        };
        return type == ParameterType.Number && unit is NumberUnit u ? Format(Strings.Param_TypeWithUnit, name, UnitName(u)) : name;
    }

    public static string UnitName(NumberUnit unit) => unit switch
    {
        NumberUnit.Pixels => Strings.Param_UnitPixels,
        NumberUnit.Ms => Strings.Action_UnitMs,
        NumberUnit.Seconds => Strings.Action_UnitS,
        NumberUnit.Count => Strings.Param_UnitCount,
        _ => "%",
    };

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
