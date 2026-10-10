using System;
using System.Linq;
using PuppyMacro.Models;

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
    /// <param name="ignoreGlobal">"StopAll", "OverlayMode" or "Record" when changing that global hotkey.</param>
    public static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId = null, string? ignoreGlobal = null, string? appExe = null) =>
        Find(binding, settings, macros, ignoreId, ignoreGlobal,
            itemApp => ignoreGlobal != null || AppScope.Same(itemApp, appExe));

    /// <summary>
    /// For a remap's source key: the hotkeys that would take it first (hotkeys come before remaps) in the apps the
    /// remap works in (<paramref name="appExe"/>, null for all apps).
    /// </summary>
    public static string? FindForRemap(int sourceVk, string? appExe, AppSettings settings, MacroLibrary macros) =>
        Find(HotkeyBinding.FromKey(sourceVk), settings, macros, null, null,
            itemApp => itemApp == null || appExe == null || AppScope.Same(itemApp, appExe));

    private static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId, string? ignoreGlobal, Func<string?, bool> clashesWith)
    {
        string name = KeyNames.Format(binding);
        if (ignoreGlobal != "StopAll" && binding.SameAs(settings.StopAllHotkey))
            return $"{name} is already used by Stop all.";
        if (ignoreGlobal != "OverlayMode" && binding.SameAs(settings.OverlayModeHotkey))
            return $"{name} is already used by Overlay mode.";
        if (ignoreGlobal != "Record" && binding.SameAs(settings.RecordHotkey))
            return $"{name} is already used by Record.";

        var loop = settings.Loops.FirstOrDefault(l => l.Id != ignoreId && binding.SameAs(l.Hotkey) && clashesWith(l.AppExe));
        if (loop != null)
            return $"{name} is already used by the loop \"{loop.Name}\"{InApp(loop.AppExe)}.";

        var macro = macros.Macros.FirstOrDefault(m => m.Id != ignoreId && binding.SameAs(m.Hotkey) && clashesWith(m.AppExe));
        if (macro != null)
            return $"{name} is already used by the macro \"{macro.Name}\"{InApp(macro.AppExe)}.";

        return null;
    }

    private static string InApp(string? appExe) => appExe == null ? "" : $" in {appExe}";
}
