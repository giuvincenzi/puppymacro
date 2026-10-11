using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ToggleSplitButton = iNKORE.UI.WPF.Modern.Controls.ToggleSplitButton;
using ToggleSplitButtonIsCheckedChangedEventArgs = iNKORE.UI.WPF.Modern.Controls.ToggleSplitButtonIsCheckedChangedEventArgs;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;
using FontIcon = iNKORE.UI.WPF.Modern.Controls.FontIcon;
using FontIconData = iNKORE.UI.WPF.Modern.Common.IconKeys.FontIconData;
using NumberBox = iNKORE.UI.WPF.Modern.Controls.NumberBox;
using NumberBoxSpinButtonPlacementMode = iNKORE.UI.WPF.Modern.Controls.NumberBoxSpinButtonPlacementMode;
using SegoeFluentIcons = iNKORE.UI.WPF.Modern.Common.IconKeys.SegoeFluentIcons;

namespace PuppyMacro;

/// <summary>Creates or edits a macro. On Save, <see cref="Result"/> holds the edited copy.</summary>
public partial class MacroEditorWindow
{
    /// <summary>Copied actions, kept while PuppyMacro runs so they can be pasted in another macro.</summary>
    private static ActionBlock? s_clipboard;

    private readonly LoopEngine _engine;
    private readonly AppSettings _settings;
    private readonly MacroLibrary _macros;
    private readonly SoundService _sounds;
    private readonly Guid _editingId;
    private readonly Guid _itemId;
    private readonly Func<EditedFloatingButton, Point?>? _placeButton;
    private bool _enabled;
    private MacroEditList _list;
    private readonly ObservableCollection<MacroEditorItem> _items = new();
    private readonly Dictionary<MacroAction, MacroActionRowViewModel> _rowOf = new();
    private readonly Dictionary<Guid, MacroGroupViewModel> _headerOf = new();
    private HotkeyBinding? _hotkey;
    private string _soundName;
    private bool _ready;
    private readonly CodeViewSwitch<MacroDefinition> _code;
    private Point _dragStart;
    private MacroEditorItem? _dragCandidate;
    private MacroEditorItem? _selectOnlyOnRelease;
    private ScrollViewer? _listScroll;

