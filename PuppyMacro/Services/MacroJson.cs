using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Models;

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

    public static string Serialize(MacroDefinition macro) => CodeJson.Serialize(macro);

    public static string Serialize(MacroAction action) => CodeJson.Serialize(action);

    /// <summary>Checks a whole macro's text and reads it. Null when there are problems.</summary>
    /// <param name="conflict">The hotkey conflict message for a binding and the app it works in (null: all apps), or null (see <see cref="HotkeyConflicts"/>).</param>
    public static MacroDefinition? Parse(string text, Guid id, Func<HotkeyBinding, string?, string?> conflict, out List<CodeProblem> problems)
    {
        MacroDefinition? macro = CodeJson.Read<MacroDefinition>(text, (m, check) => CheckMacro(m, id, conflict, check), out problems);
        if (macro == null)
            return null;
        macro.Name = macro.Name.Trim();
        macro.AppExe = AppScope.Normalize(macro.AppExe);
        foreach (var group in macro.Groups)
            group.Name = group.Name.Trim();
        return macro;
    }

    /// <summary>
    /// Checks one action's text (the action window's Code view) and reads it. Its GroupId must be
    /// one of <paramref name="allowedGroups"/> (see <see cref="AllowedGroupIds"/>).
    /// </summary>
    public static MacroAction? ParseAction(string text, IReadOnlyList<Guid?> allowedGroups, IReadOnlyDictionary<Guid, string> groupNames,
        out List<CodeProblem> problems)
    {
        MacroAction? action = CodeJson.Read<MacroAction>(text, (a, check) =>
        {
            CheckAction(a, "", check);
            if (!allowedGroups.Contains(a.GroupId))
            {
                string allowed = string.Join(", ", allowedGroups.Select(g => g is Guid id && groupNames.TryGetValue(id, out string? name)
                    ? $"\"{id}\" ({name})" : "null"));
                Add(check, "GroupId", $"This action can only be in: {allowed}. A group's actions must be next to each other.");
            }
        }, out problems);
        if (action != null)
            action.Name = action.Name.Trim();
        return action;
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

    private static void CheckMacro(MacroDefinition m, Guid id, Func<HotkeyBinding, string?, string?> conflict, CodeJson.Checker check)
    {
        check.SameId(m.Id, id);
        if (string.IsNullOrWhiteSpace(m.Name))
            check.Add("Name", "Enter a name.");
        if (m.Actions.Count == 0)
            check.Add("Actions", "Add at least one action.");
        check.Range("RepeatCount", m.RepeatCount, RepeatCount);
        if (!MacroDefinition.SpeedSteps.Contains(m.Speed))
            check.Add("Speed", "Must be 0.25, 0.5, 1, 2 or 4.");
        check.KnownSound(m.SoundName);
        check.AppExe(m.AppExe);
        check.Hotkey(m.Hotkey, m.Mode, binding => conflict(binding, AppScope.Normalize(m.AppExe)));
        check.FloatingButton(m.FloatingButton, m.Mode);
        CheckGroups(m, check);
        for (int i = 0; i < m.Actions.Count; i++)
            CheckAction(m.Actions[i], $"Actions[{i}].", check);
    }

    private static void CheckGroups(MacroDefinition m, CodeJson.Checker check)
    {
        var index = new Dictionary<Guid, int>();
        for (int j = 0; j < m.Groups.Count; j++)
        {
            MacroGroup group = m.Groups[j];
            if (!index.TryAdd(group.Id, j))
                check.Add($"Groups[{j}].Id", "Two groups have this Id.");
            if (string.IsNullOrWhiteSpace(group.Name))
                check.Add($"Groups[{j}].Name", "Enter a group name.");
        }

        var finished = new HashSet<Guid>();
        var used = new HashSet<Guid>();
        Guid? previous = null;
        for (int i = 0; i < m.Actions.Count; i++)
        {
            Guid? groupId = m.Actions[i].GroupId;
            if (previous is Guid ended && ended != groupId)
                finished.Add(ended);
            if (groupId is Guid id)
            {
                if (!index.ContainsKey(id))
                    check.Add($"Actions[{i}].GroupId", "No group in \"Groups\" has this Id.");
                else if (finished.Contains(id))
                    check.Add($"Actions[{i}].GroupId", "A group's actions must be next to each other.");
                used.Add(id);
            }
            previous = groupId;
        }
        for (int j = 0; j < m.Groups.Count; j++)
        {
            if (!used.Contains(m.Groups[j].Id))
                check.Add($"Groups[{j}]", "This group has no actions: give it some (\"GroupId\") or remove it.");
        }
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
                    check.Add($"{prefix}Vk", "Choose a key: a virtual-key code from 1 to 254.");
                break;
            case MacroActionType.Click:
            case MacroActionType.MouseDown:
            case MacroActionType.MouseUp:
                if (!KeyNames.IsMouse(a.Vk))
                    check.Add($"{prefix}Vk", "Choose a mouse button: 1 left, 2 right, 4 middle, 5 back (X1), 6 forward (X2).");
                break;
        }
        if (a.Type is MacroActionType.PressKey or MacroActionType.Click)
        {
            check.Range($"{prefix}HoldMs", a.HoldMs, HoldMs);
            check.Range($"{prefix}Repeat", a.Repeat, Repeat);
            check.Range($"{prefix}RepeatPauseMs", a.RepeatPauseMs, RepeatPauseMs);
        }
        if (a.Type == MacroActionType.Click && a.ClickCount is not (1 or 2))
            check.Add($"{prefix}ClickCount", "Must be 1 (single click) or 2 (double click).");
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
                check.Add($"{prefix}Path", "A path needs at least one point.");
            for (int p = 0; p < a.Path.Count; p++)
            {
                if (double.IsNaN(a.Path[p].T) || a.Path[p].T < 0 || (p > 0 && a.Path[p].T < a.Path[p - 1].T))
                    check.Add($"{prefix}Path[{p}].T", "Times must start at 0 or more and never go back.");
            }
        }
        if (a.Type == MacroActionType.Scroll)
            check.Range($"{prefix}ScrollSteps", a.ScrollSteps, ScrollSteps);
        if (a.Type == MacroActionType.PasteText && string.IsNullOrEmpty(a.Text))
            check.Add($"{prefix}Text", "Enter the text to paste.");
    }
}
