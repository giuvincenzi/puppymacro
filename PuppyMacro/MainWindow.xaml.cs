using System;
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
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using UiSymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using UiSymbolRegular = Wpf.Ui.Controls.SymbolRegular;

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
    private readonly GameModeWindow _gameWindow;
    private readonly DispatcherTimer _saveOpacityTimer;
    private readonly DispatcherTimer _saveVolumeTimer;
    private readonly SoundService _sounds;
    private Point _dragStart;
    private IListItem? _dragCandidate;
    private readonly bool _initializing;
    private bool _gameModeActive;
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

        _store = store;
        _settings = settings;
        _macros = macros;

        Title = App.DisplayTitle;
        AppTitleBar.Title = App.DisplayTitle;
        AboutText.Text = App.DisplayTitle;
        DevBuildStrip.Visibility = App.IsDevBuild ? Visibility.Visible : Visibility.Collapsed;

        _sounds = new SoundService(Dispatcher) { Volume = settings.SoundVolume };

        _engine = new LoopEngine(EngineSnapshot.From(settings, macros), Dispatcher);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.GameModeToggleRequested += ToggleGameMode;
        _engine.SoundRequested += (name, start) => _sounds.Play(name, start);

        _items = new ObservableCollection<LoopItemViewModel>(settings.Loops.Select(CreateItem));
        _view = new ListCollectionView(_items) { Filter = FilterLoop };
        LoopList.ItemsSource = _view;

        _macroItems = new ObservableCollection<MacroItemViewModel>(macros.Macros.Select(CreateMacroItem));
        _macroView = new ListCollectionView(_macroItems) { Filter = FilterMacro };
        MacroList.ItemsSource = _macroView;

        _gameWindow = new GameModeWindow(_items, _macroItems);

        _remapItems = new ObservableCollection<RemapItemViewModel>(settings.Remaps.Select(CreateRemapItem));
        _remapView = new ListCollectionView(_remapItems) { Filter = FilterRemap };
        RemapList.ItemsSource = _remapView;

        // Clickable game mode panel: the row positions are refreshed while game mode is on.
        _panelTargetsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _panelTargetsTimer.Tick += (_, _) => PublishPanelTargets();

        _tray = new TrayIcon(App.DisplayTitle);
        _tray.Open += ShowFromTray;
        _tray.MenuRequested += ShowTrayMenu;

        // Save the opacity once the slider stops moving, not on every step.
        _saveOpacityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveOpacityTimer.Tick += (_, _) => { _saveOpacityTimer.Stop(); Save(); };
        OpacitySlider.Value = settings.GameModeOpacity;
        ApplyOpacity(settings.GameModeOpacity);

        _saveVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveVolumeTimer.Tick += (_, _) => { _saveVolumeTimer.Stop(); Save(); };
        VolumeSlider.Value = settings.SoundVolume;
        VolumeValueText.Text = $"{settings.SoundVolume}%";

        HotkeysGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Hotkeys");
        GameModeGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("GameModePanel");
        SoundGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Sound");
        UpdatePositionBoxes();

        ClickInPanelSwitch.IsChecked = settings.ClickItemsInPanel;
        CloseToTraySwitch.IsChecked = settings.CloseToTray;
        StartWithWindowsSwitch.IsChecked = settings.StartWithWindows;
        if (App.IsDevBuild)
        {
            // The Run entry belongs to the installed app.
            StartWithWindowsSwitch.IsEnabled = false;
            StartWithWindowsText.Text = "Not available in the development build";
        }
        InitializeUpdates();
        BackupGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Backup");
        DataFolderText.Text = $"Data saved in {AppPaths.DataFolder}";
        RemapFilterBox.SelectedIndex = 0;
        UpdateRemapState();
        FilterBox.SelectedIndex = (int)settings.Filter;
        MacroFilterBox.SelectedIndex = 0;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        UpdateHotkeyLabels();
        UpdateLoopState();
        RestorePosition();

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

    private void RememberPosition()
    {
        if (IsVisible && WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_importing)
            return; // the imported settings.json must not be overwritten
        RememberPosition();
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
        _gameWindow.Close();
        Application.Current.Shutdown();
    }

    /// <summary>Closes PuppyMacro for real (tray menu Exit, Import).</summary>
    internal void ExitApp()
    {
        _exiting = true;
        if (_gameModeActive)
            ToggleGameMode();
        Close();
    }

    // ================= System tray =================

    private void ShowFromTray()
    {
        if (_gameModeActive)
            ToggleGameMode(); // also shows the window
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

    private void RestorePosition()
    {
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

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (LoopsPage == null || SettingsPage == null)
            return;

        if (MacrosPage == null || RemapsPage == null)
            return;
        LoopsPage.Visibility = LoopsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MacrosPage.Visibility = MacrosNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RemapsPage.Visibility = RemapNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = SettingsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsNav.IsChecked != true)
            _engine?.CancelCapture();
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

    private void OnLoopEnabledChanged(LoopItemViewModel item)
    {
        if (!item.IsLoopEnabled)
            _engine.StopLoop(item.Definition.Id);
        RefreshList();
        _gameWindow.RefreshList();
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
    }

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
        UpdateMacroState();
    }

    private void OnAddLoopClick(object sender, RoutedEventArgs e)
    {
        if (_engine.AnyRunning)
            return;

        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, null) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        _settings.Loops.Add(editor.Result);
        _items.Add(CreateItem(editor.Result));
        RefreshList();
        _gameWindow.RefreshList();
        Save();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not LoopItemViewModel item)
            return;

        var edit = new MenuItem
        {
            Header = "Edit",
            Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Edit24 },
            IsEnabled = !_engine.AnyRunning,
            ToolTip = _engine.AnyRunning ? "Stop running loops to edit a loop" : null,
        };
        ToolTipService.SetShowOnDisabled(edit, true);
        edit.Click += (_, _) => EditLoop(item);
        var delete = new MenuItem { Header = "Delete", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Delete24 } };
        delete.Click += (_, _) => DeleteLoop(item);

        int index = _items.IndexOf(item);
        var moveUp = new MenuItem
        {
            Header = "Move up",
            Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.ArrowUp24 },
            IsEnabled = index > 0,
        };
        moveUp.Click += (_, _) => MoveItem(item, index - 1);
        var moveDown = new MenuItem
        {
            Header = "Move down",
            Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.ArrowDown24 },
            IsEnabled = index < _items.Count - 1,
        };
        moveDown.Click += (_, _) => MoveItem(item, index + 1);

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(edit);
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

        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, item.Definition) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        _engine.StopLoop(item.Definition.Id);
        item.Definition.CopyFrom(editor.Result);
        item.Refresh();
        RefreshList();
        _gameWindow.RefreshList();
        Save();
    }

    private async void DeleteLoop(LoopItemViewModel item)
    {
        var confirm = new UiMessageBox
        {
            Owner = this,
            Title = "Delete loop",
            Content = $"Delete \"{item.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        UiMessageBoxResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowDialogAsync();
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != UiMessageBoxResult.Primary)
            return;

        _engine.StopLoop(item.Definition.Id);
        _settings.Loops.Remove(item.Definition);
        _items.Remove(item);
        RefreshList();
        _gameWindow.RefreshList();
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

    private void OnMacroEnabledChanged(MacroItemViewModel item)
    {
        if (!item.IsLoopEnabled)
            _engine.StopMacro(item.Definition.Id);
        RefreshMacros();
        _gameWindow.RefreshList();
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
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, null, recorded) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        _macros.Macros.Add(editor.Result);
        _macroItems.Add(CreateMacroItem(editor.Result));
        SaveMacro(editor.Result);
        SaveMacroOrder();
        RefreshMacros();
        _gameWindow.RefreshList();
    }

    private void OnMacroMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not MacroItemViewModel item)
            return;

        var edit = new MenuItem
        {
            Header = "Edit",
            Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Edit24 },
            IsEnabled = !_engine.AnyRunning,
            ToolTip = _engine.AnyRunning ? "Stop running loops and macros to edit" : null,
        };
        ToolTipService.SetShowOnDisabled(edit, true);
        edit.Click += (_, _) => EditMacro(item);

        int index = _macroItems.IndexOf(item);
        var moveUp = new MenuItem { Header = "Move up", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.ArrowUp24 }, IsEnabled = index > 0 };
        moveUp.Click += (_, _) => MoveMacro(item, index - 1);
        var moveDown = new MenuItem { Header = "Move down", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.ArrowDown24 }, IsEnabled = index < _macroItems.Count - 1 };
        moveDown.Click += (_, _) => MoveMacro(item, index + 1);
        var delete = new MenuItem { Header = "Delete", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Delete24 } };
        delete.Click += (_, _) => DeleteMacro(item);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(edit);
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
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, item.Definition) { Owner = this };
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return;

        item.Definition.CopyFrom(editor.Result);
        item.Refresh();
        SaveMacro(item.Definition);
        RefreshMacros();
        _gameWindow.RefreshList();
    }

    private async void DeleteMacro(MacroItemViewModel item)
    {
        var confirm = new UiMessageBox
        {
            Owner = this,
            Title = "Delete macro",
            Content = $"Delete \"{item.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        UiMessageBoxResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowDialogAsync();
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != UiMessageBoxResult.Primary)
            return;

        _engine.StopMacro(item.Definition.Id);
        _macros.Delete(item.Definition);
        _macroItems.Remove(item);
        SaveMacroOrder();
        RefreshMacros();
        _gameWindow.RefreshList();
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
        _gameWindow.RefreshList();
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
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_macros.TrySave(macro, out string? error))
            ShowSettingsError($"Could not save the macro \"{macro.Name}\": {error}");
    }

    private void SaveMacroOrder()
    {
        _settings.MacroOrder = _macros.Macros.Select(m => m.Id).ToList();
        Save();
    }

    // ================= Clickable game mode panel =================

    private void OnClickInPanelChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.ClickItemsInPanel = ClickInPanelSwitch.IsChecked == true;
        UpdatePanelClicks();
        UpdateSettingsSummaries();
        Save();
    }

    /// <summary>Clicks on the panel are handled by the input hook, so the game never loses focus.</summary>
    private void UpdatePanelClicks()
    {
        if (_gameModeActive && _settings.ClickItemsInPanel)
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
        if (_gameModeActive && _settings.ClickItemsInPanel)
            _engine.SetPanelTargets(_gameWindow.GetClickTargets());
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
        _gameWindow.RefreshList();
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

    /// <summary>Shows a dialog with every hotkey ignored until it closes.</summary>
    private bool? ShowDialogWithoutHotkeys(Window dialog)
    {
        _engine.HotkeysSuspended = true;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            _engine.HotkeysSuspended = false;
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

    private void OnGameModeClick(object sender, RoutedEventArgs e) => ToggleGameMode();

    // ================= Settings =================

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeComboBox.SelectedIndex < 0)
            return;
        _settings.Theme = (AppTheme)ThemeComboBox.SelectedIndex;
        App.ApplyTheme(_settings.Theme, this);
        Save();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null || _gameWindow == null)
            return; // still loading

        int percent = (int)Math.Round(e.NewValue);
        ApplyOpacity(percent);
        if (_initializing || percent == _settings.GameModeOpacity)
            return;

        _settings.GameModeOpacity = percent;
        UpdateSettingsSummaries();
        _saveOpacityTimer.Stop();
        _saveOpacityTimer.Start();
    }

    private void ApplyOpacity(int percent)
    {
        OpacityValueText.Text = $"{percent}%";
        byte alpha = (byte)Math.Round(percent * 255 / 100.0);
        OpacityPreviewPanel.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
        _gameWindow.SetBackgroundOpacity(percent);
    }

    private void OnGroupToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not FrameworkElement group || group.Tag is not string key)
            return;
        if (e.OriginalSource != sender)
            return; // ignore expanders nested inside
        var open = _settings.ExpandedSettingsGroups;
        open.Remove(key);
        if (group is Expander { IsExpanded: true })
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

    // ---- Game mode position ----

    /// <summary>Default: top-left of the main screen's work area, with a small margin.</summary>
    private static Point DefaultGamePosition() =>
        new(SystemParameters.WorkArea.Left + 20, SystemParameters.WorkArea.Top + 20);

    /// <summary>Saved position, or the default if none or if it is no longer on any screen.</summary>
    private Point GamePosition()
    {
        if (_settings.GameModeX is not double x || _settings.GameModeY is not double y)
            return DefaultGamePosition();

        bool onScreen =
            x >= SystemParameters.VirtualScreenLeft - 40 &&
            y >= SystemParameters.VirtualScreenTop - 20 &&
            x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
            y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        return onScreen ? new Point(x, y) : DefaultGamePosition();
    }

    private bool _updatingPositionBoxes;

    private void UpdatePositionBoxes()
    {
        Point p = GamePosition();
        _updatingPositionBoxes = true;
        PositionXBox.Value = Math.Round(p.X);
        PositionYBox.Value = Math.Round(p.Y);
        _updatingPositionBoxes = false;
        UpdateSettingsSummaries();
    }

    private void OnPositionBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingPositionBoxes)
            return;
        if (PositionXBox.Value is double x && PositionYBox.Value is double y)
        {
            _settings.GameModeX = x;
            _settings.GameModeY = y;
            UpdateSettingsSummaries();
            Save();
        }
    }

    private void OnPositionOnScreenClick(object sender, RoutedEventArgs e)
    {
        var placement = new PlacementWindow(_items, _macroItems, GamePosition(), _settings.GameModeOpacity,
            KeyNames.Format(_settings.GameModeHotkey), KeyNames.Format(_settings.StopAllHotkey));
        if (ShowDialogWithoutHotkeys(placement) != true)
            return;

        _settings.GameModeX = Math.Round(placement.Result.X);
        _settings.GameModeY = Math.Round(placement.Result.Y);
        UpdatePositionBoxes();
        Save();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _settings.GameModeX = null;
        _settings.GameModeY = null;
        UpdatePositionBoxes();
        Save();
    }

    private void UpdateSettingsSummaries()
    {
        if (HotkeysSummary == null)
            return;
        HotkeysSummary.Text = $"Stop all {KeyNames.Format(_settings.StopAllHotkey)}, Game mode {KeyNames.Format(_settings.GameModeHotkey)}, Record {KeyNames.Format(_settings.RecordHotkey)}";
        Point p = GamePosition();
        GameModeSummary.Text = $"Opacity {_settings.GameModeOpacity}%, position X {Math.Round(p.X)}, Y {Math.Round(p.Y)}, click to start {(_settings.ClickItemsInPanel ? "on" : "off")}";
        SoundSummary.Text = $"Volume {_settings.SoundVolume}%";
    }

    private void OnChangeStopAllHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureGlobalHotkey(StopAllChangeButton, "StopAll");

    private void OnChangeGameModeHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureGlobalHotkey(GameModeChangeButton, "GameMode");

    private void OnChangeRecordHotkeyClick(object sender, RoutedEventArgs e) =>
        CaptureGlobalHotkey(RecordChangeButton, "Record");

    private void CaptureGlobalHotkey(Wpf.Ui.Controls.Button button, string which)
    {
        HideSettingsError();
        StopAllChangeButton.Content = "Change";
        GameModeChangeButton.Content = "Change";
        RecordChangeButton.Content = "Change";
        button.Content = "Press a key…";

        _engine.BeginCapture(
            binding =>
            {
                button.Content = "Change";
                string? error = KeyNames.IsPrimaryMouse(binding.Vk)
                    ? "Left and right click cannot be used as a hotkey."
                    : HotkeyConflicts.Find(binding, _settings, _macros, ignoreGlobal: which);
                if (error != null)
                {
                    ShowSettingsError(error);
                    return;
                }

                switch (which)
                {
                    case "StopAll": _settings.StopAllHotkey = binding; break;
                    case "GameMode": _settings.GameModeHotkey = binding; break;
                    default: _settings.RecordHotkey = binding; break;
                }
                UpdateHotkeyLabels();
                Save();
            },
            () => button.Content = "Change",
            allowPrimaryMouse: false);
    }

    private void UpdateHotkeyLabels()
    {
        string stopAll = KeyNames.Format(_settings.StopAllHotkey);
        string gameMode = KeyNames.Format(_settings.GameModeHotkey);
        StopAllHotkeyCaps.ItemsSource = KeyNames.Parts(_settings.StopAllHotkey);
        GameModeHotkeyCaps.ItemsSource = KeyNames.Parts(_settings.GameModeHotkey);
        RecordHotkeyCaps.ItemsSource = KeyNames.Parts(_settings.RecordHotkey);
        StopAllButtonHotkeyText.Text = stopAll;
        GameModeButtonHotkeyText.Text = gameMode;
        _gameWindow.SetHotkeyLabels(gameMode, stopAll);
        UpdateSettingsSummaries();
    }

    private void ShowSettingsError(string message)
    {
        SettingsErrorText.Text = message;
        SettingsErrorText.Visibility = Visibility.Visible;
    }

    private void HideSettingsError() => SettingsErrorText.Visibility = Visibility.Collapsed;

    // ================= Game mode =================

    private void ToggleGameMode()
    {
        if (!_gameModeActive)
        {
            _engine.CancelCapture();
            Point position = GamePosition();
            _gameWindow.Left = position.X;
            _gameWindow.Top = position.Y;
            Hide();
            _gameWindow.Show();
            _gameModeActive = true;
            UpdatePanelClicks();
        }
        else
        {
            _gameWindow.Hide();
            _gameModeActive = false;
            UpdatePanelClicks();
            // Bring the window to the front: it is not topmost, so it would
            // otherwise reappear behind the game.
            Show();
            Activate();
            _gameModeActive = false;
        }
    }

    // ================= Persistence =================

    private void Save()
    {
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_store.TrySave(_settings, out string? error))
            ShowSettingsError($"Could not save settings.json: {error}");
    }

    // ================= Remap =================

    private RemapItemViewModel CreateRemapItem(RemapDefinition definition)
    {
        var item = new RemapItemViewModel(definition);
        item.EnabledChanged += _ =>
        {
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

    private void OnRemapMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not RemapItemViewModel item)
            return;

        var edit = new MenuItem { Header = "Edit", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Edit24 } };
        edit.Click += (_, _) => EditRemap(item);
        var duplicate = new MenuItem { Header = "Duplicate", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Copy24 } };
        duplicate.Click += (_, _) => DuplicateRemap(item);
        var delete = new MenuItem { Header = "Delete", Icon = new UiSymbolIcon { Symbol = UiSymbolRegular.Delete24 } };
        delete.Click += (_, _) => DeleteRemap(item);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(edit);
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
        var confirm = new UiMessageBox
        {
            Owner = this,
            Title = "Delete remap",
            Content = $"Delete the remap {item.SourceText} → {item.TargetText}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        UiMessageBoxResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await confirm.ShowDialogAsync();
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result != UiMessageBoxResult.Primary)
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
        _settings.CloseToTray = CloseToTraySwitch.IsChecked == true;
        Save();
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.StartWithWindows = StartWithWindowsSwitch.IsChecked == true;
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

        var confirm = new UiMessageBox
        {
            Owner = this,
            Title = "Import",
            Content = "Replace all your loops, macros, remaps and settings with the ones in this file? " +
                      "Your current data is saved first as a backup in the data folder. PuppyMacro restarts after the import.",
            PrimaryButtonText = "Import and restart",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != UiMessageBoxResult.Primary)
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
