using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// Lights up the header of every <see cref="CardExpander"/> under the mouse and while pressed, as in
/// Windows Settings, with the brushes WPF-UI uses for its clickable card (CardAction). WPF-UI 4.1's
/// CardExpander template has no such state. The header is the template's "ExpanderToggleButton",
/// inside "ToggleButtonBorder". Class handlers on ToggleButton, so every expander gets it, also the ones
/// whose template is applied later (pages that start hidden).
/// </summary>
internal static class CardExpanderHover
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ToggleButton), UIElement.MouseEnterEvent, new MouseEventHandler(OnEnter));
        EventManager.RegisterClassHandler(typeof(ToggleButton), UIElement.MouseLeaveEvent, new MouseEventHandler(OnLeave));
        EventManager.RegisterClassHandler(typeof(ToggleButton), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown));
        EventManager.RegisterClassHandler(typeof(ToggleButton), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp));
    }

    private static void OnEnter(object sender, MouseEventArgs e) => Show(sender, "CardBackgroundPointerOver");

    private static void OnLeave(object sender, MouseEventArgs e) => Show(sender, null);

    private static void OnDown(object sender, MouseButtonEventArgs e) => Show(sender, "CardBackgroundPressed");

    private static void OnUp(object sender, MouseButtonEventArgs e) =>
        Show(sender, ((UIElement)sender).IsMouseOver ? "CardBackgroundPointerOver" : null);

    /// <summary>Sets the header's background brush, or gives it back to the template (null).</summary>
    private static void Show(object sender, string? brushKey)
    {
        if (sender is not ToggleButton { TemplatedParent: CardExpander, Name: "ExpanderToggleButton" } header
            || VisualTreeHelper.GetParent(header) is not Border border)
            return;
        if (brushKey == null || !header.IsEnabled)
            border.ClearValue(Border.BackgroundProperty);
        else
            border.SetResourceReference(Border.BackgroundProperty, brushKey);
    }
}
