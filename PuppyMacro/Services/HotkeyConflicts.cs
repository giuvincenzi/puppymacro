using System;
using System.Globalization;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>
/// Finds which global hotkey, loop, macro or remap already uses a binding. Loops, macros and remaps follow one rule: two of
/// them can share keys only when they work in different apps, and one for an app with one for all apps is allowed too (the
/// app's one wins while that app is in front, <c>LoopEngine.OnKey</c>). Global hotkeys work everywhere, so they clash with
/// every loop, macro and remap.
/// </summary>
internal static class HotkeyConflicts
{
    /// <summary>
    /// For a loop's or macro's hotkey (<paramref name="appExe"/>: the app it works in, null for all apps), or for a
    /// global hotkey (<paramref name="ignoreGlobal"/> set): the global hotkeys, then the loops, macros and remaps for the
    /// same apps (for a global hotkey, all of them).
    /// </summary>
    /// <param name="ignoreId">Loop or macro being edited (its own hotkey is not a conflict).</param>
    /// <param name="ignoreGlobal">"OpenMain", "OverlayMode", "StopAll" or "Record" when changing that global hotkey.</param>
    public static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId = null, string? ignoreGlobal = null, string? appExe = null)
    {
        bool ClashesWith(string? itemApp) => ignoreGlobal != null || AppScope.Same(itemApp, appExe);
        return Find(binding, settings, macros, ignoreId, ignoreGlobal, ClashesWith) ?? UsedByRemap(binding, settings, ClashesWith);
    }

    /// <summary>
    /// For a remap's source (a key, a combination or a modifier key alone): the global hotkeys and the hotkeys of the loops
    /// and macros for the same apps as the remap (<paramref name="appExe"/>, null for all apps). Other remaps:
    /// <see cref="AlreadyRemapped"/>.
    /// </summary>
    public static string? FindForRemap(HotkeyBinding source, string? appExe, AppSettings settings, MacroLibrary macros) =>
        Find(source, settings, macros, null, null, itemApp => AppScope.Same(itemApp, appExe));

    /// <summary>
    /// Why <paramref name="source"/> cannot be remapped for <paramref name="appExe"/> (null: all apps): a hotkey, or another
    /// remap of the same key for the same apps (the remap editor's and the AI tools' rule). Null when it can.
    /// </summary>
    /// <param name="editingId">The remap being changed: its own source is not a clash.</param>
    public static string? RemapClash(HotkeyBinding source, string? appExe, AppSettings settings, MacroLibrary macros, Guid editingId)
    {
        string? conflict = FindForRemap(source, appExe, settings, macros);
        if (conflict != null)
            return Text(Strings.Hotkey_TakesPriorityOverRemaps, conflict);
        return AlreadyRemapped(source, appExe, settings, editingId);
    }

    /// <summary>Another remap of <paramref name="source"/> in the same apps (<paramref name="appExe"/>, null: all apps), or null.</summary>
    public static string? AlreadyRemapped(HotkeyBinding source, string? appExe, AppSettings settings, Guid editingId)
    {
        var same = settings.Remaps.FirstOrDefault(r => r.Id != editingId && source.Overlaps(r.Source)
            && string.Equals(r.AppExe, appExe, StringComparison.OrdinalIgnoreCase));
        if (same == null)
            return null;
        return appExe == null
            ? Text(Strings.Hotkey_AlreadyRemapped, KeyNames.Format(source))
            : Text(Strings.Hotkey_AlreadyRemappedInApp, KeyNames.Format(source), appExe);
    }

    /// <summary>
    /// Bindings clash when some key press would start both: the same key and modifiers, on sides that are not
    /// opposite (Left Ctrl + A and Ctrl + A clash, Left Ctrl + A and Right Ctrl + A do not).
    /// </summary>
    private static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId, string? ignoreGlobal, Func<string?, bool> clashesWith)
    {
        string name = KeyNames.Format(binding);
        if (ignoreGlobal != "StopAll" && binding.Overlaps(settings.StopAllHotkey))
            return Text(Strings.Hotkey_UsedByStopAll, name);
        if (ignoreGlobal != "OverlayMode" && binding.Overlaps(settings.OverlayModeHotkey))
            return Text(Strings.Hotkey_UsedByOverlayMode, name);
        if (ignoreGlobal != "Record" && binding.Overlaps(settings.RecordHotkey))
            return Text(Strings.Hotkey_UsedByRecord, name);
        if (ignoreGlobal != "OpenMain" && binding.Overlaps(settings.OpenMainHotkey))
            return Text(Strings.Hotkey_UsedByOpenMain, name);

        var loop = settings.Loops.FirstOrDefault(l => l.Id != ignoreId && binding.Overlaps(l.Hotkey) && clashesWith(l.AppExe));
        if (loop != null)
            return loop.AppExe == null
                ? Text(Strings.Hotkey_UsedByLoop, name, loop.Name)
                : Text(Strings.Hotkey_UsedByLoopInApp, name, loop.Name, loop.AppExe);

        var macro = macros.Macros.FirstOrDefault(m => m.Id != ignoreId && binding.Overlaps(m.Hotkey) && clashesWith(m.AppExe));
        if (macro != null)
            return macro.AppExe == null
                ? Text(Strings.Hotkey_UsedByMacro, name, macro.Name)
                : Text(Strings.Hotkey_UsedByMacroInApp, name, macro.Name, macro.AppExe);

        return null;
    }

    /// <summary>The remap whose source <paramref name="binding"/> clashes with, among those for the apps <paramref name="clashesWith"/> accepts, or null.</summary>
    private static string? UsedByRemap(HotkeyBinding binding, AppSettings settings, Func<string?, bool> clashesWith)
    {
        var remap = settings.Remaps.FirstOrDefault(r => binding.Overlaps(r.Source) && clashesWith(r.AppExe));
        if (remap == null)
            return null;
        // As the lists name it: its note, or its keys.
        string title = string.IsNullOrWhiteSpace(remap.Note)
            ? Text(Strings.Ai_RemapTitle, KeyNames.Format(remap.Source), KeyNames.Format(remap.Target))
            : remap.Note.Trim();
        string name = KeyNames.Format(binding);
        return remap.AppExe == null
            ? Text(Strings.Hotkey_UsedByRemap, name, title)
            : Text(Strings.Hotkey_UsedByRemapInApp, name, title, remap.AppExe);
    }

    private static string Text(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
