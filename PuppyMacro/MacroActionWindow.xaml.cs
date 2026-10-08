using System;
using System.Collections.Generic;
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
    private readonly bool _isNew;
    private readonly CodeViewSwitch<MacroAction> _code;
    private MacroAction _action;
    private int _keyVk;
    private bool _ready;

    /// <param name="allowedGroups">The groups the action can be in (<see cref="MacroJson.AllowedGroupIds"/>), for its Code view.</param>
    /// <param name="groupNames">The macro's group names, by id.</param>
    internal MacroActionWindow(LoopEngine engine, MacroAction action, bool isNew,
        IReadOnlyList<Guid?> allowedGroups, IReadOnlyDictionary<Guid, string> groupNames)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _engine = engine;
        _isNew = isNew;
        _action = action.Clone();
        LoadAction(_action);

        _code = new CodeViewSwitch<MacroAction>(this, ViewBar, CodeView, FormBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForAction(allowedGroups, groupNames, action.GroupId), FormAction, MacroJson.Serialize,
            (string text, out List<CodeProblem> problems) => MacroJson.ParseAction(text, allowedGroups, groupNames, out problems),
            LoadAction, Validate, new Size(760, 620));
        _code.Opening += () => _engine.CancelCapture();

        _ready = true;
        Validate();
        Closed += (_, _) => _engine.CancelCapture();
    }

    public MacroAction? Result { get; private set; }

    /// <summary>Shows <paramref name="action"/> in the Form view: the fields of its type, with its values.</summary>
    private void LoadAction(MacroAction action)
    {
        _action = action.Clone();
        _keyVk = _action.Vk;

        string title = (_isNew ? "Add " : "Edit ") + TypeName(_action.Type).ToLowerInvariant();
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
        Validate();
    }

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
        if (_code.ValidateCode())
        {
            TestButton.IsEnabled = SaveButton.IsEnabled;
            return;
        }
        string? error = GetValidationError();
        if (error == null)
            ErrorText.Visibility = Visibility.Collapsed;
        else
            ShowError(error);
        SaveButton.IsEnabled = error == null;
        TestButton.IsEnabled = error == null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_code.IsCode)
        {
            if (_code.Read() is { } fromCode)
            {
                Result = fromCode;
                DialogResult = true;
            }
            return;
        }
        if (BuildAction(SaveButton) is { } action)
        {
            Result = action;
            DialogResult = true;
        }
    }

    /// <summary>Plays the action as it is now in the window, also before it is saved.</summary>
    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if ((_code.IsCode ? _code.Read() : BuildAction(TestButton)) is { } action)
            ActionTestSession.Run(_engine, action, this);
    }

    /// <summary>The action with the values in the window, or null (and the error shown) when they are not valid.</summary>
    private MacroAction? BuildAction(UIElement clicked)
    {
        clicked.Focus(); // commit the number field being edited
        if (GetValidationError() != null)
        {
            Validate();
            return null;
        }
        return FormAction();
    }

    /// <summary>The action as the Form view's fields describe it, also when they are not valid yet.</summary>
    private MacroAction FormAction()
    {
        var a = _action.Clone();
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
        return a;
    }

    /// <summary>Form view: closes without saving. Code view: Discard changes (see <see cref="CodeViewSwitch{T}"/>).</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_code.IsCode)
            _code.Discard();
        else
            DialogResult = false;
    }
}
