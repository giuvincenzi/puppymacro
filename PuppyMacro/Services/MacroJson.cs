using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Services;

/// <summary>
/// The macro file's JSON (<c>macros\{id}.json</c>): the text <see cref="MacroLibrary"/> saves, and the
/// Code views' strict reading of a whole macro or of one of its actions (<see cref="CodeJson"/>).
/// </summary>
internal static class MacroJson
{
    /// <summary>The options of the macro files.</summary>
    public static JsonSerializerOptions Options => CodeJson.Options;

    // Limits of the editor's fields (MacroEditorWindow, MacroActionWindow), also used by CodeSchema.
    public const double MaxDelayMs = CodeJson.MaxDelayMs;
    public static readonly (double Min, double Max) HoldMs = (1, 60_000);
    public static readonly (double Min, double Max) Repeat = (1, 100_000);
    public static readonly (double Min, double Max) RepeatPauseMs = (0, MaxDelayMs);
    public static readonly (double Min, double Max) Coordinate = (-100_000, 100_000);
    public static readonly (double Min, double Max) SmoothSpeed = (1, 5);
    public static readonly (double Min, double Max) ScrollSteps = (1, 1000);
    public static readonly (double Min, double Max) RepeatCount = (1, 100_000);
    public static readonly (double Min, double Max) Tolerance = (0, 100);
    public static readonly (double Min, double Max) Similarity = (50, 100);
    public static readonly (double Min, double Max) WaitSeconds = (0.1, 86_400);
    public static readonly (double Min, double Max) AreaSize = (8, 100_000);

