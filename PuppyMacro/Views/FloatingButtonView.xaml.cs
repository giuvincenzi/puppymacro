using System;
using System.Windows;
using System.Windows.Media;
using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>
/// The round floating button: label, hotkey badge, running and disabled state. Used by the floating button
/// windows, the placement overlay and the editors' preview.
/// </summary>
public partial class FloatingButtonView
{
    /// <summary>Space around the circle (WPF units) for the running halo.</summary>
    public const double Inset = 4;

    private static readonly Brush RunningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)));
    private static readonly Brush IdleBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DisabledTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    // A disabled button's background is this much fainter than its opacity setting.
    private const double DisabledBackgroundFactor = 0.65;

    private bool _isRunning;
    private bool _isItemEnabled = true;
    private int _opacityPercent = FloatingButton.DefaultOpacity;

    public FloatingButtonView()
    {
        InitializeComponent();
        Show("", "", FloatingButtonSize.Medium);
        ApplyState();
    }

    /// <summary>The circle, whose screen rectangle is the clickable area.</summary>
    public FrameworkElement ClickArea => Circle;

    /// <summary>Sets the label, the hotkey badge (empty = none) and the size.</summary>
    public void Show(string label, string hotkeyText, FloatingButtonSize size)
    {
        double diameter = FloatingButton.DiameterOf(size);
        Circle.Width = Circle.Height = diameter;
        Circle.CornerRadius = new CornerRadius(diameter / 2);
        DisabledRing.Width = DisabledRing.Height = diameter;
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
            ApplyState();
        }
    }

    /// <summary>False: shown as disabled (dashed ring, grey label, fainter background).</summary>
    public bool IsItemEnabled
    {
        get => _isItemEnabled;
        set
        {
            _isItemEnabled = value;
            ApplyState();
        }
    }

    /// <summary>Sets the circle background opacity (percent), like the overlay panel.</summary>
    public void SetBackgroundOpacity(int percent)
    {
        _opacityPercent = Math.Clamp(percent, 0, 100);
        ApplyState();
    }

    private void ApplyState()
    {
        bool disabled = !_isItemEnabled && !_isRunning;
        Halo.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed;
        DisabledRing.Visibility = disabled ? Visibility.Visible : Visibility.Collapsed;
        Circle.BorderBrush = _isRunning ? RunningBrush : disabled ? Brushes.Transparent : IdleBorderBrush;
        Circle.BorderThickness = new Thickness(_isRunning ? 2 : 1);
        LabelText.Foreground = _isRunning ? RunningBrush : disabled ? DisabledTextBrush : Brushes.White;
        BadgeText.Foreground = disabled ? DisabledTextBrush : Brushes.White;
        double factor = disabled ? DisabledBackgroundFactor : 1;
        byte alpha = (byte)Math.Round(_opacityPercent * factor * 255 / 100.0);
        Circle.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
