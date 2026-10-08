using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>
/// The "Floating button" expander of the loop and macro editors: on/off, label, size and Position….
/// Works on a copy; <see cref="ToModel"/> returns the edited settings.
/// </summary>
public partial class FloatingButtonEditor
{
    private FloatingButton _button = new();
    private string _name = "";
    private string _hotkeyText = "";
    private bool _holdMode;
    // True while building and loading: the controls' events then come from the code, not from the user.
    private bool _loading = true;

    public FloatingButtonEditor()
    {
        InitializeComponent();
        _loading = false;
    }

    /// <summary>Raised by Position…; the host opens the placement overlay and calls <see cref="SetPosition"/>.</summary>
    public event Action? PositionRequested;

    internal void Load(FloatingButton button, string name, string hotkeyText, bool holdMode)
    {
        _loading = true;
        _button = button.Clone();
        _name = name;
        _hotkeyText = hotkeyText;
        _holdMode = holdMode;
        FloatingSwitch.IsOn = _button.Enabled;
        LabelBox.Text = _button.Label;
        SizeBox.SelectedIndex = _button.Size switch
        {
            FloatingButtonSize.Small => 0,
            FloatingButtonSize.Large => 2,
            _ => 1,
        };
        OpacitySlider.Value = _button.EffectiveOpacity;
        _loading = false;
        FloatingExpander.IsExpanded = _button.Enabled && !_holdMode;
        Update();
    }

    /// <summary>The edited settings (a copy).</summary>
    internal FloatingButton ToModel()
    {
        FloatingButton copy = _button.Clone();
        copy.Opacity = copy.EffectiveOpacity;
        return copy;
    }

    public void SetName(string name)
    {
        _name = name.Trim();
        Update();
    }

    public void SetHotkey(string hotkeyText)
    {
        _hotkeyText = hotkeyText;
        Update();
    }

    /// <summary>Hold items cannot have a floating button: a click starts or stops, it cannot hold.</summary>
    public void SetHoldMode(bool holdMode)
    {
        _holdMode = holdMode;
        if (holdMode)
            FloatingExpander.IsExpanded = false;
        Update();
    }

    public void SetPosition(Point position)
    {
        _button.X = position.X;
        _button.Y = position.Y;
        Update();
    }

    private void OnSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _button.Enabled = FloatingSwitch.IsOn;
        // Turned on for the first time: propose a label from the name.
        if (_button.Enabled && string.IsNullOrEmpty(_button.Label))
            LabelBox.Text = FloatingButton.DefaultLabel(_name);
        FloatingExpander.IsExpanded = _button.Enabled;
        Update();
    }

    private void OnLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        _button.Label = LabelBox.Text.Trim();
        Update();
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        _button.Size = SizeBox.SelectedIndex switch
        {
            0 => FloatingButtonSize.Small,
            2 => FloatingButtonSize.Large,
            _ => FloatingButtonSize.Medium,
        };
        Update();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || OpacityValueText == null)
            return;
        _button.Opacity = (int)Math.Round(e.NewValue);
        Update();
    }

    private void OnPositionClick(object sender, RoutedEventArgs e) => PositionRequested?.Invoke();

    private void Update()
    {
        if (_loading)
            return;
        FloatingSwitch.IsEnabled = !_holdMode;
        FloatingExpander.Description = _holdMode
            ? "Not available with Hold: a click can only start and stop. Switch to Toggle to use it."
            : "A round button in overlay mode that starts and stops it. It replaces its row in the overlay panel.";
        // Its options open only for a button that is on (not for Hold items).
        FloatingExpander.CanExpand = _button.Enabled && !_holdMode;
        // The details apply only to a button that is on.
        foreach (UIElement item in FloatingExpander.Items.OfType<UIElement>())
            item.IsEnabled = _button.Enabled && !_holdMode;

        string label = string.IsNullOrEmpty(_button.Label) ? FloatingButton.DefaultLabel(_name) : _button.Label;
        Preview.Show(label, _hotkeyText, _button.Size);
        Preview.SetBackgroundOpacity(_button.EffectiveOpacity);
        OpacityValueText.Text = $"{_button.EffectiveOpacity}%";
        PositionText.Text = _button.X is double x && _button.Y is double y
            ? $"X {Math.Round(x)}, Y {Math.Round(y)}"
            : "Middle of the main screen until you place it";
    }
}
