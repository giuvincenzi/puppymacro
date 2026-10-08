using System;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Adds or edits one macro action. On Save, <see cref="Result"/> holds the action.</summary>
public partial class MacroActionWindow
{
    private readonly LoopEngine _engine;
    private readonly MacroAction _action;
    private int _keyVk;
    private bool _ready;

    internal MacroActionWindow(LoopEngine engine, MacroAction action, bool isNew)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _engine = engine;
        _action = action.Clone();
        _keyVk = _action.Vk;

        string title = (isNew ? "Add " : "Edit ") + TypeName(_action.Type).ToLowerInvariant();
        Title = title;
        ActionTitleBar.Title = title;

        ActionNameBox.Text = _action.Name;

        var t = _action.Type;
        bool isKey = t is MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp;
        bool isButton = t is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp;
        KeyGroup.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
        HoldKeyRow.Visibility = t == MacroActionType.PressKey ? Visibility.Visible : Visibility.Collapsed;
        ButtonGroup.Visibility = isButton ? Visibility.Visible : Visibility.Collapsed;
        ClickOptions.Visibility = t == MacroActionType.Click ? Visibility.Visible : Visibility.Collapsed;
        PositionGroup.Visibility = isButton || t == MacroActionType.MoveTo ? Visibility.Visible : Visibility.Collapsed;
        MoveGroup.Visibility = t == MacroActionType.MoveTo ? Visibility.Visible : Visibility.Collapsed;
        ScrollGroup.Visibility = t == MacroActionType.Scroll ? Visibility.Visible : Visibility.Collapsed;
        TextGroup.Visibility = t == MacroActionType.PasteText ? Visibility.Visible : Visibility.Collapsed;
        RepeatGroup.Visibility = t is MacroActionType.PressKey or MacroActionType.Click ? Visibility.Visible : Visibility.Collapsed;
        RepeatLabel.Text = t == MacroActionType.Click ? "Click" : "Press";
        RepeatBox.Value = Math.Max(1, _action.Repeat);
        RepeatPauseBox.Value = _action.RepeatPauseMs;

        KeyButton.Content = _keyVk == 0 ? "Choose key" : KeyNames.Get(_keyVk);
        KeyHoldBox.Value = _action.HoldMs;
        ClickHoldBox.Value = _action.HoldMs;
        SelectButton(_action.Vk == 0 ? KeyNames.VK_LBUTTON : _action.Vk);
        SingleClickRadio.IsChecked = _action.ClickCount != 2;
        DoubleClickRadio.IsChecked = _action.ClickCount == 2;
        XBox.Value = _action.X;
        YBox.Value = _action.Y;
        InstantRadio.IsChecked = !_action.Smooth;
        SmoothRadio.IsChecked = _action.Smooth;
        SpeedSlider.Value = Math.Clamp(_action.SmoothSpeed, 1, 5);
        DirectionBox.SelectedIndex = (int)_action.ScrollDirection;
        StepsBox.Value = Math.Max(1, _action.ScrollSteps);
        PasteTextBox.Text = _action.Text;
        EnterBeforeBox.IsChecked = _action.EnterBefore;
        EnterAfterBox.IsChecked = _action.EnterAfter;
        DelayBox.Value = _action.DelayMs;

