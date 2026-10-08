using System;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>Finds which loop, macro or global hotkey already uses a binding.</summary>
internal static class HotkeyConflicts
{
    /// <param name="ignoreId">Loop or macro being edited (its own hotkey is not a conflict).</param>
    /// <param name="ignoreGlobal">"StopAll", "OverlayMode" or "Record" when changing that global hotkey.</param>
    public static string? Find(HotkeyBinding binding, AppSettings settings, MacroLibrary macros,
        Guid? ignoreId = null, string? ignoreGlobal = null)
    {
        string name = KeyNames.Format(binding);
        if (ignoreGlobal != "StopAll" && binding.SameAs(settings.StopAllHotkey))
            return $"{name} is already used by Stop all.";
        if (ignoreGlobal != "OverlayMode" && binding.SameAs(settings.OverlayModeHotkey))
            return $"{name} is already used by Overlay mode.";
        if (ignoreGlobal != "Record" && binding.SameAs(settings.RecordHotkey))
            return $"{name} is already used by Record.";

        var loop = settings.Loops.FirstOrDefault(l => l.Id != ignoreId && binding.SameAs(l.Hotkey));
        if (loop != null)
            return $"{name} is already used by the loop \"{loop.Name}\".";

        var macro = macros.Macros.FirstOrDefault(m => m.Id != ignoreId && binding.SameAs(m.Hotkey));
        if (macro != null)
            return $"{name} is already used by the macro \"{macro.Name}\".";

        return null;
    }
}
