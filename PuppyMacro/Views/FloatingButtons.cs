using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>Which loops and macros get a floating button in overlay mode.</summary>
internal static class FloatingButtons
{
    /// <summary>
    /// Enabled, with the floating button on, and not Hold (a click starts or stops, it cannot
    /// hold). Such items are left out of the overlay panel list.
    /// </summary>
    public static bool HasButton(IListItem item) =>
        item.IsItemEnabled && !item.IsHoldMode && item.FloatingButton.Enabled;
}

/// <summary>An editor's floating button, not saved yet, for the placement overlay.</summary>
internal sealed record EditedFloatingButton(System.Guid Id, string Name, string HotkeyText, FloatingButton Button);
