using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>Which loops and macros the overlay shows.</summary>
internal static class OverlayItems
{
    /// <summary>
    /// Shown in overlay mode (as a panel row or a floating button): Show in overlay is on, and it is enabled or
    /// Hide when disabled is off. A disabled one is shown as disabled.
    /// </summary>
    public static bool IsShown(IListItem item) =>
        item.ShowInOverlay && (item.IsItemEnabled || !item.HideInOverlayWhenDisabled);
}

/// <summary>Which loops and macros get a floating button in overlay mode.</summary>
internal static class FloatingButtons
{
    /// <summary>
    /// Shown in the overlay, with the floating button on, and not Hold (a click starts or stops, it cannot
    /// hold). Such items are left out of the overlay panel list.
    /// </summary>
    public static bool HasButton(IListItem item) =>
        OverlayItems.IsShown(item) && !item.IsHoldMode && item.FloatingButton.Enabled;
}

/// <summary>An editor's floating button, not saved yet, for the placement overlay.</summary>
internal sealed record EditedFloatingButton(System.Guid Id, string Name, string HotkeyText, FloatingButton Button);
