using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
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
using FluentSystemIcons = iNKORE.UI.WPF.Modern.Common.IconKeys.FluentSystemIcons;

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
    private MacroEditList _list;
    private readonly ObservableCollection<MacroEditorItem> _items = new();
    private readonly Dictionary<MacroAction, MacroActionRowViewModel> _rowOf = new();
    private readonly Dictionary<Guid, MacroGroupViewModel> _headerOf = new();
    private readonly Dictionary<(MacroAction Owner, bool IsElse), MacroBranchViewModel> _branchOf = new();
    private HotkeyBinding? _hotkey;
    private string _soundName;
    private bool _ready;
    private readonly CodeViewSwitch<MacroDefinition> _code;
    private Point _dragStart;
    private MacroEditorItem? _dragCandidate;
    private MacroEditorItem? _selectOnlyOnRelease;
    private ScrollViewer? _listScroll;

    /// <param name="component">Component mode: the Library component to edit (from the Library), instead of a macro.</param>
    internal MacroEditorWindow(LoopEngine engine, AppSettings settings, MacroLibrary macros, SoundService sounds,
        MacroDefinition? existing, Func<EditedFloatingButton, Point?>? placeButton = null, IEnumerable<MacroAction>? recorded = null,
        LibraryComponent? component = null)
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

        if (component != null)
            existing = new MacroDefinition { Id = component.Id, Name = component.Name, Actions = component.Actions.ConvertAll(a => a.Clone()) };
        var source = existing?.Clone() ?? new MacroDefinition();
        _editingId = existing?.Id ?? Guid.Empty;
        _itemId = existing?.Id ?? Guid.NewGuid();
        _placeButton = placeButton;

        string title = existing == null
            ? Strings.MacroEditor_AddTitle
            : string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_EditNamedTitle, existing.Name);
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
        // The sounds by their names in the user's language; the id (the file's value) is the same position in SoundService.Names.
        SoundChoices.ItemsSource = SoundService.DisplayNames();
        SoundChoices.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnSoundClick), handledEventsToo: true);
        if (component != null)
            StartComponentMode(component);
        LoadMacro(source);
        SoundChoicesScroll.Attach(SoundScroll, () => SoundService.DisplayName(_soundName));

        FloatingEditor.PositionRequested += OnFloatingPositionRequested;
        // A floating button shows only for an item shown in the overlay.
        OverlayOptions.ShowChanged += () => FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        FloatingEditor.IsEnabled = OverlayOptions.ShowInOverlay;
        _code = CreateCodeSwitch();
        _ready = true;
        Validate();
        Closing += (_, _) => RememberSize();
        KeyCaptureField.SetEngine(this, _engine); // the hotkey field waits for keys through it
        Closed += (_, _) =>
        {
            _engine.CancelCapture();
            // Images its conditions took are not held any more: the save that follows keeps those the macro uses.
            _macros.Images.Unhold(this);
        };
        Loaded += (_, _) => Activate(); // e.g. right after a recording, with the main window just restored
    }

    public MacroDefinition? Result { get; private set; }

    /// <summary>Offers the History view of a macro saved before.</summary>
    internal void EnableHistory(ItemHistory history)
    {
        if (_editingId == Guid.Empty)
            return;
        _code.EnableHistory(HistoryPanel, () => HistoryPanel.Load(history.Versions(_itemId)));
        HistoryPanel.RestoreRequested += version =>
        {
            if (HistoryRestore.Read<MacroDefinition>(version, _itemId) is not { } macro)
                return;
            // The images of that version's conditions come back from the History, as the AI activity's Restore does.
            history.RestoreImages(_itemId, MacroJson.Serialize(macro));
            LoadMacro(macro);
            _code.ShowFormView();
            HistoryRestore.Notice(NoticeBar, version);
        };
    }

    /// <summary>Shows <paramref name="source"/> in the List view's fields (at start, and when leaving the Code view).</summary>
    [MemberNotNull(nameof(_list), nameof(_soundName))]
    private void LoadMacro(MacroDefinition source)
    {
        EnabledSwitch.IsOn = source.Enabled;
        DebugSwitch.IsOn = source.Debug;
        ViewLogButton.Visibility = DebugStore.LastRunOf(_itemId) != null ? Visibility.Visible : Visibility.Collapsed;
        _hotkey = source.Hotkey?.Clone();
        _soundName = source.SoundName;

        NameBox.Text = source.Name;
        _list = new MacroEditList(source.Actions.ToList(), source.Groups);
        _rowOf.Clear();
        _headerOf.Clear();
        _branchOf.Clear();
        _checksPerSecond = source.ChecksPerSecond;
        Refresh();
        LoadChecksPerSecond();

        OnceRadio.IsChecked = source.Repeat == RepeatMode.Once;
        LoopRadio.IsChecked = source.Repeat == RepeatMode.Loop;
        TimesRadio.IsChecked = source.Repeat == RepeatMode.Times;
        TimesBox.Value = source.RepeatCount;
        TimesBox.IsEnabled = source.Repeat == RepeatMode.Times;
        int speedIndex = Array.IndexOf(MacroDefinition.SpeedSteps, source.Speed);
        SpeedSlider.Format = v => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_SpeedValue, SpeedAt(v));
        SpeedSlider.Value = speedIndex >= 0 ? speedIndex : 2;

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
            ChecksPerSecond = SelectedChecksPerSecond,
            Groups = _list.Groups.Select(g => g.Clone()).ToList(),
            Repeat = TimesRadio.IsChecked == true ? RepeatMode.Times : LoopRadio.IsChecked == true ? RepeatMode.Loop : RepeatMode.Once,
            RepeatCount = (int)Math.Max(1, double.IsNaN(TimesBox.Value) ? 3 : Math.Round(TimesBox.Value)),
            Speed = SelectedSpeed,
            Mode = IsHold ? ActivationMode.Hold : ActivationMode.Toggle,
            Hotkey = _hotkey is { IsSet: true } ? _hotkey.Clone() : null,
            Enabled = EnabledSwitch.IsOn,
            Debug = DebugSwitch.IsOn,
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

    private void OnViewLogClick(object sender, RoutedEventArgs e)
    {
        if (DebugStore.LastRunOf(_itemId) is string folder)
            new DebugLogWindow(folder, NameBox.Text.Trim()) { Owner = this }.ShowDialog();
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

    private MacroBranchViewModel BranchFor(MacroAction owner, bool isElse)
    {
        if (!_branchOf.TryGetValue((owner, isElse), out var branch))
        {
            branch = new MacroBranchViewModel(owner, isElse);
            _branchOf[(owner, isElse)] = branch;
        }
        return branch;
    }

    /// <summary>
    /// Shows the list again after a change: the macro's actions, each If followed by its Then and Else with their actions
    /// indented under them, group headers before their rows (unless the group is collapsed), numbers in playback order and
    /// totals. Items that stay keep their place, so the list does not jump. <paramref name="select"/> replaces the
    /// selection.
    /// </summary>
    private void Refresh(IEnumerable<MacroEditorItem>? select = null)
    {
        var wanted = new List<MacroEditorItem>();
        var numbers = new Dictionary<MacroAction, int>(ReferenceEqualityComparer.Instance);
        foreach (var action in _list.All)
            numbers[action] = numbers.Count + 1;
        AddRows(_list.Actions, 0, wanted, numbers);

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

        var all = _list.All.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var gone in _rowOf.Keys.Where(a => !all.Contains(a)).ToList())
        {
            _rowOf[gone].PropertyChanged -= OnRowPropertyChanged;
            _rowOf.Remove(gone);
        }
        foreach (Guid gone in _headerOf.Keys.Except(_list.Groups.Select(g => g.Id)).ToList())
            _headerOf.Remove(gone);
        foreach (var gone in _branchOf.Keys.Where(k => !all.Contains(k.Owner)).ToList())
            _branchOf.Remove(gone);

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
        ChecksCard.Visibility = HasConditions ? Visibility.Visible : Visibility.Collapsed;
        if (IsComponent)
        {
            // What it finds is one of its conditions: the list follows the actions.
            ShowReturns(_pendingReturn ?? SelectedReturn);
            _pendingReturn = null;
        }
        UpdateTotals();
        UpdateSelectionBar();
        Validate();
    }

    /// <summary>The rows of one list (the macro's, a Then or an Else) at an indent, with the Ifs' branches inside.</summary>
    private void AddRows(List<MacroAction> list, int indent, List<MacroEditorItem> wanted, Dictionary<MacroAction, int> numbers)
    {
        MacroGroup? current = null;
        foreach (var action in list)
        {
            var row = RowFor(action);
            row.Number = numbers[action];
            var group = _list.GroupOf(action);
            if (group != null && group != current)
            {
                var header = HeaderFor(group);
                header.Indent = indent;
                wanted.Add(header);
            }
            current = group;
            if (group is { Collapsed: true })
                continue;
            row.Indent = indent + (group != null ? 1 : 0);
            row.FoundName = action.FoundId is Guid id ? ConditionName(id) : "";
            (row.ComponentName, row.ComponentDetail) = LinkInfo(action);
            wanted.Add(row);
            if (action.If is not { } block)
                continue;
            var then = BranchFor(action, isElse: false);
            then.Indent = row.Indent + 1;
            wanted.Add(then);
            AddRows(block.Then, row.Indent + 2, wanted, numbers);
            // Stop the macro: its Else shows greyed, saying the If stops the macro instead; its actions are kept, not shown.
            var otherwise = BranchFor(action, isElse: true);
            otherwise.Indent = row.Indent + 1;
            otherwise.Stops = block.OnFail != ConditionFail.Else;
            wanted.Add(otherwise);
            if (!otherwise.Stops)
                AddRows(block.Else, row.Indent + 2, wanted, numbers);
        }
    }

    /// <summary>
    /// A linked row's component name and what it is: its values when it has parameters ("Slot 3986, 1056 · Settle 150 ms"),
    /// otherwise "Component · Diablo IV 5120×1440 · 4 actions"; empty for other rows.
    /// </summary>
    private (string Name, string Detail) LinkInfo(MacroAction action)
    {
        if (action.Type != MacroActionType.Component)
            return ("", "");
        if (action.ComponentId is not Guid id || _macros.Library.Find(id) is not { } found)
            return ("", Strings.Code_ComponentMissing);
        var (collection, component) = found;
        if (component.Parameters.Count > 0)
            return (component.Name, ArgumentsText(component, action.Arguments));
        int count = MacroDefinition.Walk(component.Actions).Count();
        return (component.Name, string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_RowComponent, collection.Name, Count(count)));
    }

    /// <summary>"Slot 3986, 1056 · Settle from Hold · Remove where Remove Affix was found": a call's values, in the parameters' order.</summary>
    private string ArgumentsText(LibraryComponent component, Dictionary<string, System.Text.Json.Nodes.JsonNode?>? arguments) =>
        string.Join(" · ", component.Parameters.Select(p =>
        {
            var argument = arguments?.GetValueOrDefault(p.Name);
            string value = ComponentValues.Reference(argument) switch
            {
                { From: string from } => string.Format(CultureInfo.CurrentCulture, Strings.Param_FromParameter, from),
                { Found: Guid condition } => string.Format(CultureInfo.CurrentCulture, Strings.Action_PositionFound, ConditionName(condition)),
                _ => ComponentValues.Describe(argument ?? p.Default, p.Type, p.Unit),
            };
            return string.Format(CultureInfo.CurrentCulture, Strings.Param_ArgumentShown, p.Name, value);
        }));

    /// <summary>The name of the condition with this Id, anywhere in the macro.</summary>
    private string ConditionName(Guid id) =>
        _list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).FirstOrDefault(c => c.Id == id)?.Name ?? "";

    private bool HasConditions => _list.All.Any(a => a.Type == MacroActionType.If);

    private void UpdateTotals()
    {
        foreach (var group in _list.Groups)
        {
            var actions = _list.ActionsOf(group);
            if (actions.Count == 0)
                continue;
            int first = RowFor(actions[0]).Number, last = RowFor(actions[^1]).Number;
            string range = first == last
                ? string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_ActionNumber, first)
                : string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_ActionRange, first, last);
            HeaderFor(group).Info = string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_GroupInfo,
                range, Count(MacroDefinition.Walk(actions).Count()), DurationText(DurationRange.Of(actions)));
        }
        foreach (var branch in _branchOf.Values)
        {
            int count = MacroDefinition.Walk(branch.List).Count();
            string when = branch.IsElse ? Strings.MacroEditor_ElseInfo : Strings.MacroEditor_ThenInfo;
            branch.Info = branch.Stops ? Strings.MacroEditor_ElseNotPlayed
                : count == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_BranchEmpty, when)
                : string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_BranchInfo, when, Count(count));
        }
        string total = DurationText(DurationRange.Of(_list.Actions));
        int all = _list.All.Count();
        TotalText.Text = _list.Groups.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_Total, Count(all), total)
            : string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_TotalInGroups,
                Count(all), Plurals.Format("MacroEditor_GroupCount", _list.Groups.Count), total);
    }

    /// <summary>"2.1 s"; with conditions "1.4 s to 36.4 s", or "1.4 s or more" when one waits as long as it takes.</summary>
    private static string DurationText(DurationRange range)
    {
        string min = MacroItemViewModel.FormatDuration(range.MinMs);
        if (range.Endless)
            return string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_DurationAtLeast, min);
        if (range.MaxMs > range.MinMs)
            return string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_DurationRange, min, MacroItemViewModel.FormatDuration(range.MaxMs));
        return min;
    }

    private static string Count(int actions) => Plurals.Format("MacroEditor_ActionCount", actions);

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

    /// <summary>Where new actions go.</summary>
    private sealed record InsertAt(List<MacroAction> List, int Index, MacroGroup? Into);

    /// <summary>
    /// Where new actions go: after the last selected row (in its list and group) or group, at the end of a selected Then or
    /// Else, or at the end of the macro.
    /// </summary>
    private InsertAt InsertPoint()
    {
        switch (SelectedItems.LastOrDefault())
        {
            case MacroGroupViewModel header:
                return new InsertAt(_list.ListOf(header.Group), _list.LastIndex(header.Group) + 1, null);
            case MacroBranchViewModel branch:
                return new InsertAt(branch.List, branch.List.Count, null);
            case MacroActionRowViewModel row when _list.ListOf(row.Action) is { } list:
                return new InsertAt(list, list.IndexOf(row.Action) + 1, _list.GroupOf(row.Action));
            default:
                return new InsertAt(_list.Actions, _list.Actions.Count, null);
        }
    }

    /// <summary>Where new actions go, as the second half of the text under the command bar.</summary>
    private string InsertHintText() =>
        SelectedItems.LastOrDefault() switch
        {
            MacroGroupViewModel header => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_InsertAfterGroup, header.Name),
            MacroBranchViewModel branch => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_InsertInBranch, branch.Name, RowFor(branch.Owner).Number),
            MacroActionRowViewModel row when _list.GroupOf(row.Action) is { } group =>
                string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_InsertAfterInGroup, row.Number, group.Name),
            MacroActionRowViewModel row => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_InsertAfter, row.Number),
            _ => Strings.MacroEditor_InsertAtEnd,
        };

    private void UpdateSelectionBar()
    {
        if (SelectionText == null)
            return;
        int count = _list.Expand(SelectedActions, SelectedGroups).Count;
        bool any = count > 0;
        SelectionText.Text = any
            ? Plurals.Format("MacroEditor_Selected", count, InsertHintText())
            : string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_NoneSelected, InsertHintText());
        foreach (var button in new[] { DuplicateButton, CopyButton, DeleteButton })
            button.IsEnabled = any;
        // A group alone is already a group.
        GroupButton.IsEnabled = any && SelectedGroup == null && _list.CanGroup(SelectedActions, SelectedGroups);
        PasteButton.IsEnabled = s_clipboard is { IsEmpty: false };
        // An If is not played by Test: its Then would run on whatever is on the screen.
        TestActionButton.IsEnabled = SelectedRow is { Action.Type: not MacroActionType.If };
        SaveAsComponentButton.IsEnabled = CanSaveAsComponent();
        UnlinkButton.IsEnabled = SelectedActions.Any(a => a.Type == MacroActionType.Component && LinkedComponent(a) != null);
        EditActionButton.IsEnabled = SelectedRow != null || SelectedGroup != null || SelectedBranch != null;
        RenameGroupButton.IsEnabled = SelectedGroup != null;
        UngroupButton.IsEnabled = SelectedGroup != null;
        // Each one only while a group is in the other state.
        ExpandAllGroupsButton.IsEnabled = _list.Groups.Any(g => g.Collapsed);
        CollapseAllGroupsButton.IsEnabled = _list.Groups.Any(g => !g.Collapsed);
    }

    /// <summary>The selected action, when exactly one row and nothing else is selected.</summary>
    private MacroActionRowViewModel? SelectedRow =>
        SelectedItems is [MacroActionRowViewModel row] ? row : null;

    /// <summary>The selected group, when exactly its header and nothing else is selected.</summary>
    private MacroGroupViewModel? SelectedGroup =>
        SelectedItems is [MacroGroupViewModel header] ? header : null;

    /// <summary>The selected Then or Else, when exactly its header and nothing else is selected.</summary>
    private MacroBranchViewModel? SelectedBranch =>
        SelectedItems is [MacroBranchViewModel branch] ? branch : null;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionBar();
    }

    // ---- Adding and editing ----

    private void InsertActions(IEnumerable<MacroAction> actions)
    {
        var block = new ActionBlock();
        block.Actions.AddRange(actions);
        var at = InsertPoint();
        var inserted = _list.Insert(block, at.List, at.Index, at.Into);
        Refresh(ItemsFor(inserted, at.Into));
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
        AddNew(action);
    }

    /// <summary>Opens the action window for a new action and puts it at the insert point when it is saved.</summary>
    private void AddNew(MacroAction action)
    {
        var at = InsertPoint();
        action.GroupId = at.Into?.Id;
        var dialog = ActionWindow(action, isNew: true, at.List, at.Index);
        if (dialog.ShowDialog() != true || dialog.Result is not { } added)
            return;
        // Its group: the insert point's, or the one chosen in the Code view (always next to its actions).
        MacroGroup? group = _list.Groups.FirstOrDefault(g => g.Id == added.GroupId);
        var block = new ActionBlock();
        block.Actions.Add(added);
        var inserted = _list.Insert(block, at.List, at.Index, group);
        Refresh(ItemsFor(inserted, group));
    }

    /// <summary>The action window for an action of <paramref name="list"/> at <paramref name="index"/>.</summary>
    private MacroActionWindow ActionWindow(MacroAction action, bool isNew, List<MacroAction> list, int index) =>
        new(_engine, action, isNew, MacroJson.AllowedGroupIds(list, index, replacing: !isNew), GroupNames(),
            MacroJson.FoundAt(_list.Actions, list, _macros.Library.Signatures()), _macros.Images, _settings.RecognitionDevice,
            // The other Ifs' condition names (not this one's own), so a new condition gets a name of its own.
            _list.All.Where(a => !ReferenceEquals(a, action)).SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
                .Where(c => c.Finds && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!.Trim()).ToList(),
            _macros, EditingParameters) { Owner = this };

    /// <summary>Add condition: the kinds, each opens the condition window with one condition of it (Component: after picking it).</summary>
    private void OnAddConditionClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddConditionButton, Placement = PlacementMode.Bottom };
        foreach (var (kind, icon) in new (ConditionKind, FontIconData)[]
                 {
                     (ConditionKind.Pixel, SegoeFluentIcons.Eyedropper),
                     (ConditionKind.Image, SegoeFluentIcons.Photo2),
                     (ConditionKind.Text, FluentSystemIcons.ScanText_20_Regular),
                     (ConditionKind.Component, SegoeFluentIcons.Link),
                 })
        {
            var item = new MenuItem { Header = ConditionViewModel.MenuName(kind), Icon = new FontIcon { Icon = icon } };
            item.Click += (_, _) => AddCondition(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddCondition(ConditionKind kind)
    {
        var conditions = new List<MacroCondition>();
        if (kind == ConditionKind.Component)
        {
            // The Library's components that say what they find: each chosen one is a condition, its arguments its examples.
            var picker = new LibraryWindow(_macros, LibraryWindow.Mode.Pick, appExe: SelectedApp, onlyConditions: true) { Owner = this };
            if (picker.ShowDialog() != true)
                return;
            foreach (var component in picker.Picked.Select(id => _macros.Library.Find(id)?.Component).OfType<LibraryComponent>())
                conditions.Add(new MacroCondition
                {
                    Kind = ConditionKind.Component,
                    Name = NewConditionName(kind, component.Name, conditions),
                    ComponentId = component.Id,
                    Arguments = ArgumentViewModel.Starting(component),
                });
            if (conditions.Count == 0)
                return;
        }
        else
        {
            var condition = ConditionViewModel.New(kind);
            if (kind != ConditionKind.Pixel)
                condition.Name = NewConditionName(kind);
            conditions.Add(condition);
        }
        AddNew(new MacroAction
        {
            Type = MacroActionType.If,
            DelayMs = _list.Actions.Count == 0 ? 0 : 50,
            If = new MacroIf { Conditions = conditions, Wait = ConditionWait.UpTo, WaitSeconds = 5 },
        });
    }

    // ---- Library ----

    /// <summary>
    /// The main window's one way to change the Library (Save as component, the component's own Save with the macros whose
    /// calls change), set when the editor opens.
    /// </summary>
    internal Func<Func<ComponentLibrary, string?>, IReadOnlyList<MacroDefinition>?, string?>? SaveLibrary { get; set; }

    /// <summary>Opens the Library over the editor (manage), with this macro's links counted as uses; set when the editor opens.</summary>
    internal Action<Window, MacroDefinition>? OpenLibrary { get; set; }

    private LibraryComponent? LinkedComponent(MacroAction row) =>
        row.ComponentId is Guid id ? _macros.Library.Find(id)?.Component : null;

    /// <summary>Add component: the Library to pick from; the chosen components come in as linked rows at the insert point.</summary>
    private void OnAddComponentClick(object sender, RoutedEventArgs e)
    {
        var picker = new LibraryWindow(_macros, LibraryWindow.Mode.Pick, appExe: SelectedApp) { Owner = this };
        if (picker.ShowDialog() != true || picker.Picked.Count == 0)
            return;
        // Each call starts with its component's examples: it works at once, and the user changes what differs.
        InsertActions(picker.Picked.Select(id => new MacroAction
        {
            Type = MacroActionType.Component,
            ComponentId = id,
            Arguments = _macros.Library.Find(id) is { } found ? ArgumentViewModel.Starting(found.Component) : null,
        }));
    }

    /// <summary>
    /// A linked row opens its component, read only but for the row's values (its arguments, with Save), with Unlink this row
    /// and Open in Library.
    /// </summary>
    private void OpenLinked(MacroActionRowViewModel row)
    {
        if (row.Action.ComponentId is not Guid id || _macros.Library.Find(id) is not { } found || _list.ListOf(row.Action) is not { } list)
            return;
        var signatures = _macros.Library.Signatures();
        var arguments = ArgumentViewModel.For(found.Component, row.Action.Arguments, signatures, EditingParameters,
            MacroJson.FoundAt(_list.Actions, list, signatures), _macros.Images);
        var window = new ComponentWindow(found.Collection, found.Component, LinkingMacros(), canUnlink: true, arguments, _engine) { Owner = this };
        if (window.ShowDialog() != true)
            return;
        switch (window.Choice)
        {
            case ComponentWindowChoice.Save:
                row.Action.Arguments = window.Arguments;
                row.Replace(row.Action);
                Refresh();
                break;
            case ComponentWindowChoice.Unlink:
                Unlink(new[] { row.Action });
                break;
            case ComponentWindowChoice.OpenLibrary:
                OpenLibrary?.Invoke(this, BuildMacro());
                Refresh();
                break;
        }
    }

    /// <summary>The saved macros, this one as it is now in the editor instead of as it was saved.</summary>
    private IEnumerable<MacroDefinition> LinkingMacros() =>
        _macros.Macros.Where(m => m.Id != _itemId).Append(BuildMacro());

    /// <summary>Selected rows that can become one component: one run of actions in one list (linked rows stay linked inside it).</summary>
    private bool CanSaveAsComponent()
    {
        var actions = SelectedActions;
        return SaveLibrary != null && actions.Count > 0 && SelectedGroups.Count == 0 && _list.CanGroup(actions, SelectedGroups)
            && _list.ListOf(actions[0]) is { } list && Consecutive(list, actions);
    }

    private static bool Consecutive(List<MacroAction> list, List<MacroAction> actions)
    {
        var indexes = actions.Select(a => list.IndexOf(a)).OrderBy(i => i).ToList();
        return indexes[0] >= 0 && indexes.Zip(indexes.Skip(1)).All(p => p.Second == p.First + 1);
    }

    /// <summary>
    /// Save as component: the selected actions become a component of the Library (saved at once) and are replaced by one
    /// row linked to it, in their place and group. What they took from outside them becomes a parameter, which the row
    /// passes on: a place found by a condition around them (a Point the row gives with Found), a parameter of the component
    /// being edited (the row passes it on with From).
    /// </summary>
    private void OnSaveAsComponentClick(object sender, RoutedEventArgs e)
    {
        if (!CanSaveAsComponent() || SaveLibrary == null)
            return;
        var list = _list.ListOf(SelectedActions[0])!;
        var actions = SelectedActions.OrderBy(a => list.IndexOf(a)).ToList();
        var outside = _list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Name ?? "");
        var (copies, parameters, arguments) = ComponentEdits.Lift(actions, EditingParameters, outside);
        foreach (var copy in MacroDefinition.Walk(copies))
            copy.GroupId = null;
        var dialog = ComponentDetailsWindow.ForSave(copies, _macros.Library, _macros.Images.Has, SelectedApp, parameters);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.Details is not { } details)
            return;
        var component = new LibraryComponent { Actions = copies, Parameters = parameters };
        details.ApplyTo(component);
        string? problem = SaveLibrary(library =>
        {
            if (details.NewCollection is { } created)
            {
                if (ComponentLibrary.CheckCollection(created) is string bad)
                    return bad;
                library.Collections.Add(created);
            }
            if (library.FindCollection(details.CollectionId ?? Guid.Empty) is not { } collection)
                return Strings.Library_NameMissing;
            collection.Components.Add(component);
            return ComponentLibrary.CheckComponent(component, _macros.Images.Has, library.AllComponents);
        }, null);
        if (problem != null)
        {
            ShowLibraryProblem(problem);
            return;
        }
        int index = list.IndexOf(actions[0]);
        MacroGroup? group = _list.GroupOf(actions[0]);
        _list.Remove(actions, Array.Empty<MacroGroup>());
        var block = new ActionBlock();
        block.Actions.Add(new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id, Arguments = arguments });
        var inserted = _list.Insert(block, list, Math.Min(index, list.Count), group);
        Refresh(ItemsFor(inserted, group));
    }

    private void OnUnlinkClick(object sender, RoutedEventArgs e) =>
        Unlink(SelectedActions.Where(a => a.Type == MacroActionType.Component).ToList());

    /// <summary>
    /// Unlink: each linked row becomes the component's actions as they are now, normal rows of this macro, one level, with
    /// the row's values in their fields (<see cref="ComponentEdits.Unlink"/>); a row whose values have no such place is
    /// left linked, saying why.
    /// </summary>
    private void Unlink(IReadOnlyList<MacroAction> rows)
    {
        var shown = new List<MacroEditorItem>();
        foreach (var row in rows)
        {
            if (LinkedComponent(row) is not { } component || _list.ListOf(row) is not { } list)
                continue;
            if (ComponentEdits.Unlink(row, component, out string? problem) is not { } actions)
            {
                ShowLibraryProblem(problem!);
                continue;
            }
            int index = list.IndexOf(row);
            MacroGroup? group = _list.GroupOf(row);
            _list.Remove(new[] { row }, Array.Empty<MacroGroup>());
            var block = new ActionBlock();
            block.Actions.AddRange(actions);
            var inserted = _list.Insert(block, list, Math.Min(index, list.Count), group);
            shown.AddRange(ItemsFor(inserted, group));
        }
        Refresh(shown);
    }

    private async void ShowLibraryProblem(string problem)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Library_Title,
            Content = new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Strings.Editor_Close,
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync(this);
    }

    /// <summary>
    /// "Image 1", "Text 2" (a Component condition: its component's name, then "Rare item 2"): a name no condition of the
    /// macro has (nor <paramref name="also"/>, the ones being added with it).
    /// </summary>
    private string NewConditionName(ConditionKind kind, string? baseName = null, IEnumerable<MacroCondition>? also = null)
    {
        var names = _list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).Concat(also ?? Enumerable.Empty<MacroCondition>())
            .Select(c => c.Name?.Trim() ?? "")
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (baseName != null && !names.Contains(baseName))
            return baseName;
        for (int n = baseName == null ? 1 : 2; ; n++)
        {
            string name = string.Format(CultureInfo.CurrentCulture, Strings.Condition_NewName, baseName ?? ConditionViewModel.KindShortName(kind), n);
            if (!names.Contains(name))
                return name;
        }
    }


    private Dictionary<Guid, string> GroupNames() => _list.Groups.ToDictionary(g => g.Id, g => g.Name);

    /// <summary>Edit: an action opens its window, a group its name (as Rename group).</summary>
    private void OnEditActionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            EditRow(row);
        else if (SelectedGroup is { } header)
            RenameGroup(header);
        else if (SelectedBranch is { } branch)
            EditRow(RowFor(branch.Owner));
    }

    private void EditRow(MacroActionRowViewModel row)
    {
        if (row.IsLinked)
        {
            OpenLinked(row);
            return;
        }
        if (!row.IsEditable)
        {
            EditDelay(row);
            return;
        }
        if (_list.ListOf(row.Action) is not { } list || list.IndexOf(row.Action) is not (>= 0 and var index))
            return;
        var dialog = ActionWindow(row.Action, isNew: false, list, index);
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
            return;
        // GroupId: the same, or another one allowed by the Code view (the groups stay together).
        list[index] = edited;
        // The action's Code view may have moved Then's or Else's actions between groups.
        _list.Normalize();
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
            Header = Strings.MacroEditor_DelayHeader,
            Minimum = 0,
            Maximum = 86_400_000,
            Value = row.Action.DelayMs,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var dialog = new ContentDialog
        {
            Title = row.Title,
            Content = box,
            PrimaryButtonText = Strings.Editor_Save,
            CloseButtonText = Strings.Editor_Cancel,
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
        menu.Items.Add(MenuCommand(Strings.Editor_Test, SegoeFluentIcons.Play, TestActionButton.IsEnabled, OnTestActionClick, ""));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Edit, SegoeFluentIcons.Edit, EditActionButton.IsEnabled, OnEditActionClick, ""));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Duplicate, SegoeFluentIcons.Copy, DuplicateButton.IsEnabled, OnDuplicateSelectedClick, Strings.Keys_Ctrl + "+D"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Group, SegoeFluentIcons.GroupList, GroupButton.IsEnabled, OnGroupSelectedClick, Strings.Keys_Ctrl + "+G"));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Ungroup, SegoeFluentIcons.RemoveFrom, UngroupButton.IsEnabled, OnUngroupClick, ""));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_RenameGroup, SegoeFluentIcons.Rename, RenameGroupButton.IsEnabled, OnRenameGroupClick, ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand(Strings.MacroEditor_SaveAsComponent, SegoeFluentIcons.Library, SaveAsComponentButton.IsEnabled, OnSaveAsComponentClick, ""));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Unlink, FluentSystemIcons.LinkDismiss_20_Regular, UnlinkButton.IsEnabled, OnUnlinkClick, ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Copy, SegoeFluentIcons.Copy, CopyButton.IsEnabled, OnCopySelectedClick, Strings.Keys_Ctrl + "+C"));
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Paste, SegoeFluentIcons.Paste, PasteButton.IsEnabled, OnPasteClick, Strings.Keys_Ctrl + "+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuCommand(Strings.MacroEditor_Delete, SegoeFluentIcons.Delete, DeleteButton.IsEnabled, OnDeleteSelectedClick, Strings.Keys_Delete));
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
            Title = Strings.MacroEditor_Shortcuts,
            Content = new MacroShortcutsView(),
            CloseButtonText = Strings.Editor_Close,
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync(this);
    }

    private void OnTestActionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
            return;
        // A linked row plays its component as the Library has it (the engine's snapshot); inside a component, with the
        // examples of its parameters.
        ActionTestSession.Run(_engine, row.Action, this, EditingParameters);
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
            case ListViewItem { DataContext: MacroBranchViewModel branch }:
                EditRow(RowFor(branch.Owner));
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

    private void OnExpandAllGroupsClick(object sender, RoutedEventArgs e) => SetAllGroupsCollapsed(false);

    private void OnCollapseAllGroupsClick(object sender, RoutedEventArgs e) => SetAllGroupsCollapsed(true);

    /// <summary>Opens or closes every group. The selected actions stay selected (in a closed group, its header).</summary>
    private void SetAllGroupsCollapsed(bool collapsed)
    {
        var selected = _list.Expand(SelectedActions, SelectedGroups);
        foreach (var group in _list.Groups)
            HeaderFor(group).IsCollapsed = collapsed;
        Refresh(ItemsFor(selected));
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } header)
            RenameGroup(header);
    }

    private void RenameGroup(MacroGroupViewModel header)
    {
        var dialog = new GroupNameWindow(Strings.MacroEditor_RenameGroup, Strings.Editor_Save, header.Name, header.Info) { Owner = this };
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
        var dialog = new GroupNameWindow(Strings.Group_Title, Strings.Group_Ok,
            string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_NewGroupName, _list.Groups.Count + 1), Count(count))
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
        var at = InsertPoint();
        var inserted = _list.Insert(block, at.List, at.Index, at.Into);
        Refresh(ItemsFor(inserted, at.Into));
    }

    private void CopySelected()
    {
        var block = _list.Copy(SelectedActions, SelectedGroups);
        if (block.IsEmpty)
            return;
        s_clipboard = block;
        // The copied Ifs' images stay while they can be pasted, even if their macro is deleted meanwhile.
        _macros.Images.Hold(typeof(MacroEditorWindow), ImageStore.IdsIn(new MacroDefinition { Actions = block.Actions }));
        UpdateSelectionBar();
    }

    private void Paste()
    {
        if (s_clipboard is not { IsEmpty: false } block)
            return;
        var at = InsertPoint();
        var inserted = _list.Insert(block, at.List, at.Index, at.Into);
        Refresh(ItemsFor(inserted, at.Into));
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
    private sealed record DropTarget(List<MacroAction> List, int Index, MacroGroup? Into, MacroEditorItem Line, bool Before);

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
                if (moved.Contains(row.Action) || _list.ListOf(row.Action) is not { } list)
                    return null;
                int index = list.IndexOf(row.Action);
                target = new DropTarget(list, upperHalf ? index : index + 1, _list.GroupOf(row.Action), row, upperHalf);
                break;
            case MacroGroupViewModel header:
                if (payload.Groups.Contains(header.Group))
                    return null;
                var group = header.Group;
                var groupList = _list.ListOf(group);
                target = upperHalf
                    ? new DropTarget(groupList, _list.FirstIndex(group), null, header, Before: true)
                    : new DropTarget(groupList, group.Collapsed ? _list.LastIndex(group) + 1 : _list.FirstIndex(group), group, header, Before: false);
                break;
            case MacroBranchViewModel branch:
                // At the start of the Then or Else, under its header.
                target = new DropTarget(branch.List, 0, null, branch, Before: false);
                break;
            default:
                return null;
        }
        // An If never goes inside its own Then or Else.
        if (moved.Any(a => MacroDefinition.Lists(new List<MacroAction> { a }).Skip(1).Any(l => ReferenceEquals(l, target.List))))
            return null;
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
        return new DropTarget(_list.Actions, _list.Actions.Count, null, _items[^1], Before: false);
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
            MoveTo(payload, target.List, target.Index, target.Into);
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

    private void MoveTo(DragPayload payload, List<MacroAction> list, int index, MacroGroup? into)
    {
        var selected = SelectedItems;
        if (_list.Move(payload.Actions, payload.Groups, list, index, into))
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

    private static double SpeedAt(double index) =>
        MacroDefinition.SpeedSteps[Math.Clamp((int)Math.Round(index), 0, MacroDefinition.SpeedSteps.Length - 1)];

    private double SelectedSpeed => SpeedAt(SpeedSlider.Value);

    private bool IsHold => HoldRadio.IsChecked == true;

    private void OnActivationChanged(object sender, RoutedEventArgs e)
    {
        FloatingEditor?.SetHoldMode(IsHold);
        Validate();
        if (ActivationHint == null)
            return;
        ActivationHint.Text = IsHold
            ? Strings.MacroEditor_HoldHint
            : Strings.Editor_ToggleHint;
    }

    // ================= Checks per second =================

    private int _checksPerSecond = MacroDefinition.DefaultChecksPerSecond;

    /// <summary>The choices: 10, 30, 60, Every frame (MacroDefinition.ChecksPerSecondSteps).</summary>
    private void LoadChecksPerSecond()
    {
        ChecksBox.ItemsSource = MacroDefinition.ChecksPerSecondSteps
            .Select(n => n == 0 ? Strings.MacroEditor_ChecksEveryFrame : n.ToString(CultureInfo.CurrentCulture)).ToList();
        int index = Array.IndexOf(MacroDefinition.ChecksPerSecondSteps, _checksPerSecond);
        ChecksBox.SelectedIndex = index >= 0 ? index : Array.IndexOf(MacroDefinition.ChecksPerSecondSteps, MacroDefinition.DefaultChecksPerSecond);
    }

    private int SelectedChecksPerSecond => ChecksBox.SelectedIndex >= 0
        ? MacroDefinition.ChecksPerSecondSteps[ChecksBox.SelectedIndex]
        : MacroDefinition.DefaultChecksPerSecond;

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
            return Strings.Editor_EnterName;
        if (_list.Actions.Count == 0)
            return Strings.MacroEditor_NoActionsError;
        if (ConditionsProblem() is string conditionsProblem)
            return conditionsProblem;
        // The calls to components and the fields from parameters, as the Library checks them.
        string? callProblem = null;
        ComponentChecks.CheckCalls(_list.Actions, "Actions", new CallScope(_macros.Library.Signatures(), EditingParameters, _macros.Images.Has),
            (_, message) => callProblem ??= message);
        if (callProblem != null)
            return callProblem;
        if (IsComponent)
            return ComponentProblem();
        if (AppScopeCard.ValidationError is string appError)
            return NoAppError(appError);
        if (_hotkey == null || !_hotkey.IsSet)
            return IsHold ? Strings.Editor_HoldNeedsHotkey : null;
        if (HotkeyRules.Problem(_hotkey, hold: IsHold) is string problem)
            return problem;
        return HotkeyConflict(_hotkey, SelectedApp);
    }

    /// <summary>
    /// What the conditions need before Save, as the Code view checks it: names of conditions used once, and every action
    /// that goes where a condition found something still inside that condition's Then.
    /// </summary>
    private string? ConditionsProblem()
    {
        var names = _list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
            .Where(c => c.Finds && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!.Trim()).ToList();
        if (names.Count != names.Distinct(StringComparer.CurrentCultureIgnoreCase).Count())
            return Strings.Code_ConditionNameTwice;
        // An image a condition names that PuppyMacro does not have (an If pasted after its macro and image were deleted).
        if (_list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).SelectMany(c => c.Images ?? new List<ConditionImage>())
            .Any(i => !_macros.Images.Has(i.Id)))
            return Strings.Code_ImageMissing;
        if (_list.All.Any(a => a.Type == MacroActionType.Component && LinkedComponent(a) == null))
            return Strings.Code_ComponentMissing;
        var signatures = _macros.Library.Signatures();
        foreach (var action in _list.All)
        {
            if (action.FoundId is not Guid id || _list.ListOf(action) is not { } list || MacroJson.FoundAt(_list.Actions, list, signatures).ContainsKey(id))
                continue;
            return string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_FoundOutside, RowFor(action).Number, ConditionName(id));
        }
        return null;
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
        if (IsComponent)
        {
            if (_code.IsCode)
            {
                if (_code.Read() is not MacroDefinition fromCode)
                {
                    Validate();
                    return;
                }
                LoadMacro(fromCode);
            }
            if (GetValidationError() != null)
            {
                Validate();
                return;
            }
            SaveComponent();
            return;
        }
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

    /// <summary>
    /// Specific app is on with no app chosen: when the hotkey is taken for all apps, turning off Specific app would not
    /// help, so the error names what uses it.
    /// </summary>
    private string NoAppError(string appError) =>
        _hotkey is { IsSet: true } && HotkeyConflict(_hotkey, null) is string conflict
            ? string.Format(CultureInfo.CurrentCulture, Strings.AppScope_NoAppHotkeyUsed, conflict)
            : appError;

    private void OnAppScopeChanged(object? sender, EventArgs e) => Validate();

    private CodeViewSwitch<MacroDefinition> CreateCodeSwitch()
    {
        // Component mode: the component's JSON (its parameters and what it finds too), read back as the Library reads it.
        var code = IsComponent
            ? new CodeViewSwitch<MacroDefinition>(ViewBar, CodeView, ListBody, CancelButton, ErrorText, SaveButton,
                CodeSchema.ForComponent(_itemId), BuildMacro, ComponentJson, ParseComponentJson, LoadMacro, Validate)
            : new CodeViewSwitch<MacroDefinition>(ViewBar, CodeView, ListBody, CancelButton, ErrorText, SaveButton,
                CodeSchema.ForMacro(_itemId), BuildMacro, MacroJson.Serialize,
                (string text, out List<CodeProblem> problems) => MacroJson.Parse(text, _itemId, HotkeyConflict, out problems, _macros.Images.Has,
                    _macros.Library.Signatures()),
                LoadMacro, Validate);
        code.Opening += () =>
        {
            _engine.CancelCapture();
            UpdateHotkeyLabel();
        };
        return code;
    }
}
