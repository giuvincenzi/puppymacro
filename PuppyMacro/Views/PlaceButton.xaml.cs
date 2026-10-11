using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// The one button that places something on the screen or picks a point: the target icon and <see cref="Text"/>.
/// <see cref="IsAccent"/> gives it the library's accent style (Settings' Position on screen).
/// </summary>
public partial class PlaceButton : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PlaceButton), new PropertyMetadata("", (d, _) => ((PlaceButton)d).ShowText()));

    public static readonly DependencyProperty IsAccentProperty = DependencyProperty.Register(
        nameof(IsAccent), typeof(bool), typeof(PlaceButton), new PropertyMetadata(false, (d, _) => ((PlaceButton)d).ShowStyle()));

    public PlaceButton() => InitializeComponent();

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    public event RoutedEventHandler? Click;

    private void ShowText()
    {
        Label.Text = Text;
        AutomationProperties.SetName(TheButton, Text);
    }

    private void ShowStyle() =>
        TheButton.Style = IsAccent ? (Style)FindResource("AccentButtonStyle") : null;

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
