using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// The one button that places something on the screen or picks a point: its icon and <see cref="Text"/>.
/// </summary>
public partial class PlaceButton : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PlaceButton), new PropertyMetadata("", (d, _) => ((PlaceButton)d).ShowText()));

    public static readonly DependencyProperty MovesOverlayProperty = DependencyProperty.Register(
        nameof(MovesOverlay), typeof(bool), typeof(PlaceButton), new PropertyMetadata(false, (d, _) => ((PlaceButton)d).ShowIcon()));

    public PlaceButton() => InitializeComponent();

    /// <summary>The button moves the overlay (the Move icon); otherwise it picks something on the screen (Bullseye).</summary>
    public bool MovesOverlay
    {
        get => (bool)GetValue(MovesOverlayProperty);
        set => SetValue(MovesOverlayProperty, value);
    }

    private void ShowIcon()
    {
        PickIcon.Visibility = MovesOverlay ? Visibility.Collapsed : Visibility.Visible;
        MoveIcon.Visibility = MovesOverlay ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public event RoutedEventHandler? Click;

    private void ShowText()
    {
        Label.Text = Text;
        AutomationProperties.SetName(TheButton, Text);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
