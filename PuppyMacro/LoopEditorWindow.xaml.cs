using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Add or edit a loop. On Save, <see cref="Result"/> holds the edited copy.</summary>
public partial class LoopEditorWindow
{
    private readonly LoopEngine _engine;
    private readonly MacroLibrary _macros;
    private readonly SoundService _sounds;
    private string _soundName;
    private readonly AppSettings _settings;
    private readonly Guid _editingId;
    private readonly Guid _itemId;
    private readonly Func<EditedFloatingButton, Point?>? _placeButton;
    private readonly CodeViewSwitch<LoopDefinition> _code;
    private readonly ObservableCollection<ActionBoxViewModel> _actions = new();
    private HotkeyBinding? _hotkey;
    private bool _ready;

    internal LoopEditorWindow(LoopEngine engine, AppSettings settings, MacroLibrary macros, SoundService sounds, LoopDefinition? existing,
        Func<EditedFloatingButton, Point?>? placeButton = null)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _engine = engine;
        _settings = settings;
        _macros = macros;
        _sounds = sounds;

        var source = existing?.Clone() ?? new LoopDefinition();
        _editingId = existing?.Id ?? Guid.Empty;
        _itemId = existing?.Id ?? Guid.NewGuid();
        source.Id = _itemId;
        _placeButton = placeButton;

        string title = existing == null
            ? Strings.LoopEditor_AddTitle
            : string.Format(CultureInfo.CurrentCulture, Strings.LoopEditor_EditNamedTitle, existing.Name);
        Title = title;

