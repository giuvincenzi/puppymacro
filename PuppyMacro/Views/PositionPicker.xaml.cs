using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;

namespace PuppyMacro.Views;

/// <summary>X, Y and Pick on screen: the one field that asks for a point on the screen (physical pixels).</summary>
public partial class PositionPicker : UserControl
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(
        nameof(X), typeof(double), typeof(PositionPicker),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PositionPicker)d).XBox.Value = (double)e.NewValue));

    public static readonly DependencyProperty YProperty = DependencyProperty.Register(
        nameof(Y), typeof(double), typeof(PositionPicker),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PositionPicker)d).YBox.Value = (double)e.NewValue));

    public PositionPicker()
    {
        InitializeComponent();
        XBox.Value = X;
        YBox.Value = Y;
    }

    public double X
    {
        get => (double)GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public double Y
    {
        get => (double)GetValue(YProperty);
        set => SetValue(YProperty, value);
    }

    private void OnBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender == XBox && !X.Equals(XBox.Value))
            X = XBox.Value;
        else if (sender == YBox && !Y.Equals(YBox.Value))
            Y = YBox.Value;
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        // The overlay is topmost and covers this window, so the window does not need to be hidden.
        var picker = new PickPointWindow();
        if (picker.ShowDialog() == true)
        {
            X = picker.ResultX;
            Y = picker.ResultY;
        }
        Window.GetWindow(this)?.Activate();
    }
}
