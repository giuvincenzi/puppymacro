using System;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>
/// The "Floating button" card of the loop and macro editors: on/off, label, size and Position….
/// Works on a copy; <see cref="ToModel"/> returns the edited settings.
/// </summary>
public partial class FloatingButtonEditor
{
    private FloatingButton _button = new();
    private string _name = "";
    private string _hotkeyText = "";
    private bool _holdMode;
    private bool _loading;

    public FloatingButtonEditor()
    {
        InitializeComponent();
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
        FloatingSwitch.IsChecked = _button.Enabled;
        LabelBox.Text = _button.Label;
        SmallRadio.IsChecked = _button.Size == FloatingButtonSize.Small;
        MediumRadio.IsChecked = _button.Size == FloatingButtonSize.Medium;
        LargeRadio.IsChecked = _button.Size == FloatingButtonSize.Large;
        _loading = false;
        Update();
    }

    /// <summary>The edited settings (a copy).</summary>
    internal FloatingButton ToModel() => _button.Clone();

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
        _button.Enabled = FloatingSwitch.IsChecked == true;
        // Turned on for the first time: propose a label from the name.
        if (_button.Enabled && string.IsNullOrEmpty(_button.Label))
            LabelBox.Text = FloatingButton.DefaultLabel(_name);
        Update();
    }

    private void OnLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        _button.Label = LabelBox.Text.Trim();
        Update();
    }

    private void OnSizeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _button.Size = SmallRadio.IsChecked == true ? FloatingButtonSize.Small
            : LargeRadio.IsChecked == true ? FloatingButtonSize.Large
            : FloatingButtonSize.Medium;
        Update();
    }

    private void OnPositionClick(object sender, RoutedEventArgs e) => PositionRequested?.Invoke();

    private void Update()
    {
        if (_loading)
            return;
        FloatingSwitch.IsEnabled = !_holdMode;
        DescriptionText.Text = _holdMode
            ? "Not available with Hold: a click can only start and stop. Switch to Toggle to use it."
            : "A round button in game mode that starts and stops it. It replaces its row in the game mode panel.";
        DetailsPanel.Visibility = _button.Enabled && !_holdMode ? Visibility.Visible : Visibility.Collapsed;

        string label = string.IsNullOrEmpty(_button.Label) ? FloatingButton.DefaultLabel(_name) : _button.Label;
        Preview.Show(label, _hotkeyText, _button.Size);
        PositionText.Text = _button.X is double x && _button.Y is double y
            ? $"X {Math.Round(x)}, Y {Math.Round(y)}"
            : "Default position (right edge of the screen)";
    }
}