        _soundName = source.SoundName;
        // The sounds by their names in the user's language; the id (the file's value) is the same position in SoundService.Names.
        SoundChoices.ItemsSource = SoundService.DisplayNames();
        SoundChoices.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnSoundClick), handledEventsToo: true);
        SoundChoicesScroll.Attach(SoundScroll, () => SoundService.DisplayName(_soundName));
        _actions.CollectionChanged += OnActionsChanged;
        ActionsList.ItemsSource = _actions;
        LoadLoop(source);
        FloatingEditor.PositionRequested += OnFloatingPositionRequested;
        // A floating button shows only for an item shown in the overlay.
        OverlayOptions.ShowChanged += () => FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        KeyCaptureField.SetEngine(this, _engine); // every key field in the window waits for keys through it

        _code = new CodeViewSwitch<LoopDefinition>(ViewBar, CodeView, FormBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForLoop(_itemId), FormLoop, LoopJson.Serialize,
            (string text, out List<CodeProblem> problems) => LoopJson.Parse(text, _itemId, HotkeyConflict, out problems),
            LoadLoop, Validate);
        _code.Opening += () =>
        {
            _engine.CancelCapture();
            UpdateHotkeyLabel();
        };
        _ready = true;
        Validate();

        Closed += (_, _) => _engine.CancelCapture();
    }

    public LoopDefinition? Result { get; private set; }

    /// <summary>Offers the History view of a loop saved before.</summary>
    internal void EnableHistory(ItemHistory history)
    {
        if (_editingId == Guid.Empty)
            return;
        _code.EnableHistory(HistoryPanel, () => HistoryPanel.Load(history.Versions(_itemId)));
        HistoryPanel.RestoreRequested += version =>
        {
            if (HistoryRestore.Read<LoopDefinition>(version, _itemId) is not { } loop)
                return;
            LoadLoop(loop);
            _code.ShowFormView();
            HistoryRestore.Notice(NoticeBar, version);
        };
    }

    /// <summary>Shows <paramref name="source"/> in the Form view (at start, and when leaving the Code view).</summary>
    private void LoadLoop(LoopDefinition source)
    {
        EnabledSwitch.IsOn = source.Enabled;
        _hotkey = source.Hotkey?.Clone();
        _soundName = source.SoundName;
        SoundSwitch.IsOn = source.SoundEnabled;
        SoundCard.IsEnabled = source.SoundEnabled;
        SoundExpander.IsExpanded = source.SoundEnabled;
        SoundChoices.SelectedIndex = SoundService.Names.ToList().IndexOf(_soundName);

        NameBox.Text = source.Name;
        foreach (var row in _actions)
            row.PropertyChanged -= OnRowChanged;
        _actions.Clear();
        foreach (var action in source.Actions)
            AddRow(new ActionBoxViewModel(action));

        ToggleRadio.IsChecked = source.Mode == ActivationMode.Toggle;
        HoldRadio.IsChecked = source.Mode == ActivationMode.Hold;

        UpdateHotkeyLabel();
        FloatingEditor.Load(source.FloatingButton, source.Name, HotkeyText(), source.Mode == ActivationMode.Hold);
        AppScopeCard.Load(source.AppExe);
        OverlayOptions.Load(source.ShowInOverlay, source.HideInOverlayWhenDisabled, source.OverlayClickPassesThrough);
        Validate();
    }

    /// <summary>The loop as the Form view's fields describe it, also when they are not valid yet.</summary>
    private LoopDefinition FormLoop() => new()
    {
        Id = _itemId,
        Name = NameBox.Text.Trim(),
        Actions = _actions.Select(a => a.ToLoopAction()).ToList(),
        Mode = HoldRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle,
        Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
        Enabled = EnabledSwitch.IsOn,
        SoundEnabled = SoundSwitch.IsOn,
        SoundName = _soundName,
        FloatingButton = FloatingEditor.ToModel(),
        AppExe = AppScopeCard.AppExe,
        ShowInOverlay = OverlayOptions.ShowInOverlay,
        HideInOverlayWhenDisabled = OverlayOptions.HideWhenDisabled,
        OverlayClickPassesThrough = OverlayOptions.ClickPassesThrough,
    };

    /// <summary>The app chosen in Specific app; null for all apps (also while none is chosen yet).</summary>
    private string? SelectedApp => AppScopeCard.AppExe is { Length: > 0 } app ? app : null;

    private string? HotkeyConflict(HotkeyBinding binding, string? appExe) =>
        HotkeyConflicts.Find(binding, _settings, _macros, _editingId, appExe: appExe);

    /// <summary>
    /// Specific app is on with no app chosen: when the hotkey is taken for all apps, turning off Specific app would not
    /// help, so the error names what uses it.
    /// </summary>
    private string NoAppError(string appError) =>
        _hotkey is { IsSet: true } && HotkeyConflict(_hotkey, null) is string conflict
            ? string.Format(CultureInfo.CurrentCulture, Strings.AppScope_NoAppHotkeyUsed, conflict)
            : appError;

    private void OnAppScopeChanged(object? sender, EventArgs e) => Validate();

    // ================= Rows =================

    private void AddRow(ActionBoxViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        _actions.Add(row);
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Validate();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    // One button per action in the bar. A new row opens; a Press key's key field waits for the key at once.
    private void OnAddPressKeyClick(object sender, RoutedEventArgs e) => AddRow(ActionBoxViewModel.NewLoopRow(ActionKind.PressKey));

    private void OnAddClickClick(object sender, RoutedEventArgs e) => AddRow(ActionBoxViewModel.NewLoopRow(ActionKind.Click));

    private void OnAddPasteTextClick(object sender, RoutedEventArgs e) => AddRow(ActionBoxViewModel.NewLoopRow(ActionKind.PasteText));

    private void OnRemoveAction(ActionBox box)
    {
        if (box.Action is not { } row)
            return;
        row.PropertyChanged -= OnRowChanged; // its key field stops waiting when it goes away
        _actions.Remove(row);
    }

    /// <summary>A key a row's key field did not keep (a mouse button for Press key).</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ================= Sound =================

    private void OnSoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoundChoices.SelectedIndex >= 0)
            _soundName = SoundService.Names[SoundChoices.SelectedIndex];
    }

    /// <summary>A click on a sound picks it and plays it, also when it is already the chosen one.</summary>
    private void OnSoundClick(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as RadioButton)?.Content is not string shown || SoundService.IdOf(shown) is not string name)
            return;
        _soundName = name;
        _sounds.Preview(name);
    }

    private void OnSoundSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (SoundCard == null)
            return;
        SoundCard.IsEnabled = SoundSwitch.IsOn;
        SoundExpander.IsExpanded = SoundSwitch.IsOn;
    }

    // ================= Hotkey =================

    /// <summary>The hotkey field's Set or Clear (left/right click and the wheel only with a modifier).</summary>
    private void OnHotkeyFieldChanged(object? sender, EventArgs e)
    {
        _hotkey = HotkeyField.Value?.Clone();
        UpdateHotkeyLabel();
        Validate();
    }

    private void UpdateHotkeyLabel()
    {
        HotkeyField.Value = _hotkey?.Clone();
        FloatingEditor.SetHotkey(HotkeyText());
    }

    private string HotkeyText() => _hotkey is { IsSet: true } ? KeyNames.Format(_hotkey) : "";

    // ================= Floating button =================

    private void OnFloatingPositionRequested()
    {
        _engine.CancelCapture();
        var edited = new EditedFloatingButton(_itemId, NameBox.Text.Trim(), HotkeyText(), FloatingEditor.ToModel());
        if (_placeButton?.Invoke(edited) is Point position)
            FloatingEditor.SetPosition(position);
    }

    // ================= Validation =================

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        FloatingEditor?.SetName(NameBox.Text);
        Validate();
    }

    private void OnActivationChanged(object sender, RoutedEventArgs e)
    {
        FloatingEditor?.SetHoldMode(HoldRadio.IsChecked == true);
        Validate();
        ActivationHint.Text = HoldRadio.IsChecked == true
            ? Strings.LoopEditor_HoldHint
            : Strings.Editor_ToggleHint;
    }

    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            return Strings.Editor_EnterName;
        if (_actions.Count == 0)
            return Strings.LoopEditor_NoActionsError;
        if (_actions.Select(a => a.Problem).FirstOrDefault(p => p != null) is string rowProblem)
            return rowProblem;
        // Written so that an empty interval (NaN) fails too.
        if (_actions.Any(a => a.ShowEvery && !(a.IntervalMs >= LoopAction.MinIntervalMs && a.IntervalMs <= LoopAction.MaxIntervalMs)))
            return Strings.LoopEditor_IntervalError;
        if (AppScopeCard.ValidationError is string appError)
            return NoAppError(appError);

        if (_hotkey == null || !_hotkey.IsSet)
            return HoldRadio.IsChecked == true ? Strings.Editor_HoldNeedsHotkey : null;
        if (HotkeyRules.Problem(_hotkey, hold: HoldRadio.IsChecked == true) is string problem)
            return problem;
        if (LoopJson.HotkeyIsPressed(_hotkey, _actions.Select(a => a.ToLoopAction())))
            return Strings.LoopEditor_HotkeyIsPressed;

        return HotkeyConflict(_hotkey, SelectedApp);
    }

    private void Validate()
    {
        if (!_ready || _code.ValidateCode())
            return;

        string? error = GetValidationError();
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error == null;
    }

    // ================= Buttons =================

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Move focus off the field being edited so its last typed value is committed.
        SaveButton.Focus();

        if (_code.IsCode)
        {
            if (_code.Read() is { } fromCode)
            {
                Result = fromCode;
                DialogResult = true;
            }
            return;
        }

        if (GetValidationError() != null)
        {
            Validate();
            return;
        }

        Result = FormLoop();
        DialogResult = true;
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

/// <summary>The interval's spin buttons: 10 ms steps, or 1 s / min / h (UnitIndex 0 is ms).</summary>
public sealed class IntervalStepConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int unitIndex && unitIndex == 0 ? 10.0 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
