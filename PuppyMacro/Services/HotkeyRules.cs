using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>Which keys and buttons can be hotkeys (conflicts with other hotkeys: <see cref="HotkeyConflicts"/>).</summary>
internal static class HotkeyRules
{
    /// <summary>
    /// Why <paramref name="binding"/> cannot be a hotkey, or null. Left and right click, the scroll
    /// wheel and Esc need a modifier: alone they would block the mouse, scrolling or Esc everywhere.
    /// The wheel has no release, so it cannot start a Hold loop or macro.
    /// </summary>
    public static string? Problem(HotkeyBinding binding, bool hold)
    {
        if (!binding.HasModifiers)
        {
            if (KeyNames.IsPrimaryMouse(binding.Vk))
                return Strings.Hotkey_ClickNeedsModifier;
            if (KeyNames.IsWheel(binding.Vk))
                return Strings.Hotkey_WheelNeedsModifier;
            if (binding.Vk == KeyNames.VK_ESCAPE)
                return Strings.Hotkey_EscNeedsModifier;
        }
        if (hold && KeyNames.IsWheel(binding.Vk))
            return Strings.Hotkey_WheelCantHold;
        return null;
    }
}
