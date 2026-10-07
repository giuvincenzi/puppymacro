using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Creates or edits a macro. On Save, <see cref="Result"/> holds the edited copy.</summary>
public partial class MacroEditorWindow
{
    private readonly LoopEngine _engine;
    private readonly AppSettings _settings;
    private readonly MacroLibrary _macros;
    private readonly SoundService _sounds;
    private readonly Guid _editingId;
    private readonly bool _wasEnabled;
    private readonly ObservableCollection<MacroActionRowViewModel> _rows = new();
    private HotkeyBinding? _hotkey;
    private string _soundName;
    private bool _ready;
    private Point _dragStart;
    private MacroActionRowViewModel? _dragCandidate;

    internal MacroEditorWindow(LoopEngine engine, AppSettings settings, MacroLibrary macros, SoundService sounds,
        MacroDefinition? existing, IEnumerable<MacroAction>? recorded = null)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _macros = macros;
        _sounds = sounds;

        var source = existing?.Clone() ?? new MacroDefinition();
        _editingId = existing?.Id ?? Guid.Empty;
        _wasEnabled = source.Enabled;
        _hotkey = source.Hotkey?.Clone();
        _soundName = source.SoundName;

        string title = existing == null ? "New macro" : "Edit macro";
        Title = title;
        EditorTitleBar.Title = title;

        NameBox.Text = source.Name;
        foreach (var action in source.Actions)
            _rows.Add(new MacroActionRowViewModel(action));
        if (recorded != null)
        {
            foreach (var action in recorded)
                _rows.Add(new MacroActionRowViewModel(action));
        }
        ActionList.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => { Renumber(); Validate(); };
        Renumber();

        OnceRadio.IsChecked = source.Repeat == RepeatMode.Once;
        LoopRadio.IsChecked = source.Repeat == RepeatMode.Loop;
        TimesRadio.IsChecked = source.Repeat == RepeatMode.Times;
        TimesBox.Value = source.RepeatCount;
        TimesBox.IsEnabled = source.Repeat == RepeatMode.Times;
        int speedIndex = Array.IndexOf(MacroDefinition.SpeedSteps, source.Speed);
        SpeedSlider.Value = speedIndex >= 0 ? speedIndex : 2;
        UpdateSpeedLabel();

        ToggleRadio.IsChecked = source.Mode == ActivationMode.Toggle;
        HoldRadio.IsChecked = source.Mode == ActivationMode.Hold;

        BuildSoundTiles();
        SoundSwitch.IsChecked = source.SoundEnabled;
        SoundPanel.Visibility = source.SoundEnabled ? Visibility.Visible : Visibility.Collapsed;