    internal MacroEditorWindow(LoopEngine engine, AppSettings settings, MacroLibrary macros, SoundService sounds,
        MacroDefinition? existing, Func<EditedFloatingButton, Point?>? placeButton = null, IEnumerable<MacroAction>? recorded = null)
    {
        InitializeComponent();
        if (settings.MacroEditorWidth is double width)
            Width = Math.Max(MinWidth, width);
        if (settings.MacroEditorHeight is double height)
            Height = Math.Max(MinHeight, height);
        WindowFit.Apply(this);
        _engine = engine;
        _settings = settings;
        _macros = macros;
        _sounds = sounds;

        var source = existing?.Clone() ?? new MacroDefinition();
        _editingId = existing?.Id ?? Guid.Empty;
        _itemId = existing?.Id ?? Guid.NewGuid();
        _placeButton = placeButton;

        string title = existing == null ? "New macro" : "Edit macro";
        Title = title;

        if (recorded != null)
        {
            foreach (var action in recorded)
            {
                action.GroupId = null;
                source.Actions.Add(action);
            }
        }
        ActionList.ItemsSource = _items;
        SoundChoices.ItemsSource = SoundService.Names;
        SoundChoices.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnSoundClick), handledEventsToo: true);
        LoadMacro(source);
        SoundChoicesScroll.Attach(SoundScroll, () => _soundName);

        FloatingEditor.PositionRequested += OnFloatingPositionRequested;
        // A floating button shows only for an item shown in the overlay.
        OverlayOptions.ShowChanged += () => FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        _code = CreateCodeSwitch();
        _ready = true;
        Validate();
        Closing += (_, _) => RememberSize();
        KeyCaptureField.SetEngine(this, _engine); // the hotkey field waits for keys through it
        Closed += (_, _) => _engine.CancelCapture();
        Loaded += (_, _) => Activate(); // e.g. right after a recording, with the main window just restored
    }

    public MacroDefinition? Result { get; private set; }

    /// <summary>Shows <paramref name="source"/> in the List view's fields (at start, and when leaving the Code view).</summary>
    [MemberNotNull(nameof(_list), nameof(_soundName))]
    private void LoadMacro(MacroDefinition source)
    {
        _enabled = source.Enabled;
        _hotkey = source.Hotkey?.Clone();
        _soundName = source.SoundName;

        NameBox.Text = source.Name;
        _list = new MacroEditList(source.Actions.ToList(), source.Groups);
        _rowOf.Clear();
        _headerOf.Clear();
        Refresh();

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

        SoundSwitch.IsOn = source.SoundEnabled;
        SoundCard.IsEnabled = source.SoundEnabled;
        SoundExpander.IsExpanded = source.SoundEnabled;
        SoundChoices.SelectedIndex = SoundService.Names.ToList().IndexOf(_soundName);

        UpdateHotkeyLabel();
        FloatingEditor.Load(source.FloatingButton, source.Name, HotkeyText(), source.Mode == ActivationMode.Hold);
        AppScopeCard.Load(source.AppExe);
        OverlayOptions.Load(source.ShowInOverlay, source.HideInOverlayWhenDisabled, source.OverlayClickPassesThrough);
    }

    /// <summary>The macro as the List view's fields describe it.</summary>
    private MacroDefinition BuildMacro()
    {
        var macro = new MacroDefinition
        {
            Id = _itemId,
            Name = NameBox.Text.Trim(),
            Actions = _list.Actions.Select(a => a.Clone()).ToList(),
            Groups = _list.Groups.Select(g => g.Clone()).ToList(),
            Repeat = TimesRadio.IsChecked == true ? RepeatMode.Times : LoopRadio.IsChecked == true ? RepeatMode.Loop : RepeatMode.Once,
            RepeatCount = (int)Math.Max(1, double.IsNaN(TimesBox.Value) ? 3 : Math.Round(TimesBox.Value)),
            Speed = SelectedSpeed,
            Mode = IsHold ? ActivationMode.Hold : ActivationMode.Toggle,
            Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
            Enabled = _enabled,
            SoundEnabled = SoundSwitch.IsOn,
            SoundName = _soundName,
            FloatingButton = FloatingEditor.ToModel(),
            AppExe = AppScopeCard.AppExe,
            ShowInOverlay = OverlayOptions.ShowInOverlay,
            HideInOverlayWhenDisabled = OverlayOptions.HideWhenDisabled,
            OverlayClickPassesThrough = OverlayOptions.ClickPassesThrough,
        };
        macro.NormalizeGroups();
        return macro;
    }

    // ================= Window size =================

    /// <summary>The size is saved with the settings when the editor closes (MainWindow saves them).</summary>
    private void RememberSize()
    {
        if (WindowState != WindowState.Normal)
            return;
        _settings.MacroEditorWidth = Math.Round(ActualWidth);
        _settings.MacroEditorHeight = Math.Round(ActualHeight);
    }

    // ================= List =================

    private MacroActionRowViewModel RowFor(MacroAction action)
    {
        if (!_rowOf.TryGetValue(action, out var row))
        {
            row = new MacroActionRowViewModel(action);
            row.PropertyChanged += OnRowPropertyChanged;
            _rowOf[action] = row;
        }
        return row;
    }

    private MacroGroupViewModel HeaderFor(MacroGroup group)
    {
        if (!_headerOf.TryGetValue(group.Id, out var header) || !ReferenceEquals(header.Group, group))
        {
            header = new MacroGroupViewModel(group);
            _headerOf[group.Id] = header;
        }
        return header;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroActionRowViewModel.Delay))
            UpdateTotals();
    }

    /// <summary>
    /// Shows the list again after a change: group headers, then their rows (unless the group is
    /// collapsed), numbers and totals. Items that stay keep their place, so the list does not
    /// jump. <paramref name="select"/> replaces the selection.
    /// </summary>
    private void Refresh(IEnumerable<MacroEditorItem>? select = null)
    {
        var wanted = new List<MacroEditorItem>();
        MacroGroup? current = null;
        for (int i = 0; i < _list.Actions.Count; i++)
        {
            var action = _list.Actions[i];
            var row = RowFor(action);
            row.Number = i + 1;
            var group = _list.GroupOf(action);
            if (group != null && group != current)
                wanted.Add(HeaderFor(group));
            current = group;
            row.IsInGroup = group != null;
            if (group is not { Collapsed: true })
                wanted.Add(row);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], wanted[i]))
                continue;
            int at = _items.IndexOf(wanted[i]);
            if (at > i)
                _items.Move(at, i);
            else
                _items.Insert(i, wanted[i]);
        }
        while (_items.Count > wanted.Count)
            _items.RemoveAt(_items.Count - 1);

        foreach (var gone in _rowOf.Keys.Except(_list.Actions).ToList())
        {
            _rowOf[gone].PropertyChanged -= OnRowPropertyChanged;
            _rowOf.Remove(gone);
        }
        foreach (Guid gone in _headerOf.Keys.Except(_list.Groups.Select(g => g.Id)).ToList())
            _headerOf.Remove(gone);

        if (select != null)
        {
            var items = select.Where(_items.Contains).Distinct().ToList();
            ActionList.SelectedItems.Clear();
            foreach (var item in items)
                ActionList.SelectedItems.Add(item);
            if (items.Count > 0)
                ActionList.ScrollIntoView(items[^1]);
        }

        EmptyActionsText.Visibility = _list.Actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTotals();
        UpdateSelectionBar();
        Validate();
    }

    private void UpdateTotals()
    {
        foreach (var group in _list.Groups)
        {
            int first = _list.FirstIndex(group) + 1, last = _list.LastIndex(group) + 1;
            var actions = _list.ActionsOf(group);
            string range = first == last ? $"#{first}" : $"#{first}–{last}";
            HeaderFor(group).Info = $"{range} · {Count(actions.Count)} · {MacroItemViewModel.FormatDuration(actions.Sum(Duration))}";
        }
        double total = _list.Actions.Sum(Duration);
        string groups = _list.Groups.Count switch
        {
            0 => "",
            1 => " in 1 group",
            int n => $" in {n} groups",
        };
        TotalText.Text = $"{Count(_list.Actions.Count)}{groups}, {MacroItemViewModel.FormatDuration(total)} at ×1";
    }

    private static double Duration(MacroAction action) => action.DelayMs + action.DurationMs;

    private static string Count(int actions) => $"{actions} {(actions == 1 ? "action" : "actions")}";

    /// <summary>The list items for these actions: their rows, or their group's header when it is collapsed.</summary>
    private IEnumerable<MacroEditorItem> ItemsFor(IEnumerable<MacroAction> actions, MacroGroup? into = null)
    {
        foreach (var action in actions)
        {
            var group = _list.GroupOf(action);
            // A group made by the operation (a duplicated or pasted group) is selected as a whole.
            if (group != null && (group.Collapsed || group != into && _list.ActionsOf(group).All(actions.Contains)))
                yield return HeaderFor(group);
            else
                yield return RowFor(action);
        }
    }

    // ---- Selection ----

    private List<MacroEditorItem> SelectedItems =>
        ActionList.SelectedItems.OfType<MacroEditorItem>().OrderBy(_items.IndexOf).ToList();

    private List<MacroAction> SelectedActions => SelectedItems.OfType<MacroActionRowViewModel>().Select(r => r.Action).ToList();

    private List<MacroGroup> SelectedGroups => SelectedItems.OfType<MacroGroupViewModel>().Select(h => h.Group).ToList();

    /// <summary>Where new actions go: after the last selected row (in its group) or group, or at the end.</summary>
    private (int Index, MacroGroup? Into) InsertPoint()
    {
        switch (SelectedItems.LastOrDefault())
        {
            case MacroGroupViewModel header:
                return (_list.LastIndex(header.Group) + 1, null);
            case MacroActionRowViewModel row:
                return (_list.Actions.IndexOf(row.Action) + 1, _list.GroupOf(row.Action));
            default:
                return (_list.Actions.Count, null);
        }
    }

    /// <summary>Where new actions go, as the second half of the text under the command bar.</summary>
    private string InsertHintText() =>
        SelectedItems.LastOrDefault() switch
        {
            MacroGroupViewModel header => $"New actions go after the group \"{header.Name}\"",
            MacroActionRowViewModel row when _list.GroupOf(row.Action) is { } group =>
                $"New actions go after #{row.Number}, in \"{group.Name}\"",
            MacroActionRowViewModel row => $"New actions go after #{row.Number}",
            _ => "New actions go at the end",
        };

    private void UpdateSelectionBar()
    {
        if (SelectionText == null)
            return;
        int count = _list.Expand(SelectedActions, SelectedGroups).Count;
        bool any = count > 0;
        SelectionText.Text = (any ? $"{Count(count)} selected" : "Select actions to test, edit, group, duplicate, copy or delete them")
                             + " · " + InsertHintText();
        foreach (var button in new[] { DuplicateButton, CopyButton, DeleteButton })
            button.IsEnabled = any;
        // A group alone is already a group.
        GroupButton.IsEnabled = any && SelectedGroup == null;
        PasteButton.IsEnabled = s_clipboard is { IsEmpty: false };
        TestActionButton.IsEnabled = SelectedRow != null;
        EditActionButton.IsEnabled = SelectedRow != null || SelectedGroup != null;
        RenameGroupButton.IsEnabled = SelectedGroup != null;
        UngroupButton.IsEnabled = SelectedGroup != null;
    }

    /// <summary>The selected action, when exactly one row and nothing else is selected.</summary>
    private MacroActionRowViewModel? SelectedRow =>
        SelectedItems is [MacroActionRowViewModel row] ? row : null;

    /// <summary>The selected group, when exactly its header and nothing else is selected.</summary>
    private MacroGroupViewModel? SelectedGroup =>
        SelectedItems is [MacroGroupViewModel header] ? header : null;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionBar();
    }

    // ---- Adding and editing ----

    private void InsertActions(IEnumerable<MacroAction> actions)
    {
        var block = new ActionBlock();
        block.Actions.AddRange(actions);
        var (index, into) = InsertPoint();
        var inserted = _list.Insert(block, index, into);
        Refresh(ItemsFor(inserted, into));
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
            var item = new MenuItem
            {
                Header = MacroActionWindow.TypeName(type),
                Icon = new FontIcon { Icon = MacroActionRowViewModel.IconOf(type) },
            };
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
            DelayMs = _list.Actions.Count == 0 ? 0 : 50,
            Vk = type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp ? KeyNames.VK_LBUTTON : 0,
            X = cursor.X,
            Y = cursor.Y,
            Smooth = type == MacroActionType.MoveTo,
        };
        var (index, into) = InsertPoint();
        action.GroupId = into?.Id;
        var dialog = new MacroActionWindow(_engine, action, isNew: true,
            MacroJson.AllowedGroupIds(_list.Actions, index, replacing: false), GroupNames()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } added)
            return;
        // Its group: the insert point's, or the one chosen in the Code view (always next to its actions).
        MacroGroup? group = _list.Groups.FirstOrDefault(g => g.Id == added.GroupId);
        var block = new ActionBlock();
        block.Actions.Add(added);
        var inserted = _list.Insert(block, index, group);
        Refresh(ItemsFor(inserted, group));
    }

    private Dictionary<Guid, string> GroupNames() => _list.Groups.ToDictionary(g => g.Id, g => g.Name);

    /// <summary>Edit: an action opens its window, a group its name (as Rename group).</summary>
    private void OnEditActionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            EditRow(row);
        else if (SelectedGroup is { } header)
            RenameGroup(header);
    }

    private void EditRow(MacroActionRowViewModel row)
    {
        if (!row.IsEditable)
        {
            EditDelay(row);
            return;
        }
        int index = _list.Actions.IndexOf(row.Action);
        if (index < 0)
            return;
        var dialog = new MacroActionWindow(_engine, row.Action, isNew: false,
            MacroJson.AllowedGroupIds(_list.Actions, index, replacing: true), GroupNames()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
            return;
        // GroupId: the same, or another one allowed by the Code view (the groups stay together).
        _list.Actions[index] = edited;
        _rowOf.Remove(row.Action);
        _rowOf[edited] = row;
        row.Replace(edited);
        Refresh();
    }

    /// <summary>A recorded mouse path has no action window: Edit changes only its delay.</summary>
    private async void EditDelay(MacroActionRowViewModel row)
    {
        var box = new NumberBox
        {
            Header = "Delay before this action (ms)",
            Minimum = 0,
            Maximum = 86_400_000,
            Value = row.Action.DelayMs,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var dialog = new ContentDialog
        {
            Title = row.Title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync(this) == ContentDialogResult.Primary)
            row.Delay = box.Value;
    }

    /// <summary>Right-click on a row: the commands for the selection, in a context menu at the pointer.</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ActionList, source) is not ListViewItem { DataContext: MacroEditorItem item })
            return;
        // As in File Explorer: a row outside the selection becomes the selection.
        if (!ActionList.SelectedItems.Contains(item))
        {
            ActionList.SelectedItems.Clear();
            ActionList.SelectedItem = item;
        }
        UpdateSelectionBar();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuCommand("Test", SegoeFluentIcons.Play, TestActionButton.IsEnabled, OnTestActionClick, ""));
        menu.Items.Add(MenuCommand("Edit", SegoeFluentIcons.Edit, EditActionButton.IsEnabled, OnEditActionClick, ""));
        menu.Items.Add(MenuCommand("Duplicate", SegoeFluentIcons.Copy, DuplicateButton.IsEnabled, OnDuplicateSelectedClick, "Ctrl+D"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("Group", SegoeFluentIcons.GroupList, GroupButton.IsEnabled, OnGroupSelectedClick, "Ctrl+G"));
        menu.Items.Add(MenuCommand("Ungroup", SegoeFluentIcons.RemoveFrom, UngroupButton.IsEnabled, OnUngroupClick, ""));
        menu.Items.Add(MenuCommand("Rename group", SegoeFluentIcons.Rename, RenameGroupButton.IsEnabled, OnRenameGroupClick, ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("Copy", SegoeFluentIcons.Copy, CopyButton.IsEnabled, OnCopySelectedClick, "Ctrl+C"));
        menu.Items.Add(MenuCommand("Paste", SegoeFluentIcons.Paste, PasteButton.IsEnabled, OnPasteClick, "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand("Delete", SegoeFluentIcons.Delete, DeleteButton.IsEnabled, OnDeleteSelectedClick, "Delete"));
        menu.IsOpen = true;
    }

    private static MenuItem MenuCommand(string header, FontIconData icon, bool enabled, RoutedEventHandler click, string keys)
    {
        var item = new MenuItem { Header = header, Icon = new FontIcon { Icon = icon }, IsEnabled = enabled, InputGestureText = keys };
        item.Click += click;
        return item;
    }

    /// <summary>The density button: on, the library's compact density on the list, as its Gallery's "Compact sizing" does.</summary>
    private void OnDensityChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        bool compact = sender.IsChecked;
        var lists = ActionList.Resources.MergedDictionaries;
        lists.Clear();
        if (compact)
            lists.Add(new ResourceDictionary { Source = new Uri("/iNKORE.UI.WPF.Modern;component/Themes/DensityStyles/Compact.xaml", UriKind.Relative) });
        NormalDensityItem.IsChecked = !compact;
        CompactDensityItem.IsChecked = compact;
    }

    private void OnNormalDensityClick(object sender, RoutedEventArgs e) => DensityButton.IsChecked = false;

    private void OnCompactDensityClick(object sender, RoutedEventArgs e) => DensityButton.IsChecked = true;

    private async void OnShortcutsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Shortcuts",
            Content = new MacroShortcutsView(),
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync(this);
    }

    private void OnTestActionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            ActionTestSession.Run(_engine, row.Action, this);
    }

    /// <summary>Double-click: an action opens Edit, a group opens or closes.</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInsideInteractiveControl(source))
            return;
        // The click can land on text (a Run, not a Visual): ContainerFromElement walks up from it too.
        switch (ItemsControl.ContainerFromElement(ActionList, source))
        {
            case ListViewItem { DataContext: MacroActionRowViewModel row }:
                EditRow(row);
                e.Handled = true;
                break;
            case ListViewItem { DataContext: MacroGroupViewModel header }:
                header.IsCollapsed = !header.IsCollapsed;
                Refresh();
                e.Handled = true;
                break;
        }
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        // A modal dialog cannot be hidden safely, so it is moved off screen while recording.
        // The main window is hidden too (it already is while an editor is open: then it stays hidden).
        double left = Left, top = Top;
        var main = Owner;
        bool mainWasVisible = main?.IsVisible == true;
        RecordingSession.Run(_engine, _settings,
            hide: () =>
            {
                Left = SystemParameters.VirtualScreenLeft - ActualWidth - 200;
                main?.Hide();
            },
            restore: () =>
            {
                if (mainWasVisible)
                    main?.Show();
                Left = left;
                Top = top;
                Activate();
            },
            done: InsertActions);
    }

    // ---- Groups ----

    private void OnToggleGroupClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroGroupViewModel header)
            return;
        header.IsCollapsed = !header.IsCollapsed;
        Refresh();
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } header)
            RenameGroup(header);
    }

    private void RenameGroup(MacroGroupViewModel header)
    {
        var dialog = new GroupNameWindow("Rename group", "Save", header.Name, header.Info) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            header.Name = dialog.Result;
            UpdateSelectionBar();
        }
    }

    private void OnUngroupClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } header)
            return;
        var actions = _list.ActionsOf(header.Group);
        _list.Ungroup(header.Group);
        Refresh(actions.Select(RowFor));
    }

    // ---- Selection bar and shortcuts ----

    private void OnGroupSelectedClick(object sender, RoutedEventArgs e) => GroupSelected();

    private void OnDuplicateSelectedClick(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void OnCopySelectedClick(object sender, RoutedEventArgs e) => CopySelected();

    private void OnPasteClick(object sender, RoutedEventArgs e) => Paste();

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void GroupSelected()
    {
        int count = _list.Expand(SelectedActions, SelectedGroups).Count;
        // A group alone is already a group.
        if (count == 0 || SelectedGroup != null)
            return;
        var dialog = new GroupNameWindow("Group actions", "Group", $"Group {_list.Groups.Count + 1}", Count(count))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;
        var group = _list.Group(SelectedActions, SelectedGroups, dialog.Result);
        if (group != null)
            Refresh(new[] { HeaderFor(group) });
    }

    private void DuplicateSelected()
    {
        var block = _list.Copy(SelectedActions, SelectedGroups);
        if (block.IsEmpty)
            return;
        var (index, into) = InsertPoint();
        var inserted = _list.Insert(block, index, into);
        Refresh(ItemsFor(inserted, into));
    }

    private void CopySelected()
    {
        var block = _list.Copy(SelectedActions, SelectedGroups);
        if (block.IsEmpty)
            return;
        s_clipboard = block;
        UpdateSelectionBar();
    }

    private void Paste()
    {
        if (s_clipboard is not { IsEmpty: false } block)
            return;
        var (index, into) = InsertPoint();
        var inserted = _list.Insert(block, index, into);
        Refresh(ItemsFor(inserted, into));
    }

    private void DeleteSelected()
    {
        var actions = SelectedActions;
        var groups = SelectedGroups;
        if (actions.Count == 0 && groups.Count == 0)
            return;
        _list.Remove(actions, groups);
        Refresh(Array.Empty<MacroEditorItem>());
    }

    private void MoveSelected(bool up)
    {
        var selected = SelectedItems;
        bool moved = up ? _list.MoveUp(SelectedActions, SelectedGroups) : _list.MoveDown(SelectedActions, SelectedGroups);
        if (moved)
            Refresh(selected);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideTextInput(source))
            return; // typing a delay: Delete, Ctrl+C and Ctrl+V edit the text
        // Alt+Up / Alt+Down arrive as system keys.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        Action? command = (modifiers, key) switch
        {
            (ModifierKeys.Control, Key.D) => DuplicateSelected,
            (ModifierKeys.Control, Key.G) => GroupSelected,
            (ModifierKeys.Control, Key.C) => CopySelected,
            (ModifierKeys.Control, Key.V) => Paste,
            (ModifierKeys.None, Key.Delete) => DeleteSelected,
            (ModifierKeys.Alt, Key.Up) => () => MoveSelected(up: true),
            (ModifierKeys.Alt, Key.Down) => () => MoveSelected(up: false),
            _ => null,
        };
        if (command == null)
            return;
        command();
        e.Handled = true;
    }

    private static bool IsInsideTextInput(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is TextBoxBase)
                return true;
            if (source is ListViewItem)
                return false;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- Drag and drop ----

    /// <summary>What is being dragged: actions selected one by one and whole groups.</summary>
    private sealed record DragPayload(List<MacroAction> Actions, List<MacroGroup> Groups);

    /// <summary>Where a drop puts the dragged actions, and where its indicator is drawn.</summary>
    private sealed record DropTarget(int Index, MacroGroup? Into, MacroEditorItem Line, bool Before);

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox or NumberBox)
                return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        _selectOnlyOnRelease = null;
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            return;
        if ((sender as FrameworkElement)?.Tag is not MacroEditorItem item)
            return;
        _dragCandidate = item;
        _dragStart = e.GetPosition(this);
        // A plain click on a selected item would select only that item: wait for the release,
        // so a multiple selection can be dragged.
        if (Keyboard.Modifiers == ModifierKeys.None && ActionList.SelectedItems.Count > 1 && ActionList.SelectedItems.Contains(item))
        {
            _selectOnlyOnRelease = item;
            ActionList.Focus();
            e.Handled = true;
        }
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnlyOnRelease != null && ReferenceEquals((sender as FrameworkElement)?.Tag, _selectOnlyOnRelease))
        {
            ActionList.SelectedItems.Clear();
            ActionList.SelectedItem = _selectOnlyOnRelease;
        }
        _selectOnlyOnRelease = null;
        _dragCandidate = null;
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragCandidate;
        _dragCandidate = null;
        _selectOnlyOnRelease = null;
        if (!ActionList.SelectedItems.Contains(item))
        {
            ActionList.SelectedItems.Clear();
            ActionList.SelectedItem = item;
        }
        var payload = new DragPayload(SelectedActions, SelectedGroups);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(DragPayload), payload), DragDropEffects.Move);
        }
        finally
        {
            ClearDropIndicators();
        }
    }

    /// <summary>
    /// The drop target over an item. On a row: before or after it, in the row's group. On a group
    /// header: the top half is before the group, the bottom half is into it (at its start, or at
    /// its end when it is collapsed). Null where the dragged items cannot go.
    /// </summary>
    private DropTarget? TargetAt(MacroEditorItem item, bool upperHalf, DragPayload payload)
    {
        var moved = _list.Expand(payload.Actions, payload.Groups);
        DropTarget? target;
        switch (item)
        {
            case MacroActionRowViewModel row:
                if (moved.Contains(row.Action))
                    return null;
                int index = _list.Actions.IndexOf(row.Action);
                target = new DropTarget(upperHalf ? index : index + 1, _list.GroupOf(row.Action), row, upperHalf);
                break;
            case MacroGroupViewModel header:
                if (payload.Groups.Contains(header.Group))
                    return null;
                var group = header.Group;
                target = upperHalf
                    ? new DropTarget(_list.FirstIndex(group), null, header, Before: true)
                    : new DropTarget(group.Collapsed ? _list.LastIndex(group) + 1 : _list.FirstIndex(group), group, header, Before: false);
                break;
            default:
                return null;
        }
        // Groups are never nested.
        return target.Into != null && payload.Groups.Count > 0 ? null : target;
    }

    private void ShowDropIndicator(DropTarget? target)
    {
        foreach (var item in _items)
        {
            bool here = target != null && ReferenceEquals(item, target.Line);
            item.DropBefore = here && target!.Before;
            item.DropAfter = here && !target!.Before;
            item.DropIndented = here && target!.Into != null;
        }
    }

    /// <summary>
    /// The drop target under the mouse: the item it is over (the space between rows belongs to
    /// the item around it), or the end of the macro below the last item.
    /// </summary>
    private DropTarget? TargetFor(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DragPayload)) is not DragPayload payload || _items.Count == 0)
            return null;
        if (ItemsControl.ContainerFromElement(ActionList, e.OriginalSource as DependencyObject) is ListViewItem
            {
                DataContext: MacroEditorItem item,
            } container)
            return TargetAt(item, e.GetPosition(container).Y < container.ActualHeight / 2, payload);
        return new DropTarget(_list.Actions.Count, null, _items[^1], Before: false);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = TargetFor(e);
        e.Effects = target == null ? DragDropEffects.None : DragDropEffects.Move;
        ShowDropIndicator(target);
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
    {
        // Also raised when the mouse moves from one item to another: clear only outside the list.
        Point at = e.GetPosition(ActionList);
        if (at.X < 0 || at.Y < 0 || at.X >= ActionList.ActualWidth || at.Y >= ActionList.ActualHeight)
            ClearDropIndicators();
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = TargetFor(e);
        ClearDropIndicators();
        if (target != null && e.Data.GetData(typeof(DragPayload)) is DragPayload payload)
            MoveTo(payload, target.Index, target.Into);
    }


    /// <summary>Scrolls the list while dragging near its top or bottom edge.</summary>
    private void OnListPreviewDragOver(object sender, DragEventArgs e)
    {
        _listScroll ??= FindChild<ScrollViewer>(ActionList);
        if (_listScroll == null)
            return;
        double y = e.GetPosition(ActionList).Y;
        if (y < 30)
            _listScroll.LineUp();
        else if (y > ActionList.ActualHeight - 30)
            _listScroll.LineDown();
    }

    private void MoveTo(DragPayload payload, int index, MacroGroup? into)
    {
        var selected = SelectedItems;
        if (_list.Move(payload.Actions, payload.Groups, index, into))
            Refresh(selected);
    }

    private void ClearDropIndicators() => ShowDropIndicator(null);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
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
        if (SpeedCard != null)
            SpeedCard.Header = $"Speed ×{SelectedSpeed}";
    }

    private bool IsHold => HoldRadio.IsChecked == true;

    private void OnActivationChanged(object sender, RoutedEventArgs e)
    {
        FloatingEditor?.SetHoldMode(IsHold);
        Validate();
        if (ActivationHint == null)
            return;
        ActivationHint.Text = IsHold
            ? "Plays only while the hotkey is held down."
            : "Press the hotkey to start, press it again to stop.";
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

    /// <summary>The sound can be chosen only while it is on: turning it on shows the choice.</summary>
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
        UpdateHotkeyLabel();
        var edited = new EditedFloatingButton(_itemId, NameBox.Text.Trim(), HotkeyText(), FloatingEditor.ToModel());
        if (_placeButton?.Invoke(edited) is Point position)
            FloatingEditor.SetPosition(position);
    }

    // ================= Validation and save =================

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        FloatingEditor?.SetName(NameBox.Text);
        Validate();
    }

    private string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            return "Enter a name.";
        if (_list.Actions.Count == 0)
            return "Add or record at least one action.";
        if (AppScopeCard.ValidationError is string appError)
            return appError;
        if (_hotkey == null || !_hotkey.IsSet)
            return IsHold ? "Hold needs a hotkey. Choose one or switch to Toggle." : null;
        if (HotkeyRules.Problem(_hotkey, hold: IsHold) is string problem)
            return problem;
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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.Focus(); // commit the delay field being edited
        if (_code.IsCode)
        {
            if (_code.Read() is not MacroDefinition fromCode)
            {
                Validate();
                return;
            }
            Result = fromCode;
            DialogResult = true;
            return;
        }

        if (GetValidationError() != null)
        {
            Validate();
            return;
        }
        Result = BuildMacro();
        DialogResult = true;
    }

    /// <summary>Form view: closes the editor without saving. Code view: Discard changes (see <see cref="CodeViewSwitch{T}"/>).</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_code.IsCode)
            _code.Discard();
        else
            DialogResult = false;
    }

    // ================= Code view =================

    /// <summary>The app chosen in Specific app; null for all apps (also while none is chosen yet).</summary>
    private string? SelectedApp => AppScopeCard.AppExe is { Length: > 0 } app ? app : null;

    private string? HotkeyConflict(HotkeyBinding binding, string? appExe) =>
        HotkeyConflicts.Find(binding, _settings, _macros, _editingId, appExe: appExe);

    private void OnAppScopeChanged(object? sender, EventArgs e) => Validate();

    private CodeViewSwitch<MacroDefinition> CreateCodeSwitch()
    {
        var code = new CodeViewSwitch<MacroDefinition>(ViewBar, CodeView, ListBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForMacro(_itemId), BuildMacro, MacroJson.Serialize,
            (string text, out List<CodeProblem> problems) => MacroJson.Parse(text, _itemId, HotkeyConflict, out problems),
            LoadMacro, Validate);
        code.Opening += () =>
        {
            _engine.CancelCapture();
            UpdateHotkeyLabel();
        };
        return code;
    }
}
