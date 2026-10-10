using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;
using FontIcon = iNKORE.UI.WPF.Modern.Controls.FontIcon;
using NumberBox = iNKORE.UI.WPF.Modern.Controls.NumberBox;
using NumberBoxValueChangedEventArgs = iNKORE.UI.WPF.Modern.Controls.NumberBoxValueChangedEventArgs;
using SegoeFluentIcons = iNKORE.UI.WPF.Modern.Common.IconKeys.SegoeFluentIcons;
using SettingsExpander = iNKORE.UI.WPF.Modern.Controls.SettingsExpander;

namespace PuppyMacro;

public partial class MainWindow
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly LoopEngine _engine;
    private readonly ObservableCollection<LoopItemViewModel> _items;
    private readonly ListCollectionView _view;
    private readonly MacroLibrary _macros;
    private readonly ObservableCollection<MacroItemViewModel> _macroItems;
    private readonly ListCollectionView _macroView;
    private readonly OverlayPanelWindow _overlayPanelWindow;
    private readonly List<FloatingButtonWindow> _buttonWindows = new();
    private readonly DispatcherTimer _saveOpacityTimer;
    private readonly DispatcherTimer _saveVolumeTimer;
    private readonly SoundService _sounds;
    private Point _dragStart;
    private IListItem? _dragCandidate;
    private readonly bool _initializing;
    private bool _overlayModeActive;
    private readonly ObservableCollection<RemapItemViewModel> _remapItems;
    private readonly ListCollectionView _remapView;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _panelTargetsTimer;
    private bool _engineStarted;
    private bool _exiting;
    private bool _importing;

    internal MainWindow(SettingsStore store, AppSettings settings, MacroLibrary macros)
    {
        _initializing = true;
        InitializeComponent();
        WindowFit.Apply(this);

        _store = store;
        _settings = settings;
        _macros = macros;

        Title = App.DisplayTitle;
        AppTitleText.Text = App.DisplayTitle;
        AboutText.Text = App.DisplayTitle;
        DevBuildStrip.Visibility = App.IsDevBuild ? Visibility.Visible : Visibility.Collapsed;

        _sounds = new SoundService(Dispatcher) { Volume = settings.SoundVolume };

        _engine = new LoopEngine(EngineSnapshot.From(settings, macros), Dispatcher);
        KeyCaptureField.SetEngine(this, _engine); // the Settings hotkey fields wait for keys through it
        SetUpGlobalHotkeyFields();
        _engine.StateChanged += OnEngineStateChanged;
        _engine.OverlayModeToggleRequested += ToggleOverlayMode;
        _engine.OverlayPanelDragged += OnOverlayPanelDragged;
        _engine.SoundRequested += (name, start) => _sounds.Play(name, start);
        _engine.ForegroundAppChanged += OnForegroundAppChanged;

        _items = new ObservableCollection<LoopItemViewModel>(settings.Loops.Select(CreateItem));
        _view = new ListCollectionView(_items) { Filter = FilterLoop };
        LoopList.ItemsSource = _view;

        _macroItems = new ObservableCollection<MacroItemViewModel>(macros.Macros.Select(CreateMacroItem));
        _macroView = new ListCollectionView(_macroItems) { Filter = FilterMacro };
        MacroList.ItemsSource = _macroView;

        _overlayPanelWindow = new OverlayPanelWindow(_items, _macroItems);

        _remapItems = new ObservableCollection<RemapItemViewModel>(settings.Remaps.Select(CreateRemapItem));
        _remapView = new ListCollectionView(_remapItems) { Filter = FilterRemap };
        RemapList.ItemsSource = _remapView;

        // Clickable overlay panel: the row positions are refreshed while overlay mode is on.
        _panelTargetsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _panelTargetsTimer.Tick += (_, _) => PublishPanelTargets();

        _tray = new TrayIcon(App.DisplayTitle);
        _tray.Open += ShowFromTray;
        _tray.MenuRequested += ShowTrayMenu;

        // Save the opacity once the slider stops moving, not on every step.
        _saveOpacityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveOpacityTimer.Tick += (_, _) => { _saveOpacityTimer.Stop(); Save(); };
        OpacitySlider.Value = settings.OverlayPanelOpacity;
        ApplyOpacity(settings.OverlayPanelOpacity);

        _saveVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveVolumeTimer.Tick += (_, _) => { _saveVolumeTimer.Stop(); Save(); };
        VolumeSlider.Value = settings.SoundVolume;
        VolumeValueText.Text = $"{settings.SoundVolume}%";

        HotkeysGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Hotkeys");
        OverlayGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Overlay");
        SoundGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Sound");
        UpdatePositionBoxes();

        ClickInPanelSwitch.IsOn = settings.ClickItemsInPanel;
        ShowPanelSwitch.IsOn = settings.ShowOverlayPanel;
        SetPanelOptionsEnabled(settings.ShowOverlayPanel);
        CloseToTraySwitch.IsOn = settings.CloseToTray;
        StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        if (App.IsDevBuild)
        {
            // The Run entry belongs to the installed app.
            StartWithWindowsSwitch.IsEnabled = false;
            StartWithWindowsText.Text = "Not available in the development build";
        }
        InitializeUpdates();
        Rail.PageChanged += OnPageChanged;
        RefreshUpdateUi();
        BackupGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Backup");
        AboutGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("About");
        DataFolderText.Text = $"Data saved in {AppPaths.DataFolder}";
        RemapFilterBox.SelectedIndex = 0;
        UpdateRemapState();
        FilterBox.SelectedIndex = (int)settings.Filter;
        MacroFilterBox.SelectedIndex = 0;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        UpdateHotkeyLabels();
        UpdateLoopState();
        RestorePlacement();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        _initializing = false;
    }

    // ================= Lifecycle =================

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureEngineStarted();

    private void EnsureEngineStarted()
    {
        if (_engineStarted)
            return;
        _engineStarted = true;
        try
        {
            _engine.Start();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show($"Hotkeys are not available:\n\n{ex.Message}", "PuppyMacro",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Started with Windows: hotkeys work, the window stays in the system tray.</summary>
    internal void StartInTray() => EnsureEngineStarted();

    private void RememberPlacement()
    {
        if (IsVisible && WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Math.Round(ActualWidth);
            _settings.WindowHeight = Math.Round(ActualHeight);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_importing)
            return; // the imported settings.json must not be overwritten
        RememberPlacement();
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_settings.TrayNoticeShown)
            {
                _settings.TrayNoticeShown = true;
                _tray.Notify("PuppyMacro", "PuppyMacro is still running in the system tray. Right-click its icon to exit.");
            }
        }
        Save();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _engine.Dispose();
        _tray.Dispose();
        _overlayPanelWindow.Close();
        Application.Current.Shutdown();
    }

    /// <summary>Closes PuppyMacro for real (tray menu Exit, Import).</summary>
    internal void ExitApp()
    {
        _exiting = true;
        if (_overlayModeActive)
            ToggleOverlayMode();
        Close();
    }

    // ================= System tray =================

    /// <summary>Shows the window from the tray or overlay mode (tray icon, or PuppyMacro started again).</summary>
    internal void ShowFromTray()
    {
        if (_overlayModeActive)
            ToggleOverlayMode(); // also shows the window
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowTrayMenu()
    {
        var open = new MenuItem { Header = "Open PuppyMacro", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowFromTray();
        var stopAll = new MenuItem { Header = "Stop all", IsEnabled = _engine.AnyRunning };
        stopAll.Click += (_, _) => _engine.StopAll();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = App.DisplayTitle, IsEnabled = false });
        menu.Items.Add(open);
        menu.Items.Add(stopAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) =>
        {
            // Without this, the menu would not close when clicking elsewhere.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                Native.NativeMethods.SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    private void RestorePlacement()
    {
        // The size kept from the last session (WindowFit makes it smaller when the screen is).
        if (_settings.WindowWidth is double width && _settings.WindowHeight is double height)
        {
            Width = Math.Max(MinWidth, width);
            Height = Math.Max(MinHeight, height);
        }

        if (_settings.WindowLeft is not double left || _settings.WindowTop is not double top)
            return;

        // Only restore if the title bar would still be on a screen.
        bool visible =
            left >= SystemParameters.VirtualScreenLeft - 100 &&
            top >= SystemParameters.VirtualScreenTop &&
            left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
        if (!visible)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    // ================= Navigation =================

    private void OnPageChanged(string page)
    {
        LoopsPage.Visibility = page == "Loops" ? Visibility.Visible : Visibility.Collapsed;
        MacrosPage.Visibility = page == "Macros" ? Visibility.Visible : Visibility.Collapsed;
        RemapsPage.Visibility = page == "Remap" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page != "Settings")
            _engine.CancelCapture(); // a Settings hotkey field that waits stops
    }

    /// <summary>The overlay panel's options are off while the panel is off.</summary>
    private void SetPanelOptionsEnabled(bool enabled)
    {
        OpacityCard.IsEnabled = enabled;
        PositionCard.IsEnabled = enabled;
        ClickInPanelCard.IsEnabled = enabled;
        ClickInPanelWarningCard.IsEnabled = enabled;
    }

    // ================= Loops list =================

    private LoopItemViewModel CreateItem(LoopDefinition definition)
    {
        var item = new LoopItemViewModel(definition);
        item.EnabledChanged += OnLoopEnabledChanged;
        return item;
    }

    private bool FilterLoop(object obj)
    {
        var item = (LoopItemViewModel)obj;

        string search = SearchBox.Text?.Trim() ?? "";
        if (search.Length > 0 && item.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
            return false;

        return _settings.Filter switch
        {
            LoopFilter.Enabled => item.IsLoopEnabled,
            LoopFilter.Disabled => !item.IsLoopEnabled,
            LoopFilter.Running => item.IsRunning,
            _ => true,
        };
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
            return;
        RefreshList();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || FilterBox.SelectedIndex < 0)
            return;
        _settings.Filter = (LoopFilter)FilterBox.SelectedIndex;
        RefreshList();
        Save();
    }

    private void RefreshList()
    {
        _view.Refresh();
        UpdateLoopState();
    }

    /// <summary>
    /// The card's switch. The list is rebuilt only when its filter depends on it (Enabled / Disabled): a rebuild
    /// recreates every card, so the switch would jump in the middle of its animation and the list flicker.
    /// Otherwise the card follows through its bindings (stopping a running loop refreshes the Running filter).
    /// </summary>
    private void OnLoopEnabledChanged(LoopItemViewModel item)
    {
        if (!item.IsLoopEnabled)
            _engine.StopLoop(item.Definition.Id);
        if (_settings.Filter is LoopFilter.Enabled or LoopFilter.Disabled)
            _view.Refresh();
        UpdateLoopState();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    private void OnEngineStateChanged()
    {
        foreach (var item in _items)
            item.IsRunning = _engine.IsRunning(item.Definition.Id);
        foreach (var item in _macroItems)
            item.IsRunning = _engine.IsRunning(item.Definition.Id);
        if (_settings.Filter == LoopFilter.Running)
            _view.Refresh();
        if (MacroFilterBox.SelectedIndex == (int)LoopFilter.Running)
            _macroView.Refresh();
        UpdateLoopState();
        UpdatePanelState();
    }

    /// <summary>The overlay panel's Stop all (red while something runs) and exit button.</summary>
    private void UpdatePanelState() => _overlayPanelWindow.SetState(_engine.AnyRunning, PanelClickable);

    /// <summary>Count line, empty state, and the buttons that depend on running loops.</summary>
    private void UpdateLoopState()
    {
        int total = _items.Count;
        int running = _items.Count(i => i.IsRunning);
        CountText.Text = $"{total} {(total == 1 ? "loop" : "loops")}, {running} running";

        if (total == 0)
        {
            EmptyLoopsText.Text = "No loops yet. Select Add loop to create one.";
            EmptyLoopsText.Visibility = Visibility.Visible;
        }
        else if (_view.Count == 0)
        {
            EmptyLoopsText.Text = "No loops match the search or filter.";
            EmptyLoopsText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyLoopsText.Visibility = Visibility.Collapsed;
        }

        bool anyRunning = _engine.AnyRunning;
        AddLoopButton.IsEnabled = !anyRunning;
        AddLoopButton.ToolTip = anyRunning ? "Stop running loops and macros to add a new one" : null;
        NewMacroButton.IsEnabled = !anyRunning;
        RecordMacroButton.IsEnabled = !anyRunning;
        NewMacroButton.ToolTip = RecordMacroButton.ToolTip = anyRunning ? "Stop running loops and macros first" : null;
        StopAllButton.IsEnabled = anyRunning;
        foreach (var loop in _items)
            loop.CanEdit = !anyRunning;
        foreach (var macro in _macroItems)
            macro.CanEdit = !anyRunning;
        UpdateMacroState();
    }

    private void OnAddLoopClick(object sender, RoutedEventArgs e)
    {
        if (_engine.AnyRunning)
            return;

        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, null, PlaceFromEditor) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        _settings.Loops.Add(editor.Result);
        _items.Add(CreateItem(editor.Result));
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    /// <summary>The card's Edit (not while loops or macros run).</summary>
    private void OnEditLoopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LoopItemViewModel item && !_engine.AnyRunning)
            EditLoop(item);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not LoopItemViewModel item)
            return;

        var delete = new MenuItem { Header = "Delete", Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteLoop(item);

        int index = _items.IndexOf(item);
        var moveUp = new MenuItem
        {
            Header = "Move up",
            Icon = new FontIcon { Icon = SegoeFluentIcons.Up },
            IsEnabled = index > 0,
        };
        moveUp.Click += (_, _) => MoveItem(item, index - 1);
        var moveDown = new MenuItem
        {
            Header = "Move down",
            Icon = new FontIcon { Icon = SegoeFluentIcons.Down },
            IsEnabled = index < _items.Count - 1,
        };
        moveDown.Click += (_, _) => MoveItem(item, index + 1);

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void EditLoop(LoopItemViewModel item)
    {
        if (_engine.AnyRunning)
            return;

        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, item.Definition, PlaceFromEditor) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        _engine.StopLoop(item.Definition.Id);
        item.Definition.CopyFrom(editor.Result);
        item.Refresh();
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    private async void DeleteLoop(LoopItemViewModel item)
    {
        var confirm = new ContentDialog
        {
            Title = "Delete loop",
            Content = $"Delete \"{item.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowAsync(this);
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != ContentDialogResult.Primary)
            return;

        _engine.StopLoop(item.Definition.Id);
        _settings.Loops.Remove(item.Definition);
        _items.Remove(item);
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    // ================= Macros =================

    private MacroItemViewModel CreateMacroItem(MacroDefinition definition)
    {
        var item = new MacroItemViewModel(definition);
        item.EnabledChanged += OnMacroEnabledChanged;
        return item;
    }

    private bool FilterMacro(object obj)
    {
        var item = (MacroItemViewModel)obj;
        string search = MacroSearchBox.Text?.Trim() ?? "";
        if (search.Length > 0 && item.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
            return false;
        return (LoopFilter)Math.Max(0, MacroFilterBox.SelectedIndex) switch
        {
            LoopFilter.Enabled => item.IsLoopEnabled,
            LoopFilter.Disabled => !item.IsLoopEnabled,
            LoopFilter.Running => item.IsRunning,
            _ => true,
        };
    }

    private void OnMacroSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing)
            RefreshMacros();
    }

    private void OnMacroFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && _macroView != null)
            RefreshMacros();
    }

    private void RefreshMacros()
    {
        _macroView.Refresh();
        UpdateMacroState();
    }

    private void UpdateMacroState()
    {
        if (MacroCountText == null || _macroItems == null)
            return;
        int total = _macroItems.Count;
        int running = _macroItems.Count(m => m.IsRunning);
        MacroCountText.Text = $"{total} {(total == 1 ? "macro" : "macros")}, {running} running";

        if (total == 0)
        {
            EmptyMacrosText.Text = "No macros yet. Select Record macro, or New macro to build one by hand.";
            EmptyMacrosText.Visibility = Visibility.Visible;
        }
        else if (_macroView.Count == 0)
        {
            EmptyMacrosText.Text = "No macros match the search or filter.";
            EmptyMacrosText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyMacrosText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The card's switch: the list is rebuilt only when its filter depends on it (see OnLoopEnabledChanged).</summary>
    private void OnMacroEnabledChanged(MacroItemViewModel item)
    {
        if (!item.IsLoopEnabled)
            _engine.StopMacro(item.Definition.Id);
        if ((LoopFilter)Math.Max(0, MacroFilterBox.SelectedIndex) is LoopFilter.Enabled or LoopFilter.Disabled)
            _macroView.Refresh();
        UpdateMacroState();
        _overlayPanelWindow.RefreshList();
        SaveMacro(item.Definition);
    }

    private void OnNewMacroClick(object sender, RoutedEventArgs e) => OpenNewMacroEditor(null);

    private void OnRecordMacroClick(object sender, RoutedEventArgs e)
    {
        if (_engine.AnyRunning)
            return;
        RecordingSession.Run(_engine, _settings,
            hide: Hide,
            restore: () =>
            {
                Show();
                Activate();
            },
            done: actions =>
            {
                Save(); // remembers the "Record mouse movement" choice
                OpenNewMacroEditor(actions);
            });
    }

    private void OpenNewMacroEditor(System.Collections.Generic.List<MacroAction>? recorded)
    {
        if (_engine.AnyRunning)
            return;
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, null, PlaceFromEditor, recorded) { Owner = this };
        if (ShowMacroEditor(editor) != true || editor.Result == null)
            return;

        _macros.Macros.Add(editor.Result);
        _macroItems.Add(CreateMacroItem(editor.Result));
        SaveMacro(editor.Result);
        SaveMacroOrder();
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    /// <summary>The card's Edit (not while loops or macros run).</summary>
    private void OnEditMacroClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MacroItemViewModel item && !_engine.AnyRunning)
            EditMacro(item);
    }

    private void OnMacroMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not MacroItemViewModel item)
            return;

        int index = _macroItems.IndexOf(item);
        var moveUp = new MenuItem { Header = "Move up", Icon = new FontIcon { Icon = SegoeFluentIcons.Up }, IsEnabled = index > 0 };
        moveUp.Click += (_, _) => MoveMacro(item, index - 1);
        var moveDown = new MenuItem { Header = "Move down", Icon = new FontIcon { Icon = SegoeFluentIcons.Down }, IsEnabled = index < _macroItems.Count - 1 };
        moveDown.Click += (_, _) => MoveMacro(item, index + 1);
        var delete = new MenuItem { Header = "Delete", Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteMacro(item);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void EditMacro(MacroItemViewModel item)
    {
        if (_engine.AnyRunning)
            return;
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, item.Definition, PlaceFromEditor) { Owner = this };
        if (ShowMacroEditor(editor) != true || editor.Result == null)
            return;

        item.Definition.CopyFrom(editor.Result);
        item.Refresh();
        SaveMacro(item.Definition);
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    private async void DeleteMacro(MacroItemViewModel item)
    {
        var confirm = new ContentDialog
        {
            Title = "Delete macro",
            Content = $"Delete \"{item.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowAsync(this);
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != ContentDialogResult.Primary)
            return;

        _engine.StopMacro(item.Definition.Id);
        _macros.Delete(item.Definition);
        _macroItems.Remove(item);
        SaveMacroOrder();
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    private void MoveMacro(MacroItemViewModel item, int newIndex)
    {
        int oldIndex = _macroItems.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, _macroItems.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
            return;
        _macroItems.Move(oldIndex, newIndex);
        _macros.Macros.Clear();
        _macros.Macros.AddRange(_macroItems.Select(m => m.Definition));
        SaveMacroOrder();
        _macroView.Refresh();
        _overlayPanelWindow.RefreshList();
    }

    private void OnMacroStartStopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroItemViewModel item)
            return;
        if (!item.IsLoopEnabled && !_engine.IsRunning(item.Definition.Id))
            return;
        _engine.ToggleMacro(item.Definition);
    }

    private void SaveMacro(MacroDefinition macro)
    {
        UpdateSettingsSummaries(); // the floating button count may have changed
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_macros.TrySave(macro, out string? error))
            ShowSettingsError($"Could not save the macro \"{macro.Name}\": {error}");
    }

    private void SaveMacroOrder()
    {
        _settings.MacroOrder = _macros.Macros.Select(m => m.Id).ToList();
        Save();
    }

    // ================= Clickable overlay panel =================

    private void OnClickInPanelChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.ClickItemsInPanel = ClickInPanelSwitch.IsOn;
        UpdatePanelClicks();
        UpdateSettingsSummaries();
        Save();
    }

    private void OnShowPanelChanged(object sender, RoutedEventArgs e)
    {
        bool show = ShowPanelSwitch.IsOn;
        SetPanelOptionsEnabled(show);
        if (_initializing)
            return;
        _settings.ShowOverlayPanel = show;
        if (_overlayModeActive)
        {
            if (show)
                ShowOverlayPanel();
            else
                _overlayPanelWindow.Hide();
            UpdatePanelClicks();
        }
        UpdateSettingsSummaries();
        Save();
    }

    /// <summary>The overlay panel's rows are clickable: it is shown and the setting is on.</summary>
    private bool PanelClickable => _settings.ShowOverlayPanel && _settings.ClickItemsInPanel;

    /// <summary>
    /// Clicks on the panel and on the floating buttons are handled by the input hook, so the fullscreen app
    /// never loses focus. Floating buttons are always clickable; the panel only when it is shown and the setting is on.
    /// </summary>
    private void UpdatePanelClicks()
    {
        UpdatePanelState();
        if (_overlayModeActive && (PanelClickable || _buttonWindows.Count > 0))
        {
            // Rows are measured once the panel is on screen, then kept up to date.
            Dispatcher.InvokeAsync(PublishPanelTargets, DispatcherPriority.Loaded);
            _panelTargetsTimer.Start();
        }
        else
        {
            _panelTargetsTimer.Stop();
            _engine.SetPanelTargets(null);
        }
    }

    private void PublishPanelTargets()
    {
        if (!_overlayModeActive)
            return;
        var targets = PanelClickable ? _overlayPanelWindow.GetClickTargets() : new List<PanelTarget>();
        foreach (var window in _buttonWindows)
        {
            if (window.GetClickTarget() is PanelTarget target)
                targets.Add(target);
        }
        _engine.SetPanelTargets(targets);
    }

    // ================= Reordering =================

    /// <summary>Moves a loop to <paramref name="newIndex"/> in the full list and saves the order.</summary>
    private void MoveItem(LoopItemViewModel item, int newIndex)
    {
        int oldIndex = _items.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, _items.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
            return;

        _items.Move(oldIndex, newIndex);
        _settings.Loops = _items.Select(i => i.Definition).ToList();
        _view.Refresh();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBox or ComboBox or Slider)
                return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject))
            return;
        if ((sender as FrameworkElement)?.Tag is IListItem item)
        {
            _dragCandidate = item;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragCandidate;
        _dragCandidate = null;
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(IListItem), item), DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
            ClearDropIndicators();
        }
    }

    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(IListItem)) is not IListItem dragged
            || sender is not FrameworkElement card || card.Tag is not IListItem target
            || ReferenceEquals(dragged, target) || dragged.GetType() != target.GetType())
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicators();
            return;
        }

        e.Effects = DragDropEffects.Move;
        bool before = e.GetPosition(card).Y < card.ActualHeight / 2;
        foreach (var other in AllListItems())
        {
            other.DropBefore = ReferenceEquals(other, target) && before;
            other.DropAfter = ReferenceEquals(other, target) && !before;
        }
    }

    private void OnCardDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IListItem target)
        {
            target.DropBefore = false;
            target.DropAfter = false;
        }
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(IListItem)) is not IListItem dragged
            || sender is not FrameworkElement card || card.Tag is not IListItem target
            || ReferenceEquals(dragged, target) || dragged.GetType() != target.GetType())
        {
            ClearDropIndicators();
            return;
        }

        bool before = e.GetPosition(card).Y < card.ActualHeight / 2;
        ClearDropIndicators();

        if (dragged is LoopItemViewModel loop && target is LoopItemViewModel loopTarget)
        {
            int from = _items.IndexOf(loop);
            int to = _items.IndexOf(loopTarget) + (before ? 0 : 1);
            if (from < to)
                to--; // removing the dragged item shifts the target up by one
            MoveItem(loop, to);
        }
        else if (dragged is MacroItemViewModel macro && target is MacroItemViewModel macroTarget)
        {
            int from = _macroItems.IndexOf(macro);
            int to = _macroItems.IndexOf(macroTarget) + (before ? 0 : 1);
            if (from < to)
                to--;
            MoveMacro(macro, to);
        }
    }

    private System.Collections.Generic.IEnumerable<IListItem> AllListItems() =>
        _items.Cast<IListItem>().Concat(_macroItems);

    private void ClearDropIndicators()
    {
        foreach (var item in AllListItems())
        {
            item.DropBefore = false;
            item.DropAfter = false;
        }
    }

    /// <summary>Shows the macro editor, then saves the settings: the editor keeps its size there.</summary>
    private bool? ShowMacroEditor(MacroEditorWindow editor)
    {
        bool? result = ShowDialogWithoutHotkeys(editor);
        Save();
        return result;
    }

    /// <summary>Shows a dialog with every hotkey ignored until it closes.</summary>
    /// <summary>
    /// Shows an editor (or the placement overlay) with the hotkeys off. While it is open this window is only
    /// hidden, so it takes no room on the screen, and shown again as it was when the editor closes. It hides
    /// once the editor is on screen, so the editor still opens centered on it.
    /// </summary>
    private bool? ShowDialogWithoutHotkeys(Window dialog)
    {
        // Restores the previous state: the placement overlay can open from an editor.
        bool wasSuspended = _engine.HotkeysSuspended;
        _engine.HotkeysSuspended = true;
        bool wasVisible = IsVisible;
        if (wasVisible)
            dialog.ContentRendered += (_, _) => Hide();
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            _engine.HotkeysSuspended = wasSuspended;
            if (wasVisible)
            {
                Show();
                Activate();
            }
        }
    }

    private void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LoopItemViewModel item)
            return;

        if (!item.IsLoopEnabled && !_engine.IsRunning(item.Definition.Id))
            return; // disabled loops cannot be started

        _engine.ToggleLoop(item.Definition);
    }

    private void OnStopAllClick(object sender, RoutedEventArgs e) => _engine.StopAll();

    private void OnOverlayModeClick(object sender, RoutedEventArgs e) => ToggleOverlayMode();

    // ================= Settings =================

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeComboBox.SelectedIndex < 0)
            return;
        _settings.Theme = (AppTheme)ThemeComboBox.SelectedIndex;
        App.ApplyTheme(_settings.Theme);
        Save();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null || _overlayPanelWindow == null)
            return; // still loading

        int percent = (int)Math.Round(e.NewValue);
        ApplyOpacity(percent);
        if (_initializing || percent == _settings.OverlayPanelOpacity)
            return;

        _settings.OverlayPanelOpacity = percent;
        UpdateSettingsSummaries();
        _saveOpacityTimer.Stop();
        _saveOpacityTimer.Start();
    }

    private void ApplyOpacity(int percent)
    {
        OpacityValueText.Text = $"{percent}%";
        byte alpha = (byte)Math.Round(percent * 255 / 100.0);
        OpacityPreviewPanel.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
        _overlayPanelWindow.SetBackgroundOpacity(percent);
    }

    private void OnGroupToggled(object? sender, EventArgs e)
    {
        if (_initializing || sender is not SettingsExpander { Tag: string key } group)
            return;
        var open = _settings.ExpandedSettingsGroups;
        open.Remove(key);
        if (group.IsExpanded)
            open.Add(key);
        Save();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueText == null || _sounds == null)
            return; // still loading

        int percent = (int)Math.Round(e.NewValue);
        VolumeValueText.Text = $"{percent}%";
        _sounds.Volume = percent;
        UpdateSettingsSummaries();
        if (_initializing || percent == _settings.SoundVolume)
            return;

        _settings.SoundVolume = percent;
        _saveVolumeTimer.Stop();
        _saveVolumeTimer.Start();
    }

    /// <summary>Plays a short sample when the user lets go of the volume slider.</summary>
    private void OnVolumeReleased(object sender, MouseButtonEventArgs e) =>
        _sounds.Play(SoundService.Names[0], start: true);

    // ---- Overlay mode position ----

    /// <summary>Default: top-left of the main screen's work area, with a small margin.</summary>
    private static Point DefaultPanelPosition() =>
        new(SystemParameters.WorkArea.Left + 20, SystemParameters.WorkArea.Top + 20);

    /// <summary>Saved position, or the default if none or if it is no longer on any screen.</summary>
    private Point PanelPosition()
    {
        if (_settings.OverlayPanelX is not double x || _settings.OverlayPanelY is not double y)
            return DefaultPanelPosition();

        bool onScreen =
            x >= SystemParameters.VirtualScreenLeft - 40 &&
            y >= SystemParameters.VirtualScreenTop - 20 &&
            x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
            y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        return onScreen ? new Point(x, y) : DefaultPanelPosition();
    }

    private bool _updatingPositionBoxes;

    private void UpdatePositionBoxes()
    {
        Point p = PanelPosition();
        _updatingPositionBoxes = true;
        PositionXBox.Value = Math.Round(p.X);
        PositionYBox.Value = Math.Round(p.Y);
        _updatingPositionBoxes = false;
        UpdateSettingsSummaries();
    }

    private void OnPositionBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || _updatingPositionBoxes)
            return;
        double x = PositionXBox.Value, y = PositionYBox.Value;
        if (!double.IsNaN(x) && !double.IsNaN(y))
        {
            _settings.OverlayPanelX = x;
            _settings.OverlayPanelY = y;
            UpdateSettingsSummaries();
            Save();
        }
    }

    private void OnPositionOnScreenClick(object sender, RoutedEventArgs e) => OpenPlacement(null);

    /// <summary>
    /// The editors' Position… button: places the whole overlay. The edited item is shown as in the
    /// editor (its button is left out when off); its new position is returned to the editor, which
    /// saves it with the item. Null when cancelled.
    /// </summary>
    private Point? PlaceFromEditor(EditedFloatingButton edited) => OpenPlacement(edited);

    /// <summary>
    /// Opens the placement overlay with the whole overlay as overlay mode shows it: the overlay
    /// panel (when shown) and every floating button, plus the one being edited.
    /// </summary>
    private Point? OpenPlacement(EditedFloatingButton? edited)
    {
        var others = FloatingButtonItems().Where(i => i.Id != edited?.Id).ToList();
        var sources = others.Select(i => (SavedPosition(i.FloatingButton), i.FloatingButton.Size)).ToList();
        bool editingShown = edited is { Button.Enabled: true };
        if (editingShown)
            sources.Add((SavedPosition(edited!.Button), edited.Button.Size));
        List<Point> positions = ButtonPositions(sources);

        var buttons = new List<PlacementButton>();
        for (int index = 0; index < others.Count; index++)
        {
            IListItem item = others[index];
            buttons.Add(new PlacementButton(item.Id, item.Name, FloatingButtonWindow.LabelOf(item),
                item.HasHotkey ? item.HotkeyText : "", item.FloatingButton.Size, item.FloatingButton.EffectiveOpacity, positions[index]));
        }
        if (editingShown)
        {
            FloatingButton button = edited!.Button;
            buttons.Add(new PlacementButton(edited.Id, edited.Name,
                string.IsNullOrEmpty(button.Label) ? FloatingButton.DefaultLabel(edited.Name) : button.Label,
                edited.HotkeyText, button.Size, button.EffectiveOpacity, positions[^1]));
        }
        if (!_settings.ShowOverlayPanel && buttons.Count == 0)
        {
            ShowSettingsError("Nothing to position: the overlay panel is off and there are no floating buttons.");
            return null;
        }

        var placement = new PlacementWindow(_items, _macroItems, _settings.ShowOverlayPanel, PanelPosition(), _settings.OverlayPanelOpacity,
            KeyNames.Format(_settings.OverlayModeHotkey), KeyNames.Format(_settings.StopAllHotkey), buttons, edited?.Id);
        if (ShowDialogWithoutHotkeys(placement) != true)
            return null;

        if (_settings.ShowOverlayPanel)
        {
            _settings.OverlayPanelX = Math.Round(placement.Result.X);
            _settings.OverlayPanelY = Math.Round(placement.Result.Y);
            UpdatePositionBoxes();
        }

        foreach (IListItem item in others)
        {
            if (!placement.ButtonResults.TryGetValue(item.Id, out Point p))
                continue;
            item.FloatingButton.X = Math.Round(p.X);
            item.FloatingButton.Y = Math.Round(p.Y);
            if (item is MacroItemViewModel macro)
                SaveMacro(macro.Definition);
        }
        Save();
        return edited != null && placement.ButtonResults.TryGetValue(edited.Id, out Point moved)
            ? new Point(Math.Round(moved.X), Math.Round(moved.Y))
            : null;
    }

    // ---- Floating buttons ----

    /// <summary>Loops then macros that show a floating button in overlay mode, in list order.</summary>
    private IEnumerable<IListItem> FloatingButtonItems() =>
        _items.Cast<IListItem>().Concat(_macroItems).Where(FloatingButtons.HasButton);

    private static Point? SavedPosition(FloatingButton button) =>
        button.X is double x && button.Y is double y ? new Point(x, y) : null;

    private static bool IsOnScreen(Point p, double diameter) =>
        p.X >= SystemParameters.VirtualScreenLeft &&
        p.Y >= SystemParameters.VirtualScreenTop &&
        p.X <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - diameter &&
        p.Y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - diameter;

    /// <summary>
    /// Positions of the given buttons: the saved one when it is still on a screen; the others
    /// (new or off screen) in the middle of the main screen's work area, side by side, so a new
    /// button is easy to find.
    /// </summary>
    private static List<Point> ButtonPositions(IEnumerable<(Point? Saved, FloatingButtonSize Size)> buttons)
    {
        const double slot = 72; // the largest button plus a gap
        Rect work = SystemParameters.WorkArea;
        int perRow = Math.Max(1, (int)((work.Width / 2 - 20) / slot));
        int unplaced = 0;
        var positions = new List<Point>();
        foreach (var (saved, size) in buttons)
        {
            double diameter = FloatingButton.DiameterOf(size);
            if (saved is Point p && IsOnScreen(p, diameter))
            {
                positions.Add(p);
                continue;
            }
            int index = unplaced++;
            positions.Add(new Point(
                work.Left + work.Width / 2 - diameter / 2 + slot * (index % perRow),
                work.Top + work.Height / 2 - diameter / 2 + slot * (index / perRow)));
        }
        return positions;
    }

    /// <summary>One window per floating button; only those for all apps and for the app in front are shown.</summary>
    private void ShowFloatingButtons()
    {
        var items = FloatingButtonItems().ToList();
        var positions = ButtonPositions(items.Select(i => (SavedPosition(i.FloatingButton), i.FloatingButton.Size)));
        for (int index = 0; index < items.Count; index++)
        {
            IListItem item = items[index];
            var window = new FloatingButtonWindow(item, positions[index], item.FloatingButton.EffectiveOpacity);
            _buttonWindows.Add(window);
        }
        ShowFloatingButtonsFor(_engine.ForegroundApp);
    }

    /// <summary>Shows the floating buttons for all apps and for <paramref name="appExe"/>; hidden ones cannot be clicked.</summary>
    private void ShowFloatingButtonsFor(string? appExe)
    {
        foreach (var window in _buttonWindows)
        {
            if (AppScope.Allows(window.Item.AppExe, appExe))
                window.Show();
            else
                window.Hide();
        }
    }

    /// <summary>Another app came in front: the overlay shows its loops and macros and those for all apps.</summary>
    private void OnForegroundAppChanged(string appExe)
    {
        _overlayPanelWindow.SetApp(appExe);
        if (_overlayModeActive)
            ShowFloatingButtonsFor(appExe);
    }

    private void CloseFloatingButtons()
    {
        foreach (var window in _buttonWindows)
            window.Close();
        _buttonWindows.Clear();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _settings.OverlayPanelX = null;
        _settings.OverlayPanelY = null;
        UpdatePositionBoxes();
        Save();
    }

    private void UpdateSettingsSummaries()
    {
        if (HotkeysSummary == null)
            return;
        HotkeysSummary.Text = $"Stop all {KeyNames.Format(_settings.StopAllHotkey)}, Overlay mode {KeyNames.Format(_settings.OverlayModeHotkey)}, Record {KeyNames.Format(_settings.RecordHotkey)}";
        int buttons = FloatingButtonItems().Count();
        string buttonsText = buttons switch { 0 => "no floating buttons", 1 => "1 floating button", _ => $"{buttons} floating buttons" };
        if (_settings.ShowOverlayPanel)
        {
            Point p = PanelPosition();
            OverlaySummary.Text = $"Panel: opacity {_settings.OverlayPanelOpacity}%, position X {Math.Round(p.X)}, Y {Math.Round(p.Y)}, click to start {(_settings.ClickItemsInPanel ? "on" : "off")}; {buttonsText}";
        }
        else
        {
            OverlaySummary.Text = $"Panel off; {buttonsText}";
        }
        FloatingButtonsText.Text = (buttons switch
        {
            0 => "None yet.",
            1 => "1 loop or macro has a floating button.",
            _ => $"{buttons} loops and macros have a floating button.",
        }) + " Turn one on with Floating button in the loop or macro editor, where you also set its label, size and opacity.";
        SoundSummary.Text = $"Volume {_settings.SoundVolume}%";
    }

    /// <summary>The three global hotkey fields: each checks a pressed key against the rules and the other hotkeys.</summary>
    private void SetUpGlobalHotkeyFields()
    {
        foreach (var (field, which) in new[] { (StopAllHotkeyField, "StopAll"), (OverlayModeHotkeyField, "OverlayMode"), (RecordHotkeyField, "Record") })
            field.Validate = binding => HotkeyRules.Problem(binding, hold: false)
                ?? HotkeyConflicts.Find(binding, _settings, _macros, ignoreGlobal: which);
    }

    private void OnGlobalHotkeyStarted(object? sender, EventArgs e) => HideSettingsError();

    private void OnGlobalHotkeyChanged(object? sender, EventArgs e)
    {
        if ((sender as KeyCaptureField)?.Value?.Clone() is not { } binding)
            return;
        if (sender == StopAllHotkeyField)
            _settings.StopAllHotkey = binding;
        else if (sender == OverlayModeHotkeyField)
            _settings.OverlayModeHotkey = binding;
        else
            _settings.RecordHotkey = binding;
        UpdateHotkeyLabels();
        Save();
    }

    private void UpdateHotkeyLabels()
    {
        string stopAll = KeyNames.Format(_settings.StopAllHotkey);
        string overlayMode = KeyNames.Format(_settings.OverlayModeHotkey);
        StopAllHotkeyField.Value = _settings.StopAllHotkey?.Clone();
        OverlayModeHotkeyField.Value = _settings.OverlayModeHotkey?.Clone();
        RecordHotkeyField.Value = _settings.RecordHotkey?.Clone();
        StopAllButtonHotkeyText.Text = stopAll;
        OverlayModeButtonHotkeyText.Text = overlayMode;
        _overlayPanelWindow.SetHotkeyLabels(overlayMode, stopAll);
        UpdateSettingsSummaries();
    }

    private void ShowSettingsError(string message)
    {
        SettingsError.Message = message;
        SettingsError.IsOpen = true;
    }

    private void HideSettingsError() => SettingsError.IsOpen = false;

    // ================= Overlay mode =================

    private Point _panelDragStart;

    /// <summary>
    /// The overlay panel's move handle (the input hook reports how far the mouse is from where it was pressed,
    /// in physical pixels): the panel follows, and where it is released becomes its saved position.
    /// </summary>
    private void OnOverlayPanelDragged(PanelDragPhase phase, int dx, int dy)
    {
        if (phase == PanelDragPhase.Started)
        {
            _panelDragStart = new Point(_overlayPanelWindow.Left, _overlayPanelWindow.Top);
            return;
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(_overlayPanelWindow);
        _overlayPanelWindow.Left = _panelDragStart.X + dx / dpi.DpiScaleX;
        _overlayPanelWindow.Top = _panelDragStart.Y + dy / dpi.DpiScaleY;
        if (phase != PanelDragPhase.Ended)
            return;
        _settings.OverlayPanelX = Math.Round(_overlayPanelWindow.Left);
        _settings.OverlayPanelY = Math.Round(_overlayPanelWindow.Top);
        UpdatePositionBoxes();
        Save();
        PublishPanelTargets(); // the clickable rectangles moved with the panel
    }

    private void ShowOverlayPanel()
    {
        Point position = PanelPosition();
        _overlayPanelWindow.Left = position.X;
        _overlayPanelWindow.Top = position.Y;
        _overlayPanelWindow.Show();
    }

    /// <summary>How the main window was when overlay mode started, so leaving it gives that back.</summary>
    private enum WindowBeforeOverlay { Open, Minimized, InTray }

    private WindowBeforeOverlay _windowBeforeOverlay;

    private void ToggleOverlayMode()
    {
        if (!_overlayModeActive)
        {
            _engine.CancelCapture();
            _windowBeforeOverlay = !IsVisible ? WindowBeforeOverlay.InTray
                : WindowState == WindowState.Minimized ? WindowBeforeOverlay.Minimized
                : WindowBeforeOverlay.Open;
            Hide();
            _overlayPanelWindow.SetApp(_engine.ForegroundApp);
            if (_settings.ShowOverlayPanel)
                ShowOverlayPanel();
            ShowFloatingButtons();
            _overlayModeActive = true;
            UpdatePanelClicks();
        }
        else
        {
            _overlayPanelWindow.Hide();
            CloseFloatingButtons();
            _overlayModeActive = false;
            UpdatePanelClicks();
            // The window goes back to how it was: still in the tray, minimized, or open. Open, it
            // comes to the front: it is not topmost, so it would otherwise stay behind the fullscreen app.
            switch (_windowBeforeOverlay)
            {
                case WindowBeforeOverlay.Open:
                    Show();
                    Activate();
                    break;
                case WindowBeforeOverlay.Minimized:
                    Show(); // still minimized, on the taskbar
                    break;
            }
        }
    }

    // ================= Persistence =================

    private void Save()
    {
        UpdateSettingsSummaries(); // the floating button count may have changed
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_store.TrySave(_settings, out string? error))
            ShowSettingsError($"Could not save settings.json: {error}");
    }

    // ================= Remap =================

    private RemapItemViewModel CreateRemapItem(RemapDefinition definition)
    {
        var item = new RemapItemViewModel(definition);
        // The card's switch: the list is rebuilt only when its filter depends on it (see OnLoopEnabledChanged).
        item.EnabledChanged += _ =>
        {
            if (RemapFilterBox.SelectedIndex is 1 or 2) // Enabled, Disabled
                _remapView.Refresh();
            UpdateRemapState();
            Save();
        };
        return item;
    }

    private bool FilterRemap(object obj)
    {
        var item = (RemapItemViewModel)obj;
        string search = RemapSearchBox.Text?.Trim() ?? "";
        if (search.Length > 0 && item.SearchText.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
            return false;
        return Math.Max(0, RemapFilterBox.SelectedIndex) switch
        {
            1 => item.IsLoopEnabled,
            2 => !item.IsLoopEnabled,
            _ => true,
        };
    }

    private void OnRemapSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing)
            RefreshRemaps();
    }

    private void OnRemapFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && _remapView != null)
            RefreshRemaps();
    }

    private void RefreshRemaps()
    {
        _remapView.Refresh();
        UpdateRemapState();
    }

    private void UpdateRemapState()
    {
        if (RemapCountText == null || _remapItems == null)
            return;
        int total = _remapItems.Count;
        int enabled = _remapItems.Count(r => r.IsLoopEnabled);
        RemapCountText.Text = $"{total} {(total == 1 ? "remap" : "remaps")}, {enabled} enabled";
        if (total == 0)
        {
            EmptyRemapsText.Text = "No remaps yet. Select Add remap to send another key when you press one.";
            EmptyRemapsText.Visibility = Visibility.Visible;
        }
        else if (_remapView.Count == 0)
        {
            EmptyRemapsText.Text = "No remaps match the search or filter.";
            EmptyRemapsText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyRemapsText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAddRemapClick(object sender, RoutedEventArgs e)
    {
        var editor = new RemapEditorWindow(_engine, _settings, _macros, null) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;
        _settings.Remaps.Add(editor.Result);
        _remapItems.Add(CreateRemapItem(editor.Result));
        RefreshRemaps();
        Save();
    }

    /// <summary>The card's Edit.</summary>
    private void OnEditRemapClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RemapItemViewModel item)
            EditRemap(item);
    }

    private void OnRemapMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not RemapItemViewModel item)
            return;

        var duplicate = new MenuItem { Header = "Duplicate", Icon = new FontIcon { Icon = SegoeFluentIcons.Copy } };
        duplicate.Click += (_, _) => DuplicateRemap(item);
        var delete = new MenuItem { Header = "Delete", Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteRemap(item);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void EditRemap(RemapItemViewModel item)
    {
        var editor = new RemapEditorWindow(_engine, _settings, _macros, item.Definition) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;
        item.Definition.CopyFrom(editor.Result);
        item.Refresh();
        RefreshRemaps();
        Save();
    }

    private void DuplicateRemap(RemapItemViewModel item)
    {
        // A copy with the same key and app would never run: it starts disabled, ready to be edited.
        var copy = item.Definition.Clone();
        copy.Id = Guid.NewGuid();
        copy.Enabled = false;
        int index = _settings.Remaps.IndexOf(item.Definition) + 1;
        _settings.Remaps.Insert(index, copy);
        _remapItems.Insert(_remapItems.IndexOf(item) + 1, CreateRemapItem(copy));
        RefreshRemaps();
        Save();
    }

    private async void DeleteRemap(RemapItemViewModel item)
    {
        var confirm = new ContentDialog
        {
            Title = "Delete remap",
            Content = $"Delete the remap {item.SourceText} → {item.TargetText}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowAsync(this);
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != ContentDialogResult.Primary)
            return;
        _settings.Remaps.Remove(item.Definition);
        _remapItems.Remove(item);
        RefreshRemaps();
        Save();
    }

    // ================= General settings and backup =================

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.CloseToTray = CloseToTraySwitch.IsOn;
        Save();
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.StartWithWindows = StartWithWindowsSwitch.IsOn;
        StartupService.Apply(_settings.StartWithWindows);
        Save();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        Save();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PuppyMacro data",
            FileName = $"PuppyMacro-{DateTime.Now:yyyy-MM-dd}{BackupService.Extension}",
            Filter = $"PuppyMacro backup (*{BackupService.Extension})|*{BackupService.Extension}",
            DefaultExt = BackupService.Extension,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            BackupService.Export(dialog.FileName);
            HideSettingsError();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowSettingsError($"Export failed: {ex.Message}");
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import PuppyMacro data",
            Filter = $"PuppyMacro backup (*{BackupService.Extension})|*{BackupService.Extension}",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var confirm = new ContentDialog
        {
            Title = "Import",
            Content = "Replace all your loops, macros, remaps and settings with the ones in this file? " +
                      "Your current data is saved first as a backup in the data folder. PuppyMacro restarts after the import.",
            PrimaryButtonText = "Import and restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync(this) != ContentDialogResult.Primary)
            return;

        try
        {
            _engine.StopAll();
            Save();
            BackupService.Import(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            ShowSettingsError($"Import failed: {ex.Message}");
            return;
        }
        _exiting = true;
        _importing = true;
        App.Restart();
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.DataFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\""));
    }
}
