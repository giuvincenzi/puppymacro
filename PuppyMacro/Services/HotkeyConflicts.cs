using System;
using System.Globalization;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>Finds which loop, macro or global hotkey already uses a binding.</summary>
internal static class HotkeyConflicts
{
    /// <summary>
    /// For a loop's or macro's hotkey (<paramref name="appExe"/>: the app it works in, null for all apps), or for a
    /// global hotkey (<paramref name="ignoreGlobal"/> set). Two loops or macros can share a hotkey only when they work
    /// in different apps: one for an app and one for all apps is allowed too (the app's one wins while it is in front).
    /// Global hotkeys work everywhere, so they clash with every loop and macro.
    /// </summary>
    /// <param name="ignoreId">Loop or macro being edited (its own hotkey is not a conflict).</param>
    /// <param name="ignoreGlobal">"OpenMain", "OverlayMode", "StopAll" or "Record" when changing that global hotkey.</param>
    public static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId = null, string? ignoreGlobal = null, string? appExe = null) =>
        Find(binding, settings, macros, ignoreId, ignoreGlobal,
            itemApp => ignoreGlobal != null || AppScope.Same(itemApp, appExe));

    /// <summary>
    /// For a remap's source (a key, a combination or a modifier key alone): the hotkeys that would take it first
    /// (hotkeys come before remaps) in the apps the remap works in (<paramref name="appExe"/>, null for all apps).
    /// </summary>
    public static string? FindForRemap(HotkeyBinding source, string? appExe, AppSettings settings, MacroLibrary macros) =>
        Find(source, settings, macros, null, null,
            itemApp => itemApp == null || appExe == null || AppScope.Same(itemApp, appExe));

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

    private static string Text(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
