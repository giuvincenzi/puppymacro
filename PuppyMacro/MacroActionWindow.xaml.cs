using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// Adds or edits one macro action: its Name and Delay, then the action in the same box as a loop row
/// (<see cref="ActionBox"/>), open. On Save, <see cref="Result"/> holds the action.
/// </summary>
public partial class MacroActionWindow
{
    private readonly LoopEngine _engine;
    private readonly bool _isNew;
    private readonly CodeViewSwitch<MacroAction> _code;
    private MacroAction _action;
    private ActionBoxViewModel _box = null!;
    private bool _ready;

    /// <param name="allowedGroups">The groups the action can be in (<see cref="MacroJson.AllowedGroupIds"/>), for its Code view.</param>
    /// <param name="groupNames">The macro's group names, by id.</param>
    internal MacroActionWindow(LoopEngine engine, MacroAction action, bool isNew,
        IReadOnlyList<Guid?> allowedGroups, IReadOnlyDictionary<Guid, string> groupNames)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _engine = engine;
        KeyCaptureField.SetEngine(this, engine); // the key field waits for keys through it
        _isNew = isNew;
        _action = action.Clone();
        LoadAction(_action);
        // A new Press key, Key down or Key up waits for its key at once, as a loop's new Press key.
        _box.CaptureOnLoad = isNew && _box.IsKeyKind && _box.Key is not { IsSet: true };

        _code = new CodeViewSwitch<MacroAction>(ViewBar, CodeView, FormBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForAction(allowedGroups, groupNames, action.GroupId), FormAction, MacroJson.Serialize,
            (string text, out List<CodeProblem> problems) => MacroJson.ParseAction(text, allowedGroups, groupNames, out problems),
            LoadAction, Validate);
        _code.Opening += () => _engine.CancelCapture();

        _ready = true;
        Validate();
        Closed += (_, _) => _engine.CancelCapture();
    }

    public MacroAction? Result { get; private set; }

    /// <summary>Shows <paramref name="action"/> in the Form view: its Name, Delay and box, open.</summary>
    private void LoadAction(MacroAction action)
    {
        _action = action.Clone();
        // "Add press key"; "Edit click - Open inventory" (its name, or what it does when it has none).
        string type = TypeName(_action.Type).ToLowerInvariant();
        Title = _isNew ? "Add " + type : $"Edit {type} - {new MacroActionRowViewModel(_action).Title}";

        ActionNameBox.Text = _action.Name;
        DelayBox.Value = _action.DelayMs;
        if (_box != null)
            _box.PropertyChanged -= OnBoxChanged;
        _box = new ActionBoxViewModel(_action) { IsExpanded = true };
        _box.PropertyChanged += OnBoxChanged;
        Box.DataContext = _box;
        Validate();
    }

    private void OnBoxChanged(object? sender, PropertyChangedEventArgs e) => Validate();

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

    private string? GetValidationError() => _box.Problem;

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
        var a = _box.ToMacroAction(_action);
        a.Name = ActionNameBox.Text.Trim();
        a.DelayMs = double.IsNaN(DelayBox.Value) ? 0 : Math.Max(0, Math.Round(DelayBox.Value));
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