    /// <summary>The Color of a Pixel condition: "#" and six hexadecimal digits.</summary>
    public static bool IsColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);

    /// <summary>An image of an Image condition: the SHA-256 of the PNG file, 64 lowercase hexadecimal digits.</summary>
    public static bool IsImageId(string? image) =>
        image is { Length: 64 } && image.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Serialize(MacroDefinition macro) => CodeJson.Serialize(macro);

    public static string Serialize(MacroAction action) => CodeJson.Serialize(action);

    /// <summary>Checks a whole macro's text and reads it. Null when there are problems.</summary>
    /// <param name="conflict">The hotkey conflict message for a binding and the app it works in (null: all apps), or null (see <see cref="HotkeyConflicts"/>).</param>
    /// <param name="hasImage">Whether PuppyMacro has an image (<see cref="ImageStore.Has"/>); null skips that check.</param>
    /// <param name="components">The Library's components, for the calls to them; null skips what needs the Library.</param>
    public static MacroDefinition? Parse(string text, Guid id, Func<HotkeyBinding, string?, string?> conflict, out List<CodeProblem> problems,
        Func<string, bool>? hasImage = null, ComponentSignatures? components = null)
    {
        var scope = new CallScope(components, null, hasImage);
        MacroDefinition? macro = CodeJson.Read<MacroDefinition>(text, (m, check) => CheckMacro(m, id, conflict, scope, check), out problems);
        if (macro == null)
            return null;
        macro.Name = macro.Name.Trim();
        macro.AppExe = AppScope.Normalize(macro.AppExe);
        foreach (var group in macro.Groups)
            group.Name = group.Name.Trim();
        foreach (var action in macro.AllActions)
            TrimNames(action);
        return macro;
    }

    /// <summary>
    /// Checks one action's text (the action window's Code view) and reads it. Its GroupId must be
    /// one of <paramref name="allowedGroups"/> (see <see cref="AllowedGroupIds"/>).
    /// </summary>
    /// <param name="found">The conditions whose image or text an action there can use (<see cref="FoundAt"/>), by Id with their names.</param>
    /// <param name="parameters">Inside a component: its parameters, which the action's Bind and calls can use; null in a macro.</param>
    public static MacroAction? ParseAction(string text, IReadOnlyList<Guid?> allowedGroups, IReadOnlyDictionary<Guid, string> groupNames,
        out List<CodeProblem> problems, IReadOnlyDictionary<Guid, string>? found = null, Func<string, bool>? hasImage = null,
        ComponentSignatures? components = null, IReadOnlyList<ComponentParameter>? parameters = null)
    {
        var scope = new CallScope(components, parameters, hasImage);
        MacroAction? action = CodeJson.Read<MacroAction>(text, (a, check) =>
        {
            var finders = (found ?? new Dictionary<Guid, string>()).ToDictionary(f => f.Key, _ => (MacroCondition?)null);
            CheckActions(new List<MacroAction> { a }, "", finders, scope, check, single: true);
            CheckNames(new[] { a }, check, "");
            if (!allowedGroups.Contains(a.GroupId))
            {
                string allowed = string.Join(", ", allowedGroups.Select(g => g is Guid id && groupNames.TryGetValue(id, out string? name)
                    ? $"\"{id}\" ({name})" : "null"));
                Add(check, "GroupId", string.Format(CultureInfo.CurrentCulture, Strings.Code_GroupNotAllowed, allowed));
            }
        }, out problems);
        if (action != null)
            foreach (var each in MacroDefinition.Walk(new[] { action }))
                TrimNames(each);
        return action;
    }

    /// <summary>
    /// Checks a Library component's text (its Code view, an AI app's save, a file) and reads it: its names, its parameters,
    /// what it returns, its actions as a macro's are checked (their Bind and calls with its parameters) and its place among
    /// the Library's components (<paramref name="library"/>: without it, or with an older version of it). Null when there
    /// are problems.
    /// </summary>
    public static LibraryComponent? ParseComponent(string text, Guid id, out List<CodeProblem> problems, Func<string, bool>? hasImage,
        IEnumerable<LibraryComponent> library)
    {
        LibraryComponent? component = CodeJson.Read<LibraryComponent>(text, (c, check) =>
        {
            check.SameId(c.Id, id);
            if (string.IsNullOrWhiteSpace(c.Name))
                check.Add("Name", Strings.Library_NameMissing);
            if (!ComponentLibrary.IsConcept(c.Concept))
                check.Add("Concept", Strings.Library_ConceptInvalid);
            if (c.Actions.Count == 0)
                check.Add("Actions", Strings.Library_NoActions);
            var signatures = new ComponentSignatures(library.Where(k => k.Id != c.Id).Append(c));
            var scope = new CallScope(signatures, c.Parameters, hasImage);
            ComponentChecks.CheckSignature(c, signatures, hasImage, check.Add);
            CheckNoGroups(c.Actions, "Actions", check);
            CheckActions(c.Actions, "Actions", new Dictionary<Guid, MacroCondition?>(), scope, check, single: false);
            CheckNames(c.Actions, check, "Actions");
        }, out problems);
        if (component == null)
            return null;
        component.Name = component.Name.Trim();
        foreach (var action in MacroDefinition.Walk(component.Actions))
            TrimNames(action);
        return component;
    }

    /// <summary>A component has no groups: its actions belong to none.</summary>
    private static void CheckNoGroups(List<MacroAction> list, string path, CodeJson.Checker check)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].GroupId != null)
                check.Add($"{path}[{i}].GroupId", Strings.Code_NoSuchGroup);
            foreach (var (branch, name) in new[] { (list[i].If?.Then, "Then"), (list[i].If?.Else, "Else") })
                if (branch != null)
                    CheckNoGroups(branch, $"{path}[{i}].If.{name}", check);
        }
    }

    private static void TrimNames(MacroAction action)
    {
        action.Name = action.Name.Trim();
        foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            condition.Name = condition.Name?.Trim();
    }

    /// <summary>
    /// The conditions whose image or text an action of <paramref name="list"/> can use: those of every If whose Then holds
    /// it, by Id with their names.
    /// </summary>
    public static Dictionary<Guid, string> FoundAt(List<MacroAction> root, List<MacroAction> list)
    {
        var found = new Dictionary<Guid, string>();
        Holders(root, list, new List<MacroIf>(), found);
        return found;
    }

    private static bool Holders(List<MacroAction> current, List<MacroAction> target, List<MacroIf> path, Dictionary<Guid, string> found)
    {
        if (ReferenceEquals(current, target))
        {
            foreach (var block in path)
                foreach (var condition in block.Conditions.Where(c => c.GivesPlace))
                    found[condition.Id] = condition.Name ?? "";
            return true;
        }
        foreach (var action in current)
        {
            if (action.If is not { } block)
                continue;
            path.Add(block);
            bool hit = Holders(block.Then, target, path, found);
            path.RemoveAt(path.Count - 1);
            if (hit || Holders(block.Else, target, path, found))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The groups an action at <paramref name="index"/> can be in, so that every group's actions stay
    /// next to each other: none (null), the group of the action before it or of the action after it.
    /// With <paramref name="replacing"/> the action is already at that index; otherwise it is inserted there.
    /// </summary>
    public static List<Guid?> AllowedGroupIds(IReadOnlyList<MacroAction> actions, int index, bool replacing)
    {
        Guid? before = index > 0 ? actions[index - 1].GroupId : null;
        int afterIndex = replacing ? index + 1 : index;
        Guid? after = afterIndex < actions.Count ? actions[afterIndex].GroupId : null;
        var candidates = new List<Guid?> { null, before, after };
        if (replacing && index < actions.Count)
            candidates.Add(actions[index].GroupId);

        var allowed = new List<Guid?>();
        foreach (Guid? candidate in candidates.Distinct())
        {
            var groups = actions.Select(a => a.GroupId).ToList();
            if (replacing && index < groups.Count)
                groups[index] = candidate;
            else
                groups.Insert(Math.Clamp(index, 0, groups.Count), candidate);
            if (KeepsGroupsTogether(groups))
                allowed.Add(candidate);
        }
        return allowed;
    }

    /// <summary>Every group's actions are next to each other.</summary>
    public static bool KeepsGroupsTogether(IReadOnlyList<Guid?> groups)
    {
        var finished = new HashSet<Guid>();
        Guid? previous = null;
        foreach (Guid? group in groups)
        {
            if (previous is Guid ended && ended != group)
                finished.Add(ended);
            if (group is Guid id && finished.Contains(id))
                return false;
            previous = group;
        }
        return true;
    }

    private static void Add(CodeJson.Checker check, string path, string message) => check.Add(path, message);

    private static void CheckMacro(MacroDefinition m, Guid id, Func<HotkeyBinding, string?, string?> conflict, CallScope scope,
        CodeJson.Checker check)
    {
        check.SameId(m.Id, id);
        if (string.IsNullOrWhiteSpace(m.Name))
            check.Add("Name", Strings.Code_EnterName);
        if (m.Actions.Count == 0)
            check.Add("Actions", Strings.Code_AddAction);
        check.Range("RepeatCount", m.RepeatCount, RepeatCount);
        if (!MacroDefinition.ChecksPerSecondSteps.Contains(m.ChecksPerSecond))
            check.Add("ChecksPerSecond", Strings.Code_ChecksPerSecond);
        if (!MacroDefinition.SpeedSteps.Contains(m.Speed))
            check.Add("Speed", Strings.Code_SpeedSteps);
        check.KnownSound(m.SoundName);
        check.AppExe(m.AppExe);
        check.Hotkey(m.Hotkey, m.Mode, binding => conflict(binding, AppScope.Normalize(m.AppExe)));
        check.FloatingButton(m.FloatingButton, m.Mode);
        CheckGroups(m, check);
        CheckActions(m.Actions, "Actions", new Dictionary<Guid, MacroCondition?>(), scope, check, single: false);
        CheckNames(m.Actions, check, "Actions");
    }

    /// <summary>
    /// Checks every action of <paramref name="list"/> (at <paramref name="path"/>, "Actions" or "Actions[2].If.Then"),
    /// also inside Then and Else. <paramref name="found"/>: the conditions an action here can use where they found something
    /// (null when only the Id is known); <paramref name="notThere"/>: the conditions around it that are true when what they
    /// look for is not there. <paramref name="scope"/>: a macro's or a component's, for Bind and the calls.
    /// </summary>
    private static void CheckActions(List<MacroAction> list, string path, Dictionary<Guid, MacroCondition?> found, CallScope scope,
        CodeJson.Checker check, bool single, HashSet<Guid>? notThere = null)
    {
        for (int i = 0; i < list.Count; i++)
        {
            string prefix = single ? "" : $"{path}[{i}].";
            var a = list[i];
            CheckAction(a, prefix, check);
            CheckLink(a, prefix, scope, found, notThere, check);
            ComponentChecks.CheckBind(a, prefix, scope, check.Add);
            if (a.FoundId is Guid foundId)
            {
                if (a.Type is not (MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo))
                    check.Add($"{prefix}FoundId", Strings.Code_FoundOnlyMouse);
                else if (notThere?.Contains(foundId) == true)
                    check.Add($"{prefix}FoundId", Strings.Code_FoundNot);
                else if (!found.TryGetValue(foundId, out MacroCondition? place))
                    check.Add($"{prefix}FoundId", Strings.Code_FoundOutside);
                else if (place != null && ComponentChecks.PlaceProblem(place, scope.Signatures) is string noPlace)
                    check.Add($"{prefix}FoundId", noPlace);
                check.Range($"{prefix}FoundOffsetX", a.FoundOffsetX, Coordinate);
                check.Range($"{prefix}FoundOffsetY", a.FoundOffsetY, Coordinate);
            }
            if (a.Type != MacroActionType.If)
            {
                if (a.If != null)
                    check.Add($"{prefix}If", Strings.Code_OnlyIfHasConditions);
                continue;
            }
            if (a.If is not { } block)
            {
                check.Add($"{prefix}Type", Strings.Code_IfNeedsConditions);
                continue;
            }
            if (block.Conditions.Count == 0)
                check.Add($"{prefix}If.Conditions", Strings.Code_AddCondition);
            for (int c = 0; c < block.Conditions.Count; c++)
            {
                var condition = block.Conditions[c];
                string at = $"{prefix}If.Conditions[{c}].";
                CheckCondition(condition, at, scope.HasImage, check);
                ComponentChecks.CheckBind(condition, at, scope, check.Add);
                if (condition.Kind == ConditionKind.Component)
                    ComponentChecks.CheckCall(condition.ComponentId, condition.Arguments, at, asCondition: true, scope, found, notThere, check.Add);
                else
                {
                    if (condition.ComponentId != null)
                        check.Add($"{at}ComponentId", Strings.Code_ComponentIdOnlyLinks);
                    if (condition.Arguments != null)
                        check.Add($"{at}Arguments", Strings.Code_ArgumentsOnlyCalls);
                }
            }
            if (block.Wait == ConditionWait.UpTo)
                check.Range($"{prefix}If.WaitSeconds", block.WaitSeconds, WaitSeconds);
            var inside = new Dictionary<Guid, MacroCondition?>(found);
            foreach (var condition in block.Conditions.Where(c => c.GivesPlace))
                inside[condition.Id] = condition;
            var notInside = new HashSet<Guid>(notThere ?? new HashSet<Guid>());
            notInside.UnionWith(block.Conditions.Where(c => c.Not).Select(c => c.Id));
            CheckActions(block.Then, $"{prefix}If.Then", inside, scope, check, single: false, notInside);
            CheckActions(block.Else, $"{prefix}If.Else", found, scope, check, single: false, notThere);
        }
    }

    private static void CheckCondition(MacroCondition c, string prefix, Func<string, bool>? hasImage, CodeJson.Checker check)
    {
        if (c.Finds && string.IsNullOrWhiteSpace(c.Name))
            check.Add($"{prefix}Name", Strings.Code_EnterConditionName);
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
                check.Range($"{prefix}X", c.X, Coordinate);
                check.Range($"{prefix}Y", c.Y, Coordinate);
                if (!IsColor(c.Color))
                    check.Add($"{prefix}Color", Strings.Code_ConditionColor);
                check.Range($"{prefix}Tolerance", c.Tolerance, Tolerance);
                break;
            case ConditionKind.Image:
                if (c.Images is not { Count: > 0 } images)
                {
                    check.Add($"{prefix}Images", Strings.Code_TakeImage);
                }
                else
                {
                    for (int i = 0; i < images.Count; i++)
                    {
                        if (!IsImageId(images[i].Id))
                            check.Add($"{prefix}Images[{i}].Id", Strings.Code_TakeImage);
                        else if (hasImage != null && !hasImage(images[i].Id))
                            check.Add($"{prefix}Images[{i}].Id", Strings.Code_ImageMissing);
                        check.Range($"{prefix}Images[{i}].X", images[i].X, Coordinate);
                        check.Range($"{prefix}Images[{i}].Y", images[i].Y, Coordinate);
                    }
                }
                check.Range($"{prefix}Similarity", c.Similarity, Similarity);
                CheckArea(c, prefix, check);
                break;
            case ConditionKind.Text:
                if (string.IsNullOrWhiteSpace(c.Text))
                    check.Add($"{prefix}Text", Strings.Code_EnterConditionText);
                if (c.Area == SearchArea.Around)
                    check.Add($"{prefix}Area", Strings.Code_TextArea);
                CheckArea(c, prefix, check);
                break;
        }
    }

    private static void CheckArea(MacroCondition c, string prefix, CodeJson.Checker check)
    {
        if (c.Area == SearchArea.Around)
            return;
        check.Range($"{prefix}AreaX", c.AreaX, Coordinate);
        check.Range($"{prefix}AreaY", c.AreaY, Coordinate);
        check.Range($"{prefix}AreaWidth", c.AreaWidth, AreaSize);
        check.Range($"{prefix}AreaHeight", c.AreaHeight, AreaSize);
    }

    /// <summary>Two conditions that find an image or a text never share a name: the actions name them in "Where … was found".</summary>
    private static void CheckNames(IEnumerable<MacroAction> actions, CodeJson.Checker check, string path)
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        CheckNames(actions as List<MacroAction> ?? actions.ToList(), path, names, check);
    }

    private static void CheckNames(List<MacroAction> list, string path, HashSet<string> names, CodeJson.Checker check)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].If is not { } block)
                continue;
            string prefix = path.Length == 0 ? "" : $"{path}[{i}].";
            for (int c = 0; c < block.Conditions.Count; c++)
            {
                var condition = block.Conditions[c];
                if (condition.Finds && !string.IsNullOrWhiteSpace(condition.Name) && !names.Add(condition.Name.Trim()))
                    check.Add($"{prefix}If.Conditions[{c}].Name", Strings.Code_ConditionNameTwice);
                // An Id is what "Where … was found" names: two conditions with one Id can't be told apart.
                if (!names.Add("id:" + condition.Id.ToString("N")))
                    check.Add($"{prefix}If.Conditions[{c}].Id", Strings.Code_ConditionIdTwice);
            }
            CheckNames(block.Then, $"{prefix}If.Then", names, check);
            CheckNames(block.Else, $"{prefix}If.Else", names, check);
        }
    }

    private static void CheckGroups(MacroDefinition m, CodeJson.Checker check)
    {
        var index = new Dictionary<Guid, int>();
        for (int j = 0; j < m.Groups.Count; j++)
        {
            MacroGroup group = m.Groups[j];
            if (!index.TryAdd(group.Id, j))
                check.Add($"Groups[{j}].Id", Strings.Code_GroupIdTwice);
            if (string.IsNullOrWhiteSpace(group.Name))
                check.Add($"Groups[{j}].Name", Strings.Code_EnterGroupName);
        }

        // A group's actions are next to each other in one list: the macro's own, or one Then or Else.
        var finished = new HashSet<Guid>();
        var listOf = new Dictionary<Guid, List<MacroAction>>();
        CheckGroupRuns(m.Actions, "Actions", index, finished, listOf, check);
        var used = listOf.Keys.ToHashSet();
        for (int j = 0; j < m.Groups.Count; j++)
        {
            if (!used.Contains(m.Groups[j].Id))
                check.Add($"Groups[{j}]", Strings.Code_GroupNoActions);
        }
    }

    /// <summary>
    /// A group's actions are one run in one list: an action of a group whose run ended, or whose run is in another list
    /// (<paramref name="listOf"/>: the list of each group met so far), is apart, whichever comes first.
    /// </summary>
    private static void CheckGroupRuns(List<MacroAction> list, string path, Dictionary<Guid, int> index, HashSet<Guid> finished,
        Dictionary<Guid, List<MacroAction>> listOf, CodeJson.Checker check)
    {
        Guid? previous = null;
        for (int i = 0; i < list.Count; i++)
        {
            Guid? groupId = list[i].GroupId;
            if (previous is Guid ended && ended != groupId)
                finished.Add(ended);
            if (groupId is Guid id)
            {
                if (!index.ContainsKey(id))
                    check.Add($"{path}[{i}].GroupId", Strings.Code_NoSuchGroup);
                else if (finished.Contains(id) || (listOf.TryGetValue(id, out var home) && !ReferenceEquals(home, list)))
                    check.Add($"{path}[{i}].GroupId", Strings.Code_GroupApart);
                listOf.TryAdd(id, list);
            }
            previous = groupId;
            foreach (var (branch, name) in new[] { (list[i].If?.Then, "Then"), (list[i].If?.Else, "Else") })
                if (branch != null)
                    CheckGroupRuns(branch, $"{path}[{i}].If.{name}", index, finished, listOf, check);
        }
        if (previous is Guid last)
            finished.Add(last);
    }

    /// <summary>
    /// A row linked to a component: its ComponentId, a component the Library has, its arguments (<see cref="ComponentChecks.CheckCall"/>),
    /// and nothing else (no wait, no name: the component's own actions have them). ComponentId and Arguments on another type
    /// of action are refused.
    /// </summary>
    private static void CheckLink(MacroAction a, string prefix, CallScope scope, IReadOnlyDictionary<Guid, MacroCondition?> found,
        IReadOnlySet<Guid>? notThere, CodeJson.Checker check)
    {
        if (a.Type != MacroActionType.Component)
        {
            if (a.ComponentId != null)
                check.Add($"{prefix}ComponentId", Strings.Code_ComponentIdOnlyLinks);
            if (a.Arguments != null)
                check.Add($"{prefix}Arguments", Strings.Code_ArgumentsOnlyCalls);
            return;
        }
        ComponentChecks.CheckCall(a.ComponentId, a.Arguments, prefix, asCondition: false, scope, found, notThere, check.Add);
        if (a.DelayMs != 0 || !string.IsNullOrEmpty(a.Name))
            check.Add($"{prefix}Type", Strings.Code_ComponentOnlyLink);
    }

    /// <summary>The action window's checks. <paramref name="prefix"/> is "" or "Actions[i].".</summary>
    private static void CheckAction(MacroAction a, string prefix, CodeJson.Checker check)
    {
        check.Range($"{prefix}DelayMs", a.DelayMs, (0, MaxDelayMs));
        switch (a.Type)
        {
            case MacroActionType.PressKey:
            case MacroActionType.KeyDown:
            case MacroActionType.KeyUp:
                if (!CodeJson.IsKeyCode(a.Vk))
                    check.Add($"{prefix}Vk", Strings.Code_MacroChooseKey);
                else if (KeyNames.IsMouse(a.Vk))
                    check.Add($"{prefix}Vk", Strings.Code_UseMouseAction);
                break;
            case MacroActionType.Click:
            case MacroActionType.MouseDown:
            case MacroActionType.MouseUp:
                if (!KeyNames.IsMouse(a.Vk))
                    check.Add($"{prefix}Vk", Strings.Code_ChooseMouseButton);
                break;
        }
        check.KeyModifiers(prefix, a, a.Type switch
        {
            MacroActionType.PressKey => null,
            MacroActionType.KeyDown or MacroActionType.KeyUp =>
                Strings.Code_KeyDownOneKey,
            _ => Strings.Code_MacroOnlyPressKeyModifiers,
        });
        if (a.Type is MacroActionType.PressKey or MacroActionType.Click)
        {
            check.Range($"{prefix}HoldMs", a.HoldMs, HoldMs);
            check.Range($"{prefix}Repeat", a.Repeat, Repeat);
            check.Range($"{prefix}RepeatPauseMs", a.RepeatPauseMs, RepeatPauseMs);
        }
        if (a.Type == MacroActionType.Click && a.ClickCount is not (1 or 2))
            check.Add($"{prefix}ClickCount", Strings.Code_ClickCount);
        if (a.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo)
        {
            check.Range($"{prefix}X", a.X, Coordinate);
            check.Range($"{prefix}Y", a.Y, Coordinate);
        }
        if (a.Type == MacroActionType.MoveTo)
            check.Range($"{prefix}SmoothSpeed", a.SmoothSpeed, SmoothSpeed);
        if (a.Type == MacroActionType.MovePath)
        {
            if (a.Path.Count == 0)
                check.Add($"{prefix}Path", Strings.Code_PathEmpty);
            for (int p = 0; p < a.Path.Count; p++)
            {
                if (double.IsNaN(a.Path[p].T) || a.Path[p].T < 0 || (p > 0 && a.Path[p].T < a.Path[p - 1].T))
                    check.Add($"{prefix}Path[{p}].T", Strings.Code_PathTimes);
            }
        }
        if (a.Type == MacroActionType.Scroll)
            check.Range($"{prefix}ScrollSteps", a.ScrollSteps, ScrollSteps);
        if (a.Type == MacroActionType.PasteText && string.IsNullOrEmpty(a.Text))
            check.Add($"{prefix}Text", Strings.Code_EnterPasteText);
    }
}
