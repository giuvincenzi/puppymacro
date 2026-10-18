using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
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
    private IfViewModel? _if;
    private readonly IReadOnlyDictionary<Guid, string> _found;
    private readonly ImageStore? _images;
    private readonly IReadOnlyCollection<string> _otherConditionNames;
    private readonly MacroLibrary? _macros;
    private readonly IReadOnlyList<ComponentParameter>? _parameters;
    private SlotBindings _delay = SlotBindings.None;
    private bool _ready;

    /// <param name="allowedGroups">The groups the action can be in (<see cref="MacroJson.AllowedGroupIds"/>), for its Code view.</param>
    /// <param name="groupNames">The macro's group names, by id.</param>
    /// <param name="found">The conditions whose image or text the action can go to (<see cref="MacroJson.FoundAt"/>).</param>
    /// <param name="images">The conditions' images.</param>
    /// <param name="device">Where an If's images are searched while editing (Settings › Screen recognition).</param>
    /// <param name="macros">The macros and the Library, for an If's Component conditions; null without them.</param>
    /// <param name="parameters">Inside a component: its parameters, whose values its fields can take; null in a macro.</param>
    internal MacroActionWindow(LoopEngine engine, MacroAction action, bool isNew,
        IReadOnlyList<Guid?> allowedGroups, IReadOnlyDictionary<Guid, string> groupNames,
        IReadOnlyDictionary<Guid, string>? found = null, ImageStore? images = null, RecognitionDevice device = RecognitionDevice.Automatic,
        IReadOnlyCollection<string>? otherConditionNames = null, MacroLibrary? macros = null, IReadOnlyList<ComponentParameter>? parameters = null)
    {
        _otherConditionNames = otherConditionNames ?? Array.Empty<string>();
        _macros = macros;
        _parameters = parameters;
        InitializeComponent();
        // An If's window holds its conditions and when its Then runs: it opens taller (WindowFit keeps it on the screen).
        if (action.Type == MacroActionType.If)
            Height = 1000;
        WindowFit.Apply(this);
        _engine = engine;
        _found = found ?? new Dictionary<Guid, string>();
        _images = images;
        Conditions.Device = device;
        Conditions.Changed += Validate;
        KeyCaptureField.SetEngine(this, engine); // the key field waits for keys through it
        _isNew = isNew;
        _action = action.Clone();
        LoadAction(_action);
        // A new Press key, Key down or Key up waits for its key at once, as a loop's new Press key.
        _box.CaptureOnLoad = isNew && _box.IsKeyKind && _box.Key is not { IsSet: true };

        _code = new CodeViewSwitch<MacroAction>(ViewBar, CodeView, FormBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForAction(allowedGroups, groupNames, action.GroupId), FormAction, MacroJson.Serialize,
            (string text, out List<CodeProblem> problems) => MacroJson.ParseAction(text, allowedGroups, groupNames, out problems,
                _found, images == null ? null : images.Has, macros?.Library.Signatures(), parameters),
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
        Title = _isNew
            ? Loc.Text($"ActionWindow_Add_{_action.Type}")
            : string.Format(CultureInfo.CurrentCulture, Loc.Text($"ActionWindow_Edit_{_action.Type}"),
                new MacroActionRowViewModel(_action).Title);

        ActionNameBox.Text = _action.Name;
        DelayBox.Value = _action.DelayMs;
        // Inside a component: the Delay from a parameter (a list before it, as the If's Wait card).
        _delay = new SlotBindings(ComponentSlots.Of(_action).Where(s => s.Name == ComponentSlots.DelayMs), _parameters, _action.Bind);
        DelayFrom.DataContext = _delay;
        _delay.DelayMs.PropertyChanged += (_, _) => Validate();
        if (_box != null)
            _box.PropertyChanged -= OnBoxChanged;
        _box = new ActionBoxViewModel(_action) { IsExpanded = true };
        _box.SetFounds(_found);
        if (_parameters != null)
            _box.SetParameters(_parameters, _action);
        _box.PropertyChanged += OnBoxChanged;
        Box.DataContext = _box;

        // An If shows its conditions instead of the box.
        bool isIf = _action.Type == MacroActionType.If;
        Box.Visibility = isIf ? Visibility.Collapsed : Visibility.Visible;
        Conditions.Visibility = isIf ? Visibility.Visible : Visibility.Collapsed;
        if (_if != null)
            _if.PropertyChanged -= OnIfChanged;
        var context = new ConditionContext { Macros = _macros, Parameters = _parameters, Found = _found };
        _if = isIf ? new IfViewModel(_action.If ?? new MacroIf(), _images, context, _action.Bind) { OtherNames = _otherConditionNames } : null;
        if (_if != null)
        {
            _if.PropertyChanged += OnIfChanged;
            _if.ConditionChanged += _ => Validate();
            _if.Conditions.CollectionChanged += (_, _) => Validate();
            TestButton.ToolTip = Strings.Condition_TestTooltip;
        }
        Conditions.DataContext = _if;
        Validate();
    }

    private void OnBoxChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    private void OnIfChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    public static string TypeName(MacroActionType type) => type switch
    {
        MacroActionType.PressKey => Strings.ActionWindow_Type_PressKey,
        MacroActionType.KeyDown => Strings.ActionWindow_Type_KeyDown,
        MacroActionType.KeyUp => Strings.ActionWindow_Type_KeyUp,
        MacroActionType.Click => Strings.ActionWindow_Type_Click,
        MacroActionType.MouseDown => Strings.ActionWindow_Type_MouseDown,
        MacroActionType.MouseUp => Strings.ActionWindow_Type_MouseUp,
        MacroActionType.MoveTo => Strings.ActionWindow_Type_MoveTo,
        MacroActionType.MovePath => Strings.ActionWindow_Type_MovePath,
        MacroActionType.Scroll => Strings.ActionWindow_Type_Scroll,
        MacroActionType.PasteText => Strings.ActionWindow_Type_PasteText,
        MacroActionType.If => Strings.Macro_If,
        _ => type.ToString(),
    };

    private string? GetValidationError() => _if != null ? _if.Problem : _box.Problem;

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
        TextNotice.IsOpen = _if?.HasText == true;
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
        if (_if != null)
        {
            // An If is checked on the screen, not played: its Then would send input to the window behind.
            if (!_code.IsCode)
                _ = Conditions.CheckAllAsync();
            return;
        }
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
        var a = _if != null ? _action.Clone() : _box.ToMacroAction(_action);
        if (_if != null)
            a.If = _if.ToModel(_action.If ?? new MacroIf());
        a.Name = ActionNameBox.Text.Trim();
        a.DelayMs = double.IsNaN(DelayBox.Value) ? 0 : Math.Max(0, Math.Round(DelayBox.Value));
        // The fields its component's parameters give: the box's (or the If's wait), and the Delay.
        var bind = (_if != null ? _if.WaitBind() : a.Bind)?.Where(b => b.Key != ComponentSlots.DelayMs).ToList()
                   ?? new List<KeyValuePair<string, string>>();
        a.Bind = _delay.ToBind(ComponentSlots.Of(a).Where(s => s.Name == ComponentSlots.DelayMs), bind);
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
