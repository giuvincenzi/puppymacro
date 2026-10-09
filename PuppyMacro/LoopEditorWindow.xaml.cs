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
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Add or edit a loop. On Save, <see cref="Result"/> holds the edited copy.</summary>
public partial class LoopEditorWindow
{
    private const double NewKeyIntervalMs = 40;
    private const double NewTextIntervalSeconds = 5;

    private readonly LoopEngine _engine;
    private readonly MacroLibrary _macros;
    private readonly SoundService _sounds;
    private string _soundName;
    private readonly AppSettings _settings;
    private readonly Guid _editingId;
    private readonly Guid _itemId;
    private readonly Func<EditedFloatingButton, Point?>? _placeButton;
    private readonly CodeViewSwitch<LoopDefinition> _code;
    private bool _enabled;
    private readonly ObservableCollection<ActionEditorViewModel> _actions = new();
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

        string title = existing == null ? "Add loop" : "Edit loop";
        Title = title;

        _soundName = source.SoundName;
        SoundChoices.ItemsSource = SoundService.Names;
        SoundChoices.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnSoundClick), handledEventsToo: true);
        SoundChoicesScroll.Attach(SoundScroll, () => _soundName);
        _actions.CollectionChanged += OnActionsChanged;
        ActionsList.ItemsSource = _actions;
        LoadLoop(source);
        FloatingEditor.PositionRequested += OnFloatingPositionRequested;
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

    /// <summary>Shows <paramref name="source"/> in the Form view (at start, and when leaving the Code view).</summary>
    private void LoadLoop(LoopDefinition source)
    {
        _enabled = source.Enabled;
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
            AddRow(new ActionEditorViewModel(action));

        ToggleRadio.IsChecked = source.Mode == ActivationMode.Toggle;
        HoldRadio.IsChecked = source.Mode == ActivationMode.Hold;

        UpdateHotkeyLabel();
        FloatingEditor.Load(source.FloatingButton, source.Name, HotkeyText(), source.Mode == ActivationMode.Hold);
        Validate();
    }

    /// <summary>The loop as the Form view's fields describe it, also when they are not valid yet.</summary>
    private LoopDefinition FormLoop() => new()
    {
        Id = _itemId,
        Name = NameBox.Text.Trim(),
        Actions = _actions.Select(a => a.ToAction()).ToList(),
        Mode = HoldRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle,
        Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
        Enabled = _enabled,
        SoundEnabled = SoundSwitch.IsOn,
        SoundName = _soundName,
        FloatingButton = FloatingEditor.ToModel(),
    };

    private string? HotkeyConflict(HotkeyBinding binding) => HotkeyConflicts.Find(binding, _settings, _macros, _editingId);

    // ================= Rows =================

    private void AddRow(ActionEditorViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        _actions.Add(row);
    }

    private void OnActionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Validate();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    private void OnAddKeyClick(object sender, RoutedEventArgs e)
    {
        // The new row opens and its key field waits for the key.
        AddRow(new ActionEditorViewModel(new LoopAction
        {
            Type = ActionType.Key,
            IntervalValue = NewKeyIntervalMs,
            IntervalUnit = IntervalUnit.Milliseconds,
        })
        {
            IsExpanded = true,
            CaptureOnLoad = true,
        });
    }

    private void OnAddTextClick(object sender, RoutedEventArgs e)
    {
        AddRow(new ActionEditorViewModel(new LoopAction
        {
            Type = ActionType.Text,
            IntervalValue = NewTextIntervalSeconds,
            IntervalUnit = IntervalUnit.Seconds,
        }));
    }

    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ActionEditorViewModel row)
            return;
        row.PropertyChanged -= OnRowChanged; // its key field stops waiting when it goes away
        _actions.Remove(row);
    }

    // ================= Sound =================

    private void OnSoundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoundChoices.SelectedItem is string name)
            _soundName = name;
    }

    /// <summary>A click on a sound picks it and plays it, also when it is already the chosen one.</summary>
    private void OnSoundClick(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as RadioButton)?.Content is not string name)
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
            ? "Runs only while the hotkey is held down."
            : "Press the hotkey to start, press it again to stop.";
    }

    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            return "Enter a name.";
        if (_actions.Count == 0)
            return "Add at least one key or text.";
        if (_actions.Any(a => a.IsKey && a.KeyVk == 0))
            return "Choose the key for every key row.";
        if (_actions.Any(a => a.IsText && string.IsNullOrEmpty(a.Text)))
            return "Enter the text for every text row.";
        // Written so that an empty interval (NaN) fails too.
        if (_actions.Any(a => a.ShowInterval && !(a.IntervalMs >= LoopAction.MinIntervalMs && a.IntervalMs <= LoopAction.MaxIntervalMs)))
            return "Every interval must be between 10 ms and 24 h.";

        if (_hotkey == null || !_hotkey.IsSet)
            return HoldRadio.IsChecked == true ? "Hold needs a hotkey. Choose one or switch to Toggle." : null;
        if (HotkeyRules.Problem(_hotkey, hold: HoldRadio.IsChecked == true) is string problem)
            return problem;
        if (!_hotkey.HasModifiers && _actions.Any(a => a.IsKey && a.KeyVk == _hotkey.Vk))
            return "The hotkey cannot be one of the keys this loop presses.";

        return HotkeyConflicts.Find(_hotkey, _settings, _macros, _editingId);
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
