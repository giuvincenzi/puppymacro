using PuppyMacro.Models;

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
                return "Left and right click need Ctrl, Alt, Shift or Win: alone they would block the mouse.";
            if (KeyNames.IsWheel(binding.Vk))
                return "The scroll wheel needs Ctrl, Alt, Shift or Win: alone it would block scrolling.";
            if (binding.Vk == KeyNames.VK_ESCAPE)
                return "Esc needs Ctrl, Alt, Shift or Win.";
        }
        if (hold && KeyNames.IsWheel(binding.Vk))
            return "Hold needs a key or button that can be held down, and the scroll wheel cannot. Switch to Toggle.";
        return null;
    }
}
