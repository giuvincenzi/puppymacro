using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>A loop's JSON, as in settings.json: the loop editor's Code view (see <see cref="CodeJson"/>).</summary>
internal static class LoopJson
{
    public static string Serialize(LoopDefinition loop) => CodeJson.Serialize(loop);

    /// <summary>Checks a loop's text with the loop editor's rules and reads it. Null when there are problems.</summary>
    /// <param name="conflict">The hotkey conflict message for a binding and the app it works in (null: all apps), or null (see <see cref="HotkeyConflicts"/>).</param>
    public static LoopDefinition? Parse(string text, Guid id, Func<HotkeyBinding, string?, string?> conflict, out List<CodeProblem> problems)
    {
        LoopDefinition? loop = CodeJson.Read<LoopDefinition>(text, (l, check) => Check(l, id, conflict, check), out problems);
        if (loop != null)
        {
            loop.Name = loop.Name.Trim();
            loop.AppExe = AppScope.Normalize(loop.AppExe);
        }
        return loop;
    }

    private static void Check(LoopDefinition l, Guid id, Func<HotkeyBinding, string?, string?> conflict, CodeJson.Checker check)
    {
        check.SameId(l.Id, id);
        if (string.IsNullOrWhiteSpace(l.Name))
            check.Add("Name", Strings.Code_EnterName);
        if (l.Actions.Count == 0)
            check.Add("Actions", Strings.Code_AddAction);

        // Fields of version 1.0, converted when settings.json is loaded.
        if (l.KeyVk != null)
            check.Add("KeyVk", Strings.Code_KeyVkOld);
        if (l.IntervalMs != null)
            check.Add("IntervalMs", Strings.Code_IntervalMsOld);
        if (l.HotkeyVk != null)
            check.Add("HotkeyVk", Strings.Code_HotkeyVkOld);

        for (int i = 0; i < l.Actions.Count; i++)
        {
            LoopAction a = l.Actions[i];
            string path = $"Actions[{i}]";
            if (a.Type == ActionType.Key && !CodeJson.IsKeyCode(a.KeyVk))
                check.Add($"{path}.KeyVk", Strings.Code_LoopChooseKey);
            if (a.Type == ActionType.Text && string.IsNullOrEmpty(a.Text))
                check.Add($"{path}.Text", Strings.Code_EnterText);
            if (a.Type == ActionType.Text && a.HoldDown)
                check.Add($"{path}.HoldDown", Strings.Code_TextNoHold);
            bool hasInterval = !(a.Type == ActionType.Key && a.HoldDown);
            if (hasInterval && (double.IsNaN(a.IntervalMs) || a.IntervalMs < LoopAction.MinIntervalMs || a.IntervalMs > LoopAction.MaxIntervalMs))
                check.Add($"{path}.IntervalValue", Strings.Code_IntervalRange);
            CheckPressOptions(a, $"{path}.", check);
        }

        check.AppExe(l.AppExe);
        check.Hotkey(l.Hotkey, l.Mode, binding => conflict(binding, AppScope.Normalize(l.AppExe)));
        if (HotkeyIsPressed(l.Hotkey, l.Actions))
            check.Add("Hotkey", Strings.Code_HotkeyPressed);
        check.KnownSound(l.SoundName);
        check.FloatingButton(l.FloatingButton, l.Mode);
    }

    /// <summary>
    /// The loop's hotkey is a key or combination one of its Press key or Click actions presses (the loop editor
    /// has the same rule).
    /// </summary>
    public static bool HotkeyIsPressed(HotkeyBinding? hotkey, IEnumerable<LoopAction> actions) =>
        hotkey is { IsSet: true } && actions.Any(a => a.Type == ActionType.Key && hotkey.Overlaps(a.Key));