        UpdateSpeedLabel();
        _ready = true;
        Validate();
        Closed += (_, _) => _engine.CancelCapture();
    }

    public MacroAction? Result { get; private set; }

    public static string TypeName(MacroActionType type) => type switch
    {
        MacroActionType.PressKey => "Press key",
        MacroActionType.KeyDown => "Key down",
        MacroActionType.KeyUp => "Key up",
        MacroActionType.Click => "Click",
        MacroActionType.MouseDown => "Mouse button down",
        MacroActionType.MouseUp => "Mouse button up",
        MacroActionType.MoveTo => "Move to",
        MacroActionType.MovePath => "Move path",
        MacroActionType.Scroll => "Scroll",
        MacroActionType.PasteText => "Paste text",
        _ => type.ToString(),
    };

    private void SelectButton(int vk)
    {
        foreach (ComboBoxItem item in ButtonBox.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == vk)
            {
                ButtonBox.SelectedItem = item;
                return;
            }
        }
        ButtonBox.SelectedIndex = 0;
    }

    private void OnChooseKeyClick(object sender, RoutedEventArgs e)
    {
        KeyButton.Content = "Press a key (Esc cancels)";
        _engine.BeginCapture(
            binding =>
            {
                if (KeyNames.IsMouse(binding.Vk))
                {
                    KeyButton.Content = _keyVk == 0 ? "Choose key" : KeyNames.Get(_keyVk);
                    ShowError("Use a Click or Mouse button action for mouse buttons.");
                    return;
                }
                _keyVk = binding.Vk;
                KeyButton.Content = KeyNames.Get(_keyVk);
                Validate();
            },
            () => KeyButton.Content = _keyVk == 0 ? "Choose key" : KeyNames.Get(_keyVk),
            allowPrimaryMouse: false);
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        // The overlay is topmost and covers this dialog, so the dialog does not need to be hidden.
        var picker = new PickPointWindow();
        if (picker.ShowDialog() == true)
        {
            XBox.Value = picker.ResultX;
            YBox.Value = picker.ResultY;
        }
        Activate();
    }

    private void OnMovementChanged(object sender, RoutedEventArgs e)
    {
        if (SpeedRow != null)
            SpeedRow.Visibility = SmoothRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSpeedLabel();

    private void UpdateSpeedLabel()
    {
        if (SpeedLabel == null)
            return;
        int speed = (int)Math.Round(SpeedSlider.Value);
        string name = speed switch { 1 => "very slow", 2 => "slow", 3 => "normal", 4 => "fast", _ => "very fast" };
        double seconds = MacroAction.SmoothDurationMs(speed) / 1000;
        SpeedLabel.Text = $"Speed: {name}, about {seconds:0.##} s";
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Validate();

    private string? GetValidationError()
    {
        var t = _action.Type;
        if ((t is MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp) && _keyVk == 0)
            return "Choose a key.";
        if (t == MacroActionType.PasteText && string.IsNullOrEmpty(PasteTextBox.Text))
            return "Enter the text to paste.";
        return null;
    }

    private void Validate()
    {
        if (!_ready)
            return;
        string? error = GetValidationError();
        if (error == null)
            ErrorText.Visibility = Visibility.Collapsed;
        else
            ShowError(error);
        SaveButton.IsEnabled = error == null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.Focus(); // commit the number field being edited
        if (GetValidationError() != null)
        {
            Validate();
            return;
        }

        var a = _action;
        var t = a.Type;
        a.Name = ActionNameBox.Text.Trim();
        if (t is MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp)
        {
            a.Vk = _keyVk;
            a.HoldMs = KeyHoldBox.Value ?? 30;
        }
        if (t is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp)
        {
            a.Vk = ButtonBox.SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag) : KeyNames.VK_LBUTTON;
            a.ClickCount = DoubleClickRadio.IsChecked == true ? 2 : 1;
            a.HoldMs = ClickHoldBox.Value ?? 30;
        }
        if (t is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo)
        {
            a.X = (int)Math.Round(XBox.Value ?? 0);
            a.Y = (int)Math.Round(YBox.Value ?? 0);
        }
        if (t == MacroActionType.MoveTo)
        {
            a.Smooth = SmoothRadio.IsChecked == true;
            a.SmoothSpeed = (int)Math.Round(SpeedSlider.Value);
        }
        if (t == MacroActionType.Scroll)
        {
            a.ScrollDirection = (ScrollDirection)Math.Max(0, DirectionBox.SelectedIndex);
            a.ScrollSteps = (int)Math.Max(1, Math.Round(StepsBox.Value ?? 1));
        }
        if (t == MacroActionType.PasteText)
        {
            a.Text = PasteTextBox.Text;
            a.EnterBefore = EnterBeforeBox.IsChecked == true;
            a.EnterAfter = EnterAfterBox.IsChecked == true;
        }
        if (t is MacroActionType.PressKey or MacroActionType.Click)
        {
            a.Repeat = (int)Math.Max(1, Math.Round(RepeatBox.Value ?? 1));
            a.RepeatPauseMs = Math.Max(0, Math.Round(RepeatPauseBox.Value ?? 50));
        }
        a.DelayMs = Math.Max(0, Math.Round(DelayBox.Value ?? 0));

        Result = a;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
