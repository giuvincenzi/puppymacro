using System;
using System.Windows;
using System.Windows.Media;
using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>
/// The round floating button: label, hotkey badge and running state. Used by the floating button
/// windows, the placement overlay and the editors' preview.
/// </summary>
public partial class FloatingButtonView
{
    /// <summary>Space around the circle (WPF units) for the running halo.</summary>
    public const double Inset = 4;

    private static readonly Brush RunningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)));
    private static readonly Brush IdleBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));

    private bool _isRunning;

    public FloatingButtonView()
    {
        InitializeComponent();
        Show("", "", FloatingButtonSize.Medium);
    }

    /// <summary>The circle, whose screen rectangle is the clickable area.</summary>
    public FrameworkElement ClickArea => Circle;

    /// <summary>Sets the label, the hotkey badge (empty = none) and the size.</summary>
    public void Show(string label, string hotkeyText, FloatingButtonSize size)
    {
        double diameter = FloatingButton.DiameterOf(size);
        Circle.Width = Circle.Height = diameter;
        Circle.CornerRadius = new CornerRadius(diameter / 2);
        Halo.Width = Halo.Height = diameter + 2 * Inset;
        LabelText.Text = label;
        LabelText.FontSize = size switch
        {
            FloatingButtonSize.Small => 12,
            FloatingButtonSize.Large => 18,
            _ => 15,
        };
        BadgeText.Text = hotkeyText;
        Badge.Visibility = string.IsNullOrEmpty(hotkeyText) ? Visibility.Collapsed : Visibility.Visible;
        Badge.Margin = new Thickness(diameter - 20, diameter - 14, 0, 0);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            _isRunning = value;
            Halo.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Circle.BorderBrush = value ? RunningBrush : IdleBorderBrush;
            Circle.BorderThickness = new Thickness(value ? 2 : 1);
            LabelText.Foreground = value ? RunningBrush : Brushes.White;
        }
    }

    /// <summary>Sets the circle background opacity (percent), like the overlay panel.</summary>
    public void SetBackgroundOpacity(int percent)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);
        Circle.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