        UpdateHotkeyLabel();
        UpdateInsertHint();
        _ready = true;
        Validate();
        Closed += (_, _) => _engine.CancelCapture();
        Loaded += (_, _) => Activate(); // e.g. right after a recording, with the main window just restored
    }

    public MacroDefinition? Result { get; private set; }

    // ================= Rows =================

    private void Renumber()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Number = i + 1;
        EmptyActionsText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        double total = _rows.Sum(r => r.Action.DelayMs + r.Action.DurationMs);
        TotalText.Text = $"{_rows.Count} {(_rows.Count == 1 ? "action" : "actions")}, {MacroItemViewModel.FormatDuration(total)} at ×1";
        UpdateInsertHint();
    }

    /// <summary>Index where new actions go: after the selected row, or at the end.</summary>
    private int InsertIndex => ActionList.SelectedIndex >= 0 ? ActionList.SelectedIndex + 1 : _rows.Count;

    private void UpdateInsertHint()
    {
        if (InsertHint == null)
            return;
        InsertHint.Text = ActionList.SelectedIndex >= 0
            ? $"New actions go after #{ActionList.SelectedIndex + 1}"
            : "New actions go at the end";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInsertHint();

    private void InsertRows(IEnumerable<MacroAction> actions)
    {
        int index = InsertIndex;
        MacroActionRowViewModel? last = null;
        foreach (var action in actions)
        {
            last = new MacroActionRowViewModel(action);
            _rows.Insert(index++, last);
        }
        if (last != null)
        {
            ActionList.SelectedItem = last;
            ActionList.ScrollIntoView(last);
        }
    }

    private void OnAddActionClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddActionButton, Placement = PlacementMode.Bottom };
        foreach (MacroActionType type in new[]
                 {
                     MacroActionType.PressKey, MacroActionType.KeyDown, MacroActionType.KeyUp,
                     MacroActionType.Click, MacroActionType.MouseDown, MacroActionType.MouseUp,
                     MacroActionType.MoveTo, MacroActionType.Scroll, MacroActionType.PasteText,
                 })
        {
            var item = new MenuItem { Header = MacroActionWindow.TypeName(type) };
            item.Click += (_, _) => AddAction(type);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddAction(MacroActionType type)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var action = new MacroAction
        {
            Type = type,
            DelayMs = _rows.Count == 0 ? 0 : 50,
            Vk = type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp ? KeyNames.VK_LBUTTON : 0,
            X = cursor.X,
            Y = cursor.Y,
            Smooth = type == MacroActionType.MoveTo,
        };
        var dialog = new MacroActionWindow(_engine, action, isNew: true) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
            InsertRows(new[] { dialog.Result });
    }

    private void OnEditActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroActionRowViewModel row)
            return;
        var dialog = new MacroActionWindow(_engine, row.Action, isNew: false) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            row.Replace(dialog.Result);
            Renumber();
        }
    }

    private void OnDuplicateActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MacroActionRowViewModel row)
            Duplicate(row);
    }

    /// <summary>Inserts a copy of the row right after it and selects the copy.</summary>
    private void Duplicate(MacroActionRowViewModel row)
    {
        int index = _rows.IndexOf(row);
        if (index < 0)
            return;
        var copy = new MacroActionRowViewModel(row.Action.Clone());
        _rows.Insert(index + 1, copy);
        ActionList.SelectedItem = copy;
        ActionList.ScrollIntoView(copy);
    }

    private void OnRemoveActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MacroActionRowViewModel row)
            _rows.Remove(row);
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        // A modal dialog cannot be hidden safely, so it is moved off screen while recording.
        // The main window behind it is hidden too.
        double left = Left, top = Top;
        var main = Owner;
        RecordingSession.Run(_engine, _settings,
            hide: () =>
            {
                Left = SystemParameters.VirtualScreenLeft - ActualWidth - 200;
                main?.Hide();
            },
            restore: () =>
            {
                main?.Show();
                Left = left;
                Top = top;
                Activate();
            },
            done: actions => InsertRows(actions));
    }

    // ---- Reordering ----

    private void MoveRow(MacroActionRowViewModel row, int newIndex)
    {
        int oldIndex = _rows.IndexOf(row);
        newIndex = Math.Clamp(newIndex, 0, _rows.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
            return;
        _rows.Move(oldIndex, newIndex);
        ActionList.SelectedItem = row;
        ActionList.ScrollIntoView(row);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+Up / Alt+Down arrive as system keys.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.D && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && ActionList.SelectedItem is MacroActionRowViewModel selected)
        {
            Duplicate(selected);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 || ActionList.SelectedItem is not MacroActionRowViewModel row)
            return;
        if (key == Key.Up)
        {
            MoveRow(row, _rows.IndexOf(row) - 1);
            e.Handled = true;
        }
        else if (key == Key.Down)
        {
            MoveRow(row, _rows.IndexOf(row) + 1);
            e.Handled = true;
        }
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox or Wpf.Ui.Controls.NumberBox)
                return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            return;
        if ((sender as FrameworkElement)?.Tag is MacroActionRowViewModel row)
        {
            _dragCandidate = row;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var row = _dragCandidate;
        _dragCandidate = null;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(MacroActionRowViewModel), row), DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicators();
        }
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(MacroActionRowViewModel)) is not MacroActionRowViewModel dragged
            || sender is not FrameworkElement element || element.Tag is not MacroActionRowViewModel target
            || ReferenceEquals(dragged, target))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicators();
            return;
        }
        e.Effects = DragDropEffects.Move;
        bool before = e.GetPosition(element).Y < element.ActualHeight / 2;
        foreach (var r in _rows)
        {
            r.DropBefore = ReferenceEquals(r, target) && before;
            r.DropAfter = ReferenceEquals(r, target) && !before;
        }
    }

    private void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MacroActionRowViewModel target)
        {
            target.DropBefore = false;
            target.DropAfter = false;
        }
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(MacroActionRowViewModel)) is not MacroActionRowViewModel dragged
            || sender is not FrameworkElement element || element.Tag is not MacroActionRowViewModel target
            || ReferenceEquals(dragged, target))
        {
            ClearDropIndicators();
            return;
        }
        bool before = e.GetPosition(element).Y < element.ActualHeight / 2;
        ClearDropIndicators();
        int from = _rows.IndexOf(dragged);
        int to = _rows.IndexOf(target) + (before ? 0 : 1);
        if (from < to)
            to--;
        MoveRow(dragged, to);
    }

    private void ClearDropIndicators()
    {
        foreach (var r in _rows)
        {
            r.DropBefore = false;
            r.DropAfter = false;
        }
    }

    // ================= Playback options =================

    private void OnRepeatChanged(object sender, RoutedEventArgs e)
    {
        if (TimesBox != null)
            TimesBox.IsEnabled = TimesRadio.IsChecked == true;
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSpeedLabel();

    private double SelectedSpeed => MacroDefinition.SpeedSteps[Math.Clamp((int)Math.Round(SpeedSlider.Value), 0, 4)];

    private void UpdateSpeedLabel()
    {
        if (SpeedLabel != null)
            SpeedLabel.Text = $"Speed ×{SelectedSpeed}";
    }

    private void OnActivationChanged(object sender, RoutedEventArgs e)
    {
        Validate();
        if (ActivationHint == null)
            return;
        ActivationHint.Text = HoldRadio.IsChecked == true
            ? "Plays only while the hotkey is held down."
            : "Press the hotkey to start, press it again to stop.";
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
            tile.Click += (_, _) =>
            {
                _soundName = name;
                _sounds.Preview(name);
            };
            SoundGrid.Children.Add(tile);
        }
    }

    private void OnSoundSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (SoundPanel != null)
            SoundPanel.Visibility = SoundSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Hotkey =================

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
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
            allowPrimaryMouse: false);
    }

    private void UpdateHotkeyLabel()
    {
        HotkeyCaptureText.Visibility = Visibility.Collapsed;
        HotkeyCaps.Visibility = Visibility.Visible;
        HotkeyCaps.ItemsSource = KeyNames.Parts(_hotkey);
        ClearHotkeyButton.IsEnabled = _hotkey is { IsSet: true };
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
        _hotkey = null;
        UpdateHotkeyLabel();
        Validate();
    }

    // ================= Validation and save =================

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            return "Enter a name.";
        if (_rows.Count == 0)
            return "Add or record at least one action.";
        if (_hotkey == null || !_hotkey.IsSet)
            return HoldRadio.IsChecked == true ? "Hold needs a hotkey. Choose one or switch to Toggle." : null;
        if (KeyNames.IsPrimaryMouse(_hotkey.Vk))
            return "Left and right click cannot be used as a hotkey.";
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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.Focus(); // commit the delay field being edited
        if (GetValidationError() != null)
        {
            Validate();
            return;
        }

        Result = new MacroDefinition
        {
            Id = _editingId == Guid.Empty ? Guid.NewGuid() : _editingId,
            Name = NameBox.Text.Trim(),
            Actions = _rows.Select(r => r.Action.Clone()).ToList(),
            Repeat = TimesRadio.IsChecked == true ? RepeatMode.Times : LoopRadio.IsChecked == true ? RepeatMode.Loop : RepeatMode.Once,
            RepeatCount = (int)Math.Max(1, Math.Round(TimesBox.Value ?? 3)),
            Speed = SelectedSpeed,
            Mode = HoldRadio.IsChecked == true ? ActivationMode.Hold : ActivationMode.Toggle,
            Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
            Enabled = _wasEnabled,
            SoundEnabled = SoundSwitch.IsChecked == true,
            SoundName = _soundName,
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