    /// <summary>A row's Press key / Click options, as the loop editor allows them.</summary>
    private static void CheckPressOptions(LoopAction a, string path, CodeJson.Checker check)
    {
        bool key = a.Type == ActionType.Key;
        string? whyNoModifiers = !key ? Strings.Code_LoopOnlyPressKeyModifiers
            : a.IsClick ? Strings.Code_ClickNoModifiers
            : a.HoldDown ? Strings.Code_HoldDownSingleKey
            : null;
        check.KeyModifiers(path, a, whyNoModifiers);

        void NotHere(string field, object? value, string why)
        {
            if (value != null)
                check.Add(path + field, why);
        }
        if (!key)
        {
            string onlyKeys = Strings.Code_OnlyPressKeyAndClick;
            NotHere("HoldMs", a.HoldMs, onlyKeys);
            NotHere("Repeat", a.Repeat, onlyKeys);
            NotHere("RepeatPauseMs", a.RepeatPauseMs, onlyKeys);
        }
        if (!a.IsClick)
        {
            string onlyClicks = Strings.Code_OnlyClick;
            NotHere("ClickCount", a.ClickCount, onlyClicks);
            NotHere("X", a.X, onlyClicks);
            NotHere("Y", a.Y, onlyClicks);
        }
        if (a.HoldMs is double hold)
            check.Range(path + "HoldMs", hold, MacroJson.HoldMs);
        if (a.Repeat is int repeat)
            check.Range(path + "Repeat", repeat, MacroJson.Repeat);
        if (a.RepeatPauseMs is double pause)
            check.Range(path + "RepeatPauseMs", pause, MacroJson.RepeatPauseMs);
        if (a.IsClick && a.ClickCount is not null and not (1 or 2))
            check.Add(path + "ClickCount", Strings.Code_ClickCountOrNull);
        if (a.IsClick && (a.X == null) != (a.Y == null))
            check.Add(path + (a.X == null ? "Y" : "X"), Strings.Code_SetBothXYFixed);
        if (a.IsClick && a.X is int x)
            check.Range(path + "X", x, MacroJson.Coordinate);
        if (a.IsClick && a.Y is int y)
            check.Range(path + "Y", y, MacroJson.Coordinate);
    }
}

/// <summary>A remap's JSON, as in settings.json: the remap editor's Code view (see <see cref="CodeJson"/>).</summary>
internal static class RemapJson
{
    public static string Serialize(RemapDefinition remap) => CodeJson.Serialize(remap);

    /// <summary>Checks a remap's text with the remap editor's rules and reads it. Null when there are problems.</summary>
    /// <param name="clash">For a source and app (null: all apps): why it cannot be remapped (a hotkey, another remap), or null.</param>
    /// <param name="withoutSource">A remap without a hotkey (Source) is read too: only Import, from a file exported with one
    /// that Import had left without it (<see cref="ItemFile"/>).</param>
    public static RemapDefinition? Parse(string text, Guid id, Func<HotkeyBinding, string?, string?> clash, out List<CodeProblem> problems,
        bool withoutSource = false)
    {
        RemapDefinition? remap = CodeJson.Read<RemapDefinition>(text, (r, check) => Check(r, id, clash, check, withoutSource), out problems);
        if (remap != null)
        {
            remap.Note = remap.Note.Trim();
            remap.AppExe = AppScope.Normalize(remap.AppExe);
        }
        return remap;
    }

    private static void Check(RemapDefinition r, Guid id, Func<HotkeyBinding, string?, string?> clash, CodeJson.Checker check,
        bool withoutSource)
    {
        check.SameId(r.Id, id);

        // The single key of versions before schema 7, converted when settings.json is loaded.
        if (r.SourceVk != null)
            check.Add("SourceVk", Strings.Code_SourceVkOld);

        bool sourceValid = false;
        if (r.Source is not { IsSet: true } source)
        {
            if (!withoutSource)
                check.Add("Source", Strings.Code_ChooseSource);
        }
        else
            sourceValid = check.RemapKey("Source", source);

        if (r.Target is not { IsSet: true } target)
            check.Add("Target", Strings.Code_ChooseTarget);
        else if (check.RemapKey("Target", target) && r.Source != null && target.SameAs(r.Source))
            check.Add("Target", Strings.Code_TargetDifferent);

        check.AppExe(r.AppExe);
        if (sourceValid && KeyNames.IsPrimaryMouse(r.Source!.Vk) && !r.Source.HasModifiers && r.AppExe == null)
            check.Add("AppExe", Strings.Code_PrimaryMouseNeedsApp);

        if (sourceValid && clash(r.Source!, AppScope.Normalize(r.AppExe)) is string message)
            check.Add("Source", message);
    }
}
