using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

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
            check.Add("Name", "Enter a name.");
        if (l.Actions.Count == 0)
            check.Add("Actions", "Add at least one key or text.");

        // Fields of version 1.0, converted when settings.json is loaded.
        if (l.KeyVk != null)
            check.Add("KeyVk", "Not used any more: keys go in \"Actions\". Remove it.");
        if (l.IntervalMs != null)
            check.Add("IntervalMs", "Not used any more: intervals go in \"Actions\". Remove it.");
        if (l.HotkeyVk != null)
            check.Add("HotkeyVk", "Not used any more: use \"Hotkey\". Remove it.");

        for (int i = 0; i < l.Actions.Count; i++)
        {
            LoopAction a = l.Actions[i];
            string path = $"Actions[{i}]";
            if (a.Type == ActionType.Key && !CodeJson.IsKeyCode(a.KeyVk))
                check.Add($"{path}.KeyVk", "Choose the key or mouse button: a virtual-key code from 1 to 254.");
            if (a.Type == ActionType.Text && string.IsNullOrEmpty(a.Text))
                check.Add($"{path}.Text", "Enter the text.");
            if (a.Type == ActionType.Text && a.HoldDown)
                check.Add($"{path}.HoldDown", "Only key rows can be held down.");
            bool hasInterval = !(a.Type == ActionType.Key && a.HoldDown);
            if (hasInterval && (double.IsNaN(a.IntervalMs) || a.IntervalMs < LoopAction.MinIntervalMs || a.IntervalMs > LoopAction.MaxIntervalMs))
                check.Add($"{path}.IntervalValue", "The interval must be between 10 ms and 24 h.");
        }

        check.AppExe(l.AppExe);
        check.Hotkey(l.Hotkey, l.Mode, binding => conflict(binding, AppScope.Normalize(l.AppExe)));
        if (l.Hotkey is { IsSet: true } hotkey && !hotkey.HasModifiers
            && l.Actions.Any(a => a.Type == ActionType.Key && a.KeyVk == hotkey.Vk))
            check.Add("Hotkey", "The hotkey cannot be one of the keys this loop presses.");
        check.KnownSound(l.SoundName);
        check.FloatingButton(l.FloatingButton, l.Mode);
    }
}

/// <summary>A remap's JSON, as in settings.json: the remap editor's Code view (see <see cref="CodeJson"/>).</summary>
internal static class RemapJson
{
    public static string Serialize(RemapDefinition remap) => CodeJson.Serialize(remap);

    /// <summary>Checks a remap's text with the remap editor's rules and reads it. Null when there are problems.</summary>
    /// <param name="clash">For a source key and app (null: all apps): why it cannot be remapped (a hotkey, another remap), or null.</param>
    public static RemapDefinition? Parse(string text, Guid id, Func<int, string?, string?> clash, out List<CodeProblem> problems)
    {
        RemapDefinition? remap = CodeJson.Read<RemapDefinition>(text, (r, check) => Check(r, id, clash, check), out problems);
        if (remap != null)
        {
            remap.Note = remap.Note.Trim();
            remap.AppExe = AppScope.Normalize(remap.AppExe);
        }
        return remap;
    }

    private static void Check(RemapDefinition r, Guid id, Func<int, string?, string?> clash, CodeJson.Checker check)
    {
        check.SameId(r.Id, id);
        bool sourceValid = CodeJson.IsKeyCode(r.SourceVk) && !KeyNames.IsModifier(r.SourceVk);
        if (!sourceValid)
            check.Add("SourceVk", "Choose the key or mouse button to remap: a virtual-key code from 1 to 254, not Ctrl, Alt, Shift or Win.");

        if (r.Target is not { IsSet: true } target)
            check.Add("Target", "Choose what to send instead.");
        else if (!CodeJson.IsKeyCode(target.Vk) || KeyNames.IsModifier(target.Vk))
            check.Add("Target.Vk", "Choose the key or mouse button to send: a virtual-key code from 1 to 254; set Ctrl, Alt, Shift and Win with their own fields.");
        else if (!target.HasModifiers && target.Vk == r.SourceVk)
            check.Add("Target", "The key to send must be different from the key pressed.");

        check.AppExe(r.AppExe);
        if (KeyNames.IsPrimaryMouse(r.SourceVk) && r.AppExe == null)
            check.Add("AppExe", "Left and right click can be remapped only for a specific app, so they keep working everywhere else.");

        if (sourceValid && clash(r.SourceVk, AppScope.Normalize(r.AppExe)) is string message)
            check.Add("SourceVk", message);
    }
}
