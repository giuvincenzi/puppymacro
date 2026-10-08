using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private readonly bool _wasEnabled;
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
        _soundName = source.SoundName;
        BuildSoundTiles();
        SoundSwitch.IsChecked = source.SoundEnabled;
        SoundPanel.Visibility = source.SoundEnabled ? Visibility.Visible : Visibility.Collapsed;
        _editingId = existing?.Id ?? Guid.Empty;
        _itemId = existing?.Id ?? Guid.NewGuid();
        _placeButton = placeButton;
        _wasEnabled = source.Enabled;
        _hotkey = source.Hotkey?.Clone();

        string title = existing == null ? "Add loop" : "Edit loop";
        Title = title;
        EditorTitleBar.Title = title;

        NameBox.Text = source.Name;
        foreach (var action in source.Actions)
            AddRow(new ActionEditorViewModel(action));
        _actions.CollectionChanged += OnActionsChanged;
        ActionsList.ItemsSource = _actions;

        ToggleRadio.IsChecked = source.Mode == ActivationMode.Toggle;
        HoldRadio.IsChecked = source.Mode == ActivationMode.Hold;

        UpdateHotkeyLabel();
        FloatingEditor.Load(source.FloatingButton, source.Name, HotkeyText(), source.Mode == ActivationMode.Hold);
        FloatingEditor.PositionRequested += OnFloatingPositionRequested;
        _ready = true;
        Validate();

        Closed += (_, _) => _engine.CancelCapture();
    }

    public LoopDefinition? Result { get; private set; }

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
        var row = new ActionEditorViewModel(new LoopAction
        {
            Type = ActionType.Key,
            IntervalValue = NewKeyIntervalMs,
            IntervalUnit = IntervalUnit.Milliseconds,
        });
        AddRow(row);
        StartKeyCapture(row);
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
        if (row.IsCapturing)
            _engine.CancelCapture();
        row.PropertyChanged -= OnRowChanged;
        _actions.Remove(row);
    }

    private void OnStepUpClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ActionEditorViewModel row)
            row.Step(+1);
    }

    private void OnStepDownClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ActionEditorViewModel row)
            row.Step(-1);
    }

    private void OnCaptureKeyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ActionEditorViewModel row)
            StartKeyCapture(row);
    }

    private void StartKeyCapture(ActionEditorViewModel row)
    {
        EndAllCaptures();
        row.IsCapturing = true;
        _engine.BeginCapture(
            binding =>
            {
                row.IsCapturing = false;
                row.KeyVk = binding.Vk; // modifiers held while choosing are ignored here
            },
            () => row.IsCapturing = false,
            allowPrimaryMouse: true); // left and right click can be repeated
    }

    // ================= Sound =================

    private void BuildSoundTiles()
    {
        var style = (Style)FindResource("SoundTile");
        foreach (string name in SoundService.Names)
        {
            var tile = new RadioButton
            {
                Content = name,
                Tag = name,
                GroupName = "Sound",
                Style = style,
                Margin = new Thickness(0, 0, 6, 6),
                IsChecked = name == _soundName,
            };
            tile.Click += OnSoundTileClick;
            SoundGrid.Children.Add(tile);
        }
    }

    private void OnSoundTileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name)
            return;
        _soundName = name;
        _sounds.Preview(name);
    }

    private void OnSoundSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (SoundPanel != null)
            SoundPanel.Visibility = SoundSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Hotkey =================

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        EndAllCaptures();
        HotkeyCaps.Visibility = Visibility.Collapsed;
        HotkeyCaptureText.Visibility = Visibility.Visible;

        _engine.BeginCapture(
            binding =>
            {
                _hotkey = binding;
                UpdateHotkeyLabel();
                Validate();
            },
            UpdateHotkeyLabel,
            allowPrimaryMouse: false, hotkey: true); // left/right click and the wheel only with a modifier
    }

    private void UpdateHotkeyLabel()
    {
        HotkeyCaptureText.Visibility = Visibility.Collapsed;
        HotkeyCaps.Visibility = Visibility.Visible;
        HotkeyCaps.ItemsSource = KeyNames.Parts(_hotkey);
        ClearHotkeyButton.IsEnabled = _hotkey is { IsSet: true };
        FloatingEditor.SetHotkey(HotkeyText());
    }

    private string HotkeyText() => _hotkey is { IsSet: true } ? KeyNames.Format(_hotkey) : "";

    // ================= Floating button =================

    private void OnFloatingPositionRequested()
    {
        EndAllCaptures();
        var edited = new EditedFloatingButton(_itemId, NameBox.Text.Trim(), HotkeyText(), FloatingEditor.ToModel());
        if (_placeButton?.Invoke(edited) is Point position)
            FloatingEditor.SetPosition(position);
    }

    private void EndAllCaptures()
    {
        _engine.CancelCapture();
        foreach (var row in _actions)
            row.IsCapturing = false;
        UpdateHotkeyLabel();
    }

    // ================= Validation =================

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        FloatingEditor?.SetName(NameBox.Text);
        Validate();
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
        _hotkey = null;
        UpdateHotkeyLabel();
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
        if (_actions.Any(a => a.ShowInterval && (a.IntervalMs < LoopAction.MinIntervalMs || a.IntervalMs > LoopAction.MaxIntervalMs)))
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
        if (!_ready)
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

        if (GetValidationError() != null)
        {
            Validate();
            return;
        }

        Result = new LoopDefinition
        {
            Id = _itemId,
            Name = NameBox.Text.Trim(),
            Actions = _actions.Select(a => a.ToAction()).ToList(),
            Mode = HoldRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle,
            Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
            Enabled = _wasEnabled,
            SoundEnabled = SoundSwitch.IsChecked == true,
            SoundName = _soundName,
            FloatingButton = FloatingEditor.ToModel(),
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
