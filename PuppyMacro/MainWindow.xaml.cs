using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using PuppyMacro.Resources;
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
using ToggleSwitch = iNKORE.UI.WPF.Modern.Controls.ToggleSwitch;

namespace PuppyMacro;

public partial class MainWindow
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly LoopEngine _engine;
    private readonly ObservableCollection<LoopItemViewModel> _items;
    private readonly PageViews _view;
    private readonly MacroLibrary _macros;
    private readonly ObservableCollection<MacroItemViewModel> _macroItems;
    private readonly PageViews _macroView;
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
    private readonly PageViews _remapView;
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
        DevBuildRun.Text = App.ShowsDevMarks ? "DEV" : "";
        AboutText.Text = App.DisplayTitle;
        DevBuildStrip.Visibility = App.ShowsDevMarks ? Visibility.Visible : Visibility.Collapsed;

        _sounds = new SoundService(Dispatcher) { Volume = settings.SoundVolume };

        _engine = new LoopEngine(EngineSnapshot.From(settings, macros), Dispatcher);
        KeyCaptureField.SetEngine(this, _engine); // the Settings hotkey fields wait for keys through it
        SetUpGlobalHotkeyFields();
        _engine.StateChanged += OnEngineStateChanged;
        _engine.OverlayModeToggleRequested += ToggleOverlayMode;
        _engine.OverlayExitAndOpenRequested += ShowFromTray;
        _engine.OverlayPanelDragged += OnOverlayPanelDragged;
        _engine.OverlayEnableToggleRequested += OnOverlayEnableToggleRequested;
        _engine.SoundRequested += (name, start) => _sounds.Play(name, start);
        _engine.ForegroundAppChanged += OnForegroundAppChanged;

        _items = new ObservableCollection<LoopItemViewModel>(settings.Loops.Select(CreateItem));
        _view = new PageViews(_items, FilterLoop, item => ((LoopItemViewModel)item).AppExe);

        _macroItems = new ObservableCollection<MacroItemViewModel>(macros.Macros.Select(CreateMacroItem));
        _macroView = new PageViews(_macroItems, FilterMacro, item => ((MacroItemViewModel)item).AppExe);

        _overlayPanelWindow = new OverlayPanelWindow(_items, _macroItems);

        _remapItems = new ObservableCollection<RemapItemViewModel>(settings.Remaps.Select(CreateRemapItem));
        _remapView = new PageViews(_remapItems, FilterRemap, item => ((RemapItemViewModel)item).Definition.AppExe);
        InitializeLists();

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

        HotkeysGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Hotkeys");
        OverlayGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Overlay");

        ClickInPanelSwitch.IsOn = settings.ClickItemsInPanel;
        ShowPanelSwitch.IsOn = settings.ShowOverlayPanel;
        SetPanelOptionsEnabled(settings.ShowOverlayPanel);
        CloseToTraySwitch.IsOn = settings.CloseToTray;
        StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        if (App.IsDevBuild)
        {
            // The Run entry belongs to the installed app.
            StartWithWindowsSwitch.IsEnabled = false;
            StartWithWindowsText.Text = Strings.Settings_NotInDevBuild;
        }
        else
        {
            // The Run entry follows the setting: after an import, a reinstall or on another PC the setting may be on
            // with no entry (uninstalling removes it).
            StartupService.Apply(settings.StartWithWindows);
        }
        InitializeUpdates();
        Rail.PageChanged += OnPageChanged;
        RefreshUpdateUi();
        BackupGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("Backup");
        AboutGroup.IsExpanded = settings.ExpandedSettingsGroups.Contains("About");
        DataFolderText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Settings_DataSavedIn, AppPaths.DataFolder);
        RemapFilterBox.SelectedIndex = 0;
        UpdateRemapState();
        FilterBox.SelectedIndex = (int)settings.Filter;
        MacroFilterBox.SelectedIndex = 0;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        FillLanguages();
        UpdateHotkeyLabels();
        UpdateLoopState();
        RestorePlacement();
        InitializeHistory();
        InitializeAi();

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
            MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Strings.Main_HotkeysNotWorking, ex.Message), "PuppyMacro",
                MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, App.MessageBoxOptions);
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
                _tray.Notify("PuppyMacro", Strings.Tray_StillRunning);
            }
        }
        Save();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopAi();
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

    /// <summary>Shows the window from the tray or overlay mode (tray icon, PuppyMacro started again, the overlay panel's open button).</summary>
    internal void ShowFromTray()
    {
        if (_overlayModeActive)
            ToggleOverlayMode(); // also shows the window
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Shows Settings with Backup open (Open Settings in <see cref="BackupFileWindow"/>).</summary>
    internal void ShowBackupSettings()
    {
        ShowFromTray();
        Rail.Select("Settings");
        BackupGroup.IsExpanded = true;
        // Once the Settings page is laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BackupGroup.BringIntoView());
    }

    private void ShowTrayMenu()
    {
        var open = new MenuItem { Header = Strings.Tray_Open, FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowFromTray();
        var stopAll = new MenuItem { Header = Strings.Main_StopAll, IsEnabled = _engine.AnyRunning };
        stopAll.Click += (_, _) => _engine.StopAll();
        var exit = new MenuItem { Header = Strings.Tray_Exit };
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
        AiPage.Visibility = page == "AI" ? Visibility.Visible : Visibility.Collapsed;
        OnAiPageShown(page == "AI");
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page != "Settings")
            _engine.CancelCapture(); // a Settings hotkey field that waits stops
    }

    /// <summary>The overlay panel's options are off while the panel is off.</summary>
    private void SetPanelOptionsEnabled(bool enabled)
    {
        OpacityCard.IsEnabled = enabled;
        ClickInPanelCard.IsEnabled = enabled;
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
        RefitLists();
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
        CountText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Loops_CountLine,
            Plurals.Format("Loops_Count", total), Plurals.Format("Main_RunningCount", running));

        if (total == 0)
        {
            EmptyLoopsText.Text = Strings.Loops_Empty;
            EmptyLoopsText.Visibility = Visibility.Visible;
        }
        else if (_view.Count == 0)
        {
            EmptyLoopsText.Text = Strings.Loops_NoMatch;
            EmptyLoopsText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyLoopsText.Visibility = Visibility.Collapsed;
        }

        bool anyRunning = _engine.AnyRunning;
        AddLoopButton.IsEnabled = !anyRunning;
        AddLoopButton.ToolTip = anyRunning ? Strings.Main_StopRunningFirst : null;
        NewMacroButton.IsEnabled = !anyRunning;
        RecordMacroButton.IsEnabled = !anyRunning;
        NewMacroButton.ToolTip = RecordMacroButton.ToolTip = anyRunning ? Strings.Main_StopRunningFirst : null;
        StopAllButton.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;
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
        AddLoop(editor.Result);
    }

    /// <summary>A loop saved by the editor as new.</summary>
    private void AddLoop(LoopDefinition loop)
    {
        _settings.Loops.Add(loop);
        _items.Add(CreateItem(loop));
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not LoopItemViewModel item)
            return;

        var delete = new MenuItem { Header = Strings.Card_Delete, Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteLoop(item);

        int index = _items.IndexOf(item);
        var moveUp = new MenuItem
        {
            Header = Strings.Card_MoveUp,
            Icon = new FontIcon { Icon = SegoeFluentIcons.Up },
            IsEnabled = index > 0,
        };
        moveUp.Click += (_, _) => MoveItem(item, index - 1);
        var moveDown = new MenuItem
        {
            Header = Strings.Card_MoveDown,
            Icon = new FontIcon { Icon = SegoeFluentIcons.Down },
            IsEnabled = index < _items.Count - 1,
        };
        moveDown.Click += (_, _) => MoveItem(item, index + 1);

        var duplicate = new MenuItem { Header = Strings.Card_Duplicate, Icon = new FontIcon { Icon = SegoeFluentIcons.Copy } };
        duplicate.Click += (_, _) => DuplicateLoop(item);
        var export = new MenuItem { Header = Strings.Share_CardExport, Icon = new FontIcon { Icon = SegoeFluentIcons.Upload } };
        export.Click += (_, _) => ExportOne(item.Definition);
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(EditMenuItem(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicate);
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    /// <param name="forAi">Opened for an AI app (open_item): prepares the editor instead of centering it on this window.</param>
    /// <returns>Whether the user saved.</returns>
    private bool EditLoop(LoopItemViewModel item, Action<Window>? forAi = null)
    {
        if (_engine.AnyRunning)
            return false;

        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, item.Definition, PlaceFromEditor);
        if (forAi == null)
            editor.Owner = this;
        else
            forAi(editor);
        if (_itemHistory != null)
            editor.EnableHistory(_itemHistory);
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return false;
        ApplyLoopEdit(item, editor.Result);
        return true;
    }

    /// <summary>A change of a loop saved by the editor.</summary>
    private void ApplyLoopEdit(LoopItemViewModel item, LoopDefinition result)
    {
        _engine.StopLoop(item.Definition.Id);
        item.Definition.CopyFrom(result);
        item.Refresh();
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    private async void DeleteLoop(LoopItemViewModel item)
    {
        var confirm = new ContentDialog
        {
            Title = Strings.Loops_DeleteTitle,
            Content = string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteNamedConfirm, item.Name),
            PrimaryButtonText = Strings.Card_Delete,
            CloseButtonText = Strings.Main_Cancel,
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
        RemoveLoop(item);
    }

    /// <summary>Deletes a loop, after the user confirmed.</summary>
    private void RemoveLoop(LoopItemViewModel item)
    {
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
        RefitLists();
    }

    private void UpdateMacroState()
    {
        if (MacroCountText == null || _macroItems == null)
            return;
        int total = _macroItems.Count;
        int running = _macroItems.Count(m => m.IsRunning);
        MacroCountText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Macros_CountLine,
            Plurals.Format("Macros_Count", total), Plurals.Format("Main_RunningCount", running));

        if (total == 0)
        {
            EmptyMacrosText.Text = Strings.Macros_Empty;
            EmptyMacrosText.Visibility = Visibility.Visible;
        }
        else if (_macroView.Count == 0)
        {
            EmptyMacrosText.Text = Strings.Macros_NoMatch;
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
        AddMacro(editor.Result);
    }

    /// <summary>A macro saved by the editor as new.</summary>
    private void AddMacro(MacroDefinition macro)
    {
        _macros.Macros.Add(macro);
        _macroItems.Add(CreateMacroItem(macro));
        SaveMacro(macro);
        SaveMacroOrder();
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    private void OnMacroMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not MacroItemViewModel item)
            return;

        int index = _macroItems.IndexOf(item);
        var moveUp = new MenuItem { Header = Strings.Card_MoveUp, Icon = new FontIcon { Icon = SegoeFluentIcons.Up }, IsEnabled = index > 0 };
        moveUp.Click += (_, _) => MoveMacro(item, index - 1);
        var moveDown = new MenuItem { Header = Strings.Card_MoveDown, Icon = new FontIcon { Icon = SegoeFluentIcons.Down }, IsEnabled = index < _macroItems.Count - 1 };
        moveDown.Click += (_, _) => MoveMacro(item, index + 1);
        var delete = new MenuItem { Header = Strings.Card_Delete, Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteMacro(item);
        var export = new MenuItem { Header = Strings.Share_CardExport, Icon = new FontIcon { Icon = SegoeFluentIcons.Upload } };
        export.Click += (_, _) => ExportOne(item.Definition);

        var duplicate = new MenuItem { Header = Strings.Card_Duplicate, Icon = new FontIcon { Icon = SegoeFluentIcons.Copy } };
        duplicate.Click += (_, _) => DuplicateMacro(item);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(EditMenuItem(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicate);
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    /// <param name="forAi">Opened for an AI app (open_item): prepares the editor instead of centering it on this window.</param>
    /// <returns>Whether the user saved.</returns>
    private bool EditMacro(MacroItemViewModel item, Action<Window>? forAi = null)
    {
        if (_engine.AnyRunning)
            return false;
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, item.Definition, PlaceFromEditor);
        if (forAi == null)
            editor.Owner = this;
        else
            forAi(editor);
        if (_itemHistory != null)
            editor.EnableHistory(_itemHistory);
        if (ShowMacroEditor(editor) != true || editor.Result == null)
            return false;
        ApplyMacroEdit(item, editor.Result);
        return true;
    }

    /// <summary>A change of a macro saved by the editor.</summary>
    private void ApplyMacroEdit(MacroItemViewModel item, MacroDefinition result)
    {
        item.Definition.CopyFrom(result);
        item.Refresh();
        SaveMacro(item.Definition);
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    private async void DeleteMacro(MacroItemViewModel item)
    {
        var confirm = new ContentDialog
        {
            Title = Strings.Macros_DeleteTitle,
            Content = string.Format(CultureInfo.CurrentCulture, Strings.Main_DeleteNamedConfirm, item.Name),
            PrimaryButtonText = Strings.Card_Delete,
            CloseButtonText = Strings.Main_Cancel,
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
        RemoveMacro(item);
    }

    /// <summary>Deletes a macro, after the user confirmed.</summary>
    private void RemoveMacro(MacroItemViewModel item)
    {
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
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_macros.TrySave(macro, out string? error))
            ShowSettingsError(string.Format(CultureInfo.CurrentCulture, Strings.Macros_SaveFailed, macro.Name, error));
        _itemHistory?.Record(macro.Id, MacroJson.Serialize(macro), _savedBy);
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

    /// <summary>The press is on a control inside <paramref name="card"/> (its buttons, switch…), not on the card itself.</summary>
    private static bool IsInsideInteractiveControl(DependencyObject? source, DependencyObject? card = null)
    {
        while (source != null && !ReferenceEquals(source, card))
        {
            if (source is ButtonBase or TextBox or ComboBox or Slider or ToggleSwitch)
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
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject, sender as DependencyObject))
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
        // The card is a button (a click opens the editor): without its mouse capture it is no longer pressed, so the drop
        // does not open the editor.
        (sender as UIElement)?.ReleaseMouseCapture();
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
            || !CanDropOn(dragged, target))
        {
            e.Effects = DragDropEffects.None;
            ClearDropIndicators();
            return;
        }

        e.Effects = DragDropEffects.Move;
        bool before = DropsBefore(card, e);
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
            || !CanDropOn(dragged, target))
        {
            ClearDropIndicators();
            return;
        }

        bool before = DropsBefore(card, e);
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

    /// <summary>The Language list: Use system language, then every language by its own name, alphabetical. Each item's
    /// Tag is its code (null for the system language).</summary>
    private void FillLanguages()
    {
        LanguageComboBox.Items.Add(new ComboBoxItem { Content = Strings.Settings_LanguageSystem, Tag = null });
        foreach (AppLanguages.Language language in AppLanguages.ForMenu())
            LanguageComboBox.Items.Add(new ComboBoxItem { Content = language.NativeName, Tag = language.Code });
        LanguageComboBox.SelectedItem = LanguageComboBox.Items.Cast<ComboBoxItem>()
            .First(item => (string?)item.Tag == _settings.Language);
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageComboBox.SelectedItem is not ComboBoxItem item)
            return;
        _settings.Language = (string?)item.Tag;
        Save();

        // The texts are read once at start: a restart shows the new language. Nothing to restart when the choice gives
        // the language in use (Italiano on an Italian Windows).
        string language = App.LanguageFor(_settings.Language);
        if (language == App.LanguageInUse)
            return;
        var restart = new ContentDialog
        {
            Title = Strings.LanguageRestart_Title,
            Content = string.Format(CultureInfo.CurrentCulture, Strings.LanguageRestart_Content,
                AppLanguages.All.First(l => l.Code == language).NativeName),
            PrimaryButtonText = Strings.LanguageRestart_Restart,
            CloseButtonText = Strings.LanguageRestart_Later,
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result;
        _engine.HotkeysSuspended = true;
        try
        {
            result = await restart.ShowAsync(this);
        }
        finally
        {
            _engine.HotkeysSuspended = false;
        }
        if (result == ContentDialogResult.Primary)
            App.Restart();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_overlayPanelWindow == null)
            return; // still loading

        int percent = (int)Math.Round(e.NewValue);
        ApplyOpacity(percent);
        if (_initializing || percent == _settings.OverlayPanelOpacity)
            return;

        _settings.OverlayPanelOpacity = percent;
        _saveOpacityTimer.Stop();
        _saveOpacityTimer.Start();
    }

    private void ApplyOpacity(int percent)
    {
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
        if (_sounds == null)
            return; // still loading

        int percent = (int)Math.Round(e.NewValue);
        _sounds.Volume = percent;
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


    private void OnPositionOnScreenClick(object sender, RoutedEventArgs e) => OpenPlacement(null);

    /// <summary>
    /// The editors' Position button: places the whole overlay. The edited item is shown as in the
    /// editor (its button is left out when off); its new position is returned to the editor, which
    /// saves it with the item. Null when cancelled.
    /// </summary>
    private Point? PlaceFromEditor(EditedFloatingButton edited) => OpenPlacement(edited);

    /// <summary>
    /// Opens the placement overlay with the whole overlay as overlay mode shows it: the overlay
    /// panel (when shown) and every floating button, plus the one being edited.
    /// </summary>
    /// <param name="show">Shows the placement screen (default: as the editors do); true when the user selected Done.</param>
    private Point? OpenPlacement(EditedFloatingButton? edited, Func<Window, bool>? show = null)
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
                item.HasHotkey ? item.HotkeyText : "", item.FloatingButton.Size, item.FloatingButton.EffectiveOpacity, positions[index],
                item.IsItemEnabled));
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
            ShowSettingsError(Strings.Settings_NothingToPlace);
            return null;
        }

        var placement = new PlacementWindow(_items, _macroItems, _settings.ShowOverlayPanel, PanelPosition(), _settings.OverlayPanelOpacity,
            KeyNames.Format(_settings.OverlayModeHotkey), KeyNames.Format(_settings.StopAllHotkey), OpenMainHotkeyLabel(), buttons, edited?.Id);
        if (!(show?.Invoke(placement) ?? ShowDialogWithoutHotkeys(placement) == true))
            return null;

        if (_settings.ShowOverlayPanel)
        {
            _settings.OverlayPanelX = Math.Round(placement.Result.X);
            _settings.OverlayPanelY = Math.Round(placement.Result.Y);
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

    /// <summary>
    /// A right click on a loop or macro in the overlay: enables or disables it, as its card's switch does (a running
    /// one stops). One hidden while disabled leaves the overlay; enabled again it comes back.
    /// </summary>
    private void OnOverlayEnableToggleRequested(Guid id)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is LoopItemViewModel loop)
            loop.IsLoopEnabled = !loop.IsLoopEnabled;
        else if (_macroItems.FirstOrDefault(m => m.Id == id) is MacroItemViewModel macro)
            macro.IsLoopEnabled = !macro.IsLoopEnabled;
        else
            return;
        if (!_overlayModeActive)
            return;
        // The floating buttons change only when one is hidden while disabled.
        var shown = _buttonWindows.Select(w => w.ItemId).ToList();
        if (!FloatingButtonItems().Select(i => i.Id).SequenceEqual(shown))
        {
            CloseFloatingButtons();
            ShowFloatingButtons();
        }
        UpdatePanelClicks(); // a left click starts it right away once enabled
    }

    private void CloseFloatingButtons()
    {
        foreach (var window in _buttonWindows)
            window.Close();
        _buttonWindows.Clear();
    }

    /// <summary>The four global hotkey fields: each checks a pressed key against the rules and the other hotkeys.</summary>
    private void SetUpGlobalHotkeyFields()
    {
        foreach (var (field, which) in new[] { (OpenMainHotkeyField, "OpenMain"), (OverlayModeHotkeyField, "OverlayMode"),
                     (StopAllHotkeyField, "StopAll"), (RecordHotkeyField, "Record") })
            field.Validate = binding => HotkeyRules.Problem(binding, hold: false)
                ?? HotkeyConflicts.Find(binding, _settings, _macros, ignoreGlobal: which);
    }

    private void OnGlobalHotkeyStarted(object? sender, EventArgs e) => HideSettingsError();

    private void OnGlobalHotkeyChanged(object? sender, EventArgs e)
    {
        // Open PuppyMacro can be cleared (none); the others always have a hotkey.
        if (sender == OpenMainHotkeyField)
        {
            _settings.OpenMainHotkey = OpenMainHotkeyField.Value is { IsSet: true } open ? open.Clone() : null;
            UpdateHotkeyLabels();
            Save();
            return;
        }
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
        OpenMainHotkeyField.Value = _settings.OpenMainHotkey?.Clone();
        // The hotkey in the tooltip, when there is one
        StopAllButton.ToolTip = _settings.StopAllHotkey == null ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_StopAllTooltip, stopAll);
        OverlayModeButton.ToolTip = _settings.OverlayModeHotkey == null ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_EnterOverlayModeTooltip, overlayMode);
        _overlayPanelWindow.SetHotkeyLabels(overlayMode, stopAll, OpenMainHotkeyLabel());
    }

    /// <summary>The Open PuppyMacro hotkey as the overlay panel shows it; null when it has none.</summary>
    private string? OpenMainHotkeyLabel() =>
        _settings.OpenMainHotkey is { IsSet: true } open ? KeyNames.Format(open) : null;

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

    /// <summary>
    /// Overlay mode on or off (the Overlay mode hotkey, the panel's exit button). Leaving it keeps the main
    /// window in the tray: the panel's open button and the tray icon open it (<see cref="ShowFromTray"/>).
    /// </summary>
    private void ToggleOverlayMode()
    {
        if (!_overlayModeActive)
        {
            _engine.CancelCapture();
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
        }
    }

    // ================= Persistence =================

    private void Save()
    {
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        if (!_store.TrySave(_settings, out string? error))
            ShowSettingsError(string.Format(CultureInfo.CurrentCulture, Strings.Settings_SaveFailed, error));
        RecordHistory();
    }

    // ================= History =================

    /// <summary>The History of every item (the editors' History view), recorded on every save.</summary>
    private ItemHistory? _itemHistory;

    /// <summary>Who the saves are by: "You", or the AI app while it changes something (<see cref="SavingAs"/>).</summary>
    private string _savedBy = ItemHistory.You;

    private void InitializeHistory()
    {
        _itemHistory = new ItemHistory(AppPaths.HistoryFolder);
        foreach (LoopDefinition loop in _settings.Loops)
            _itemHistory.Seed(loop.Id, LoopJson.Serialize(loop));
        foreach (MacroDefinition macro in _macros.Macros)
            _itemHistory.Seed(macro.Id, MacroJson.Serialize(macro));
        foreach (RemapDefinition remap in _settings.Remaps)
            _itemHistory.Seed(remap.Id, RemapJson.Serialize(remap));
    }

    /// <summary>
    /// Every save goes through here (the editors, a card's switch, the overlay's right click, an AI app): the loops and
    /// remaps that changed get a new version, and the items gone are recorded as deleted. Macros are recorded by
    /// <see cref="SaveMacro"/>, one file each.
    /// </summary>
    private void RecordHistory()
    {
        if (_itemHistory == null)
            return;
        foreach (LoopDefinition loop in _settings.Loops)
            _itemHistory.Record(loop.Id, LoopJson.Serialize(loop), _savedBy);
        foreach (RemapDefinition remap in _settings.Remaps)
            _itemHistory.Record(remap.Id, RemapJson.Serialize(remap), _savedBy);
        var existing = _settings.Loops.Select(l => l.Id).Concat(_settings.Remaps.Select(r => r.Id))
            .Concat(_macros.Macros.Select(m => m.Id)).ToHashSet();
        foreach (Guid gone in _itemHistory.Known.Where(id => !existing.Contains(id)).ToList())
            _itemHistory.RecordDeleted(gone, _savedBy);
    }

    /// <summary>Runs <paramref name="change"/> with its saves recorded as <paramref name="by"/> (an AI app) in the History.</summary>
    private T SavingAs<T>(string by, Func<T> change)
    {
        string previous = _savedBy;
        _savedBy = by;
        try
        {
            return change();
        }
        finally
        {
            _savedBy = previous;
        }
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
        RefitLists();
    }

    private void UpdateRemapState()
    {
        if (RemapCountText == null || _remapItems == null)
            return;
        int total = _remapItems.Count;
        int enabled = _remapItems.Count(r => r.IsLoopEnabled);
        RemapCountText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Remaps_CountLine,
            Plurals.Format("Remaps_Count", total), Plurals.Format("Remaps_EnabledCount", enabled));
        if (total == 0)
        {
            EmptyRemapsText.Text = Strings.Remaps_Empty;
            EmptyRemapsText.Visibility = Visibility.Visible;
        }
        else if (_remapView.Count == 0)
        {
            EmptyRemapsText.Text = Strings.Remaps_NoMatch;
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
        AddRemap(editor.Result);
    }

    /// <summary>A remap saved by the editor as new.</summary>
    private void AddRemap(RemapDefinition remap)
    {
        _settings.Remaps.Add(remap);
        _remapItems.Add(CreateRemapItem(remap));
        RefreshRemaps();
        Save();
    }

    private void OnRemapMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not RemapItemViewModel item)
            return;

        var duplicate = new MenuItem { Header = Strings.Card_Duplicate, Icon = new FontIcon { Icon = SegoeFluentIcons.Copy } };
        duplicate.Click += (_, _) => DuplicateRemap(item);
        var delete = new MenuItem { Header = Strings.Card_Delete, Icon = new FontIcon { Icon = SegoeFluentIcons.Delete } };
        delete.Click += (_, _) => DeleteRemap(item);
        var export = new MenuItem { Header = Strings.Share_CardExport, Icon = new FontIcon { Icon = SegoeFluentIcons.Upload } };
        export.Click += (_, _) => ExportOne(item.Definition);

        int index = _remapItems.IndexOf(item);
        var moveUp = new MenuItem { Header = Strings.Card_MoveUp, Icon = new FontIcon { Icon = SegoeFluentIcons.Up }, IsEnabled = index > 0 };
        moveUp.Click += (_, _) => MoveRemap(item, index - 1);
        var moveDown = new MenuItem { Header = Strings.Card_MoveDown, Icon = new FontIcon { Icon = SegoeFluentIcons.Down }, IsEnabled = index < _remapItems.Count - 1 };
        moveDown.Click += (_, _) => MoveRemap(item, index + 1);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(EditMenuItem(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicate);
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    /// <param name="forAi">Opened for an AI app (open_item): prepares the editor instead of centering it on this window.</param>
    /// <returns>Whether the user saved.</returns>
    private bool EditRemap(RemapItemViewModel item, Action<Window>? forAi = null)
    {
        var editor = new RemapEditorWindow(_engine, _settings, _macros, item.Definition);
        if (forAi == null)
            editor.Owner = this;
        else
            forAi(editor);
        if (_itemHistory != null)
            editor.EnableHistory(_itemHistory);
        if (ShowDialogWithoutHotkeys(editor) != true || editor.Result == null)
            return false;
        ApplyRemapEdit(item, editor.Result);
        return true;
    }

    /// <summary>A change of a remap saved by the editor.</summary>
    private void ApplyRemapEdit(RemapItemViewModel item, RemapDefinition result)
    {
        item.Definition.CopyFrom(result);
        item.Refresh();
        RefreshRemaps();
        Save();
    }

    /// <summary>
    /// More options › Edit, its first command: the item's editor, as a click on it (loops and macros not while one runs).
    /// </summary>
    private MenuItem EditMenuItem(object item)
    {
        var edit = new MenuItem
        {
            Header = Strings.Card_Edit,
            Icon = new FontIcon { Icon = SegoeFluentIcons.Edit },
            IsEnabled = item is RemapItemViewModel || !_engine.AnyRunning,
        };
        edit.Click += (_, _) => OpenItem(item);
        return edit;
    }

    /// <summary>
    /// More options › Duplicate of a loop: a copy right under it, "Name (2)", disabled and without a hotkey (two loops cannot
    /// have the same one; a Hold copy becomes Toggle, as Hold needs a hotkey), ready to be edited.
    /// </summary>
    private void DuplicateLoop(LoopItemViewModel item)
    {
        if (_engine.AnyRunning)
            return;
        var copy = item.Definition.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = ItemNames.Copy(copy.Name, _settings.Loops.Select(l => l.Name));
        copy.Enabled = false;
        copy.Hotkey = null;
        if (copy.Mode == ActivationMode.Hold)
            copy.Mode = ActivationMode.Toggle;
        _settings.Loops.Insert(_settings.Loops.IndexOf(item.Definition) + 1, copy);
        _items.Insert(_items.IndexOf(item) + 1, CreateItem(copy));
        RefreshList();
        _overlayPanelWindow.RefreshList();
        Save();
    }

    /// <summary>More options › Duplicate of a macro: as a loop's (<see cref="DuplicateLoop"/>).</summary>
    private void DuplicateMacro(MacroItemViewModel item)
    {
        if (_engine.AnyRunning)
            return;
        var copy = item.Definition.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = ItemNames.Copy(copy.Name, _macros.Macros.Select(m => m.Name));
        copy.Enabled = false;
        copy.Hotkey = null;
        if (copy.Mode == ActivationMode.Hold)
            copy.Mode = ActivationMode.Toggle;
        _macros.Macros.Insert(_macros.Macros.IndexOf(item.Definition) + 1, copy);
        _macroItems.Insert(_macroItems.IndexOf(item) + 1, CreateMacroItem(copy));
        SaveMacro(copy);
        SaveMacroOrder();
        RefreshMacros();
        _overlayPanelWindow.RefreshList();
    }

    /// <summary>More options › Move up / Move down of a remap, as a loop's (<see cref="MoveItem"/>).</summary>
    private void MoveRemap(RemapItemViewModel item, int newIndex)
    {
        int oldIndex = _remapItems.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, _remapItems.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
            return;
        _remapItems.Move(oldIndex, newIndex);
        _settings.Remaps = _remapItems.Select(r => r.Definition).ToList();
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
            Title = Strings.Remaps_DeleteTitle,
            Content = string.Format(CultureInfo.CurrentCulture, Strings.Remaps_DeleteConfirm, item.SourceText, item.TargetText),
            PrimaryButtonText = Strings.Card_Delete,
            CloseButtonText = Strings.Main_Cancel,
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
        RemoveRemap(item);
    }

    /// <summary>Deletes a remap, after the user confirmed.</summary>
    private void RemoveRemap(RemapItemViewModel item)
    {
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

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        Save();
        var options = new Views.ExportOptionsView(BackupService.Sizes(AppPaths.DataFolder));
        var choose = new ContentDialog
        {
            Title = Strings.Export_Title,
            Content = options,
            PrimaryButtonText = Strings.Settings_ExportButton,
            CloseButtonText = Strings.Main_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult answer;
        _userDialogs++;
        try
        {
            answer = await choose.ShowAsync(this);
        }
        finally
        {
            _userDialogs--;
        }
        if (answer != ContentDialogResult.Primary)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Export_FileDialogTitle,
            FileName = $"PuppyMacro-{DateTime.Now:yyyy-MM-dd}{BackupService.Extension}",
            Filter = string.Format(CultureInfo.CurrentCulture, Strings.Backup_FileFilter, BackupService.Extension),
            DefaultExt = BackupService.Extension,
        };
        if (ShowUserDialog(() => dialog.ShowDialog(this)) != true)
            return;
        try
        {
            BackupService.Export(AppPaths.DataFolder, dialog.FileName, options.Parts);
            HideSettingsError();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowSettingsError(string.Format(CultureInfo.CurrentCulture, Strings.Export_Failed, ex.Message));
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Backup_ImportFileDialogTitle,
            Filter = string.Format(CultureInfo.CurrentCulture, Strings.Backup_FileFilter, BackupService.Extension),
        };
        if (ShowUserDialog(() => dialog.ShowDialog(this)) != true)
            return;

        BackupParts parts;
        try
        {
            parts = BackupService.ReadParts(dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            ShowSettingsError(string.Format(CultureInfo.CurrentCulture, Strings.Backup_ImportFailed, ex.Message));
            return;
        }

        var confirm = new ContentDialog
        {
            Title = Strings.Backup_ImportTitle,
            Content = string.Format(CultureInfo.CurrentCulture, Strings.Backup_ImportConfirm, PartNames(parts)),
            PrimaryButtonText = Strings.Backup_ImportAndRestart,
            CloseButtonText = Strings.Main_Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult answer;
        _userDialogs++;
        try
        {
            answer = await confirm.ShowAsync(this);
        }
        finally
        {
            _userDialogs--;
        }
        if (answer != ContentDialogResult.Primary)
            return;

        try
        {
            _engine.StopAll();
            Save();
            BackupService.Import(AppPaths.DataFolder, dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            ShowSettingsError(string.Format(CultureInfo.CurrentCulture, Strings.Backup_ImportFailed, ex.Message));
            return;
        }
        _exiting = true;
        _importing = true;
        App.Restart();
    }

    /// <summary>"loops, macros, remaps and settings, History and AI activity".</summary>
    private static string PartNames(BackupParts parts)
    {
        var names = new List<string> { Strings.Backup_PartData };
        if (parts.HasFlag(BackupParts.History))
            names.Add(Strings.Backup_PartHistory);
        if (parts.HasFlag(BackupParts.AiActivity))
            names.Add(Strings.Backup_PartAiActivity);
        if (parts.HasFlag(BackupParts.Logs))
            names.Add(Strings.Backup_PartLogs);
        return names.Count == 1 ? names[0]
            : string.Format(CultureInfo.CurrentCulture, Strings.Main_ListLastAnd,
                string.Join(Strings.ListSeparator, names.Take(names.Count - 1)), names[^1]);
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.DataFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\""));
    }
}
