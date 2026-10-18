using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Screen;
using PuppyMacro.Native;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The AI page, the status bar and what AI apps ask through the MCP server (<see cref="AiServer"/>, <see cref="AiTools"/>):
/// this window is their <see cref="IAiHost"/>. Their changes are saved as the editors' Save does, recorded in the History by
/// the app's name; confirmations and interactions open a PuppyMacro window the user answers in.
/// </summary>
public partial class MainWindow : IAiHost
{
    private const int RecentCount = 4;

    private AiActivityLog _aiLog = null!;
    private AiTools _aiTools = null!;
    private AiServer _aiServer = null!;
    private DispatcherTimer _aiRefreshTimer = null!;
    private string? _aiAppsShown;
    private string? _aiRecentShown;
    private string? _aiStatusShown;
    private readonly Dictionary<string, ImageSource?> _exeIcons = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Claude's Microsoft Store folder, asked to Windows again only when claude.exe is no longer there (an update).</summary>
    private string? _claudeStoreFolder;

    /// <summary>The paths of the AI apps' settings (the user's folders).</summary>
    private static readonly string AppDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string UserProfileFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string ProgramFilesFolder = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>Starts the MCP server (also in the tray; with Allow AI access off it refuses every request).</summary>
    private void InitializeAi()
    {
        _aiLog = new AiActivityLog(AppPaths.AiActivityFile);
        _aiLog.Load();
        _aiTools = new AiTools(this, _aiLog);
        _aiServer = new AiServer(_aiTools, _aiLog, () => _settings.AllowAiAccess);
        _aiLog.Changed += () => Dispatcher.InvokeAsync(RefreshAiPage);
        _aiServer.ClientsChanged += () => Dispatcher.InvokeAsync(RefreshAiPage);
        _aiServer.Start();

        AiAccessSwitch.IsOn = _settings.AllowAiAccess;
        AiAskDeleteSwitch.IsOn = _settings.AiAskBeforeDeleting;
        AiAskChangeSwitch.IsOn = _settings.AiAskBeforeChanging;
        AiAskSettingsSwitch.IsOn = _settings.AiAskBeforeSettings;
        AiSeeScreenSwitch.IsOn = _settings.AiSeeScreen;
        AiTestMacrosSwitch.IsOn = _settings.AiTestMacros;
        AiStartSwitch.IsOn = _settings.AiStartWhenNeeded;
        // The apps' settings change outside PuppyMacro (Install in VS Code, a restart of Claude Desktop): looked at again
        // while the page shows.
        _aiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _aiRefreshTimer.Tick += (_, _) => RefreshAiPage();
        Activated += (_, _) => RefreshAiPage();
        RefreshAiPage();
    }

    private void StopAi()
    {
        _aiRefreshTimer.Stop();
        _engine.StopReadyKeys();
        _aiServer.Dispose();
    }

    /// <summary>The AI page shows: its states are refreshed every few seconds while it does.</summary>
    private void OnAiPageShown(bool shown)
    {
        if (shown)
        {
            RefreshAiPage();
            _aiRefreshTimer.Start();
        }
        else
        {
            _aiRefreshTimer?.Stop(); // also before InitializeAi
        }
    }

    // ================= AI page =================

    private void OnAiAccessChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.AllowAiAccess = AiAccessSwitch.IsOn;
        Save();
        RefreshAiPage();
    }

    private void OnAiPermissionChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.AiAskBeforeDeleting = AiAskDeleteSwitch.IsOn;
        _settings.AiAskBeforeChanging = AiAskChangeSwitch.IsOn;
        _settings.AiAskBeforeSettings = AiAskSettingsSwitch.IsOn;
        _settings.AiSeeScreen = AiSeeScreenSwitch.IsOn;
        _settings.AiTestMacros = AiTestMacrosSwitch.IsOn;
        _settings.AiStartWhenNeeded = AiStartSwitch.IsOn;
        Save();
    }

    /// <summary>The status bar: the AI page.</summary>
    private void OnAiStatusBarClick(object sender, MouseButtonEventArgs e) => Rail.Select("AI");

    private void RefreshAiPage()
    {
        if (_aiServer == null)
            return;
        List<AiClient> clients = _aiServer.Clients;
        AiConnectionError.Message = _aiServer.ListenError ?? "";
        AiConnectionError.IsOpen = _aiServer.ListenError != null;
        RefreshStatusBar(clients);

        List<AiAppTile> tiles = AppTiles(clients);
        string appsShown = string.Join("|", tiles.Select(t => t.Shown));
        // Rebuilt only when something changed: new cards would take the keyboard focus and the hover off the buttons.
        if (appsShown != _aiAppsShown)
        {
            _aiAppsShown = appsShown;
            AiAppsPanel.Children.Clear();
            foreach (AiAppTile tile in tiles)
                AiAppsPanel.Children.Add(AppCard(tile));
        }

        List<AiActivityRow> recent = _aiLog.Entries().Take(RecentCount).Select(e => new AiActivityRow(e)).ToList();
        string recentShown = string.Join("|", recent.Select(r => $"{r.Entry.Time:O}:{r.Request}:{r.ResultText}:{r.CanRestore}"));
        if (recentShown != _aiRecentShown)
        {
            _aiRecentShown = recentShown;
            AiRecentList.ItemsSource = recent;
        }
        AiRecentList.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiNoActivityText.Visibility = recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The status bar at the bottom of the window: the AI apps connected now; hidden while none is.</summary>
    private void RefreshStatusBar(List<AiClient> clients)
    {
        List<string> names = clients.Select(c => c.Name).Distinct().ToList();
        string shown = string.Join("|", names);
        if (shown == _aiStatusShown)
            return;
        _aiStatusShown = shown;
        AiStatusList.ItemsSource = names.Select(n => new AiStatusItem(n)).ToList();
        AiStatusBar.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<AiAppTile> AppTiles(List<AiClient> clients)
    {
        string exe = AppPaths.ExecutablePath;
        bool access = _settings.AllowAiAccess;
        AiClient? Live(AiAppKind kind) => clients.FirstOrDefault(c => c.Kind == kind);
        static string Since(AiClient client) =>
            string.Format(CultureInfo.CurrentCulture, Strings.AiApps_ConnectedSince, client.Since.ToString("t", CultureInfo.CurrentCulture));

        string claudeConfig = AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder);
        bool claudeInstalled = AiApps.ClaudeInstalled(AppDataFolder, LocalAppDataFolder);
        bool claudeAdded = AiApps.ClaudeHas(claudeConfig, exe);
        AiClient? claude = Live(AiAppKind.ClaudeDesktop);
        string? claudeExe = AiApps.ClaudeExe(LocalAppDataFolder, _claudeStoreFolder);
        if (claudeExe == null && claudeInstalled)
        {
            _claudeStoreFolder = AiApps.ClaudeStoreFolder(LocalAppDataFolder);
            claudeExe = AiApps.ClaudeExe(LocalAppDataFolder, _claudeStoreFolder);
        }

        string? codeExe = AiApps.VsCodeExe(LocalAppDataFolder, ProgramFilesFolder);
        bool codeAdded = AiApps.VsCodeHas(AiApps.VsCodeConfigPath(AppDataFolder), exe);
        AiClient? code = Live(AiAppKind.VsCode);

        bool codexInstalled = AiApps.CodexInstalled(UserProfileFolder);
        bool codexAdded = AiApps.CodexHas(AiApps.CodexConfigPath(UserProfileFolder), exe);
        AiClient? codex = Live(AiAppKind.Codex);

        var others = clients.Where(c => c.Kind == AiAppKind.Other).Select(c => c.Name).Distinct().ToList();
        return new List<AiAppTile>
        {
            new()
            {
                Id = "claude", Name = "Claude Desktop",
                Icon = ExeIcon(claudeExe),
                Status = claude != null ? Since(claude)
                    : claudeAdded ? Strings.AiApps_ClaudeAdded
                    : claudeInstalled ? Strings.AiApps_InstalledNotConnected : Strings.AiApps_NotInstalled,
                Connected = claude != null, Waiting = claude == null && claudeAdded,
                ActionText = claudeAdded ? Strings.AiApps_Remove : claudeInstalled ? Strings.AiApps_Connect : null,
                ActionIsAccent = !claudeAdded, ActionEnabled = access,
                LinkText = claudeInstalled ? null : Strings.AiApps_GetClaude,
                LinkUri = claudeInstalled ? null : new Uri("https://claude.ai/download"),
            },
            new()
            {
                Id = "vscode", Name = "Visual Studio Code",
                Icon = ExeIcon(codeExe),
                Status = code != null ? Since(code)
                    : codeAdded ? Strings.AiApps_VsCodeAdded
                    : codeExe != null ? Strings.AiApps_VsCodeInstalled : Strings.AiApps_NotInstalled,
                Connected = code != null, Waiting = code == null && codeAdded,
                ActionText = codeAdded ? Strings.AiApps_Remove : codeExe != null ? Strings.AiApps_Connect : null,
                ActionIsAccent = !codeAdded, ActionEnabled = access,
                LinkText = codeExe != null ? null : Strings.AiApps_GetVsCode,
                LinkUri = codeExe != null ? null : new Uri("https://code.visualstudio.com/"),
            },
            new()
            {
                Id = "codex", Name = "Codex",
                Status = codex != null ? Since(codex)
                    : codexAdded ? Strings.AiApps_CodexAdded
                    : codexInstalled ? Strings.AiApps_InstalledNotConnected : Strings.AiApps_NotInstalled,
                Connected = codex != null, Waiting = codex == null && codexAdded,
                ActionText = codexAdded ? Strings.AiApps_Remove : codexInstalled ? Strings.AiApps_Connect : null,
                ActionIsAccent = !codexAdded, ActionEnabled = access,
                LinkText = codexInstalled ? null : Strings.AiApps_GetCodex,
                LinkUri = codexInstalled ? null : new Uri("https://chatgpt.com/codex"),
            },
            new()
            {
                Id = "other", Name = Strings.AiApps_OtherApps,
                Status = others.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.AiApps_OthersConnected, string.Join(Strings.ListSeparator, others))
                    : Strings.AiApps_OthersHint,
                Connected = others.Count > 0,
                ActionText = Strings.AiApps_CopyConfiguration, ActionEnabled = access,
            },
        };
    }

    private ImageSource? ExeIcon(string? exe)
    {
        if (exe == null)
            return null;
        if (!_exeIcons.TryGetValue(exe, out ImageSource? icon))
            _exeIcons[exe] = icon = AiAppTile.ExeIcon(exe);
        return icon;
    }

    /// <summary>An app's row: a standard SettingsCard with the app's icon, its state and its button.</summary>
    private SettingsCard AppCard(AiAppTile tile)
    {
        var status = new StackPanel { Orientation = Orientation.Horizontal };
        if (tile.Connected || tile.Waiting)
        {
            status.Children.Add(new InfoBadge
            {
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource(tile.Connected ? "SuccessDotInfoBadgeStyle" : "CautionDotInfoBadgeStyle"),
            });
        }
        status.Children.Add(new TextBlock { Text = tile.Status, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (tile.LinkText != null && tile.LinkUri != null)
            actions.Children.Add(new HyperlinkButton { Content = tile.LinkText, NavigateUri = tile.LinkUri });
        if (tile.ActionText != null)
        {
            var button = new Button { Content = tile.ActionText, Tag = tile, IsEnabled = tile.ActionEnabled, Margin = new Thickness(8, 0, 0, 0) };
            if (tile.ActionIsAccent)
                button.Style = (Style)FindResource("AccentButtonStyle"); // otherwise the implicit (standard) style
            System.Windows.Automation.AutomationProperties.SetName(button, string.Format(CultureInfo.CurrentCulture, Strings.AiApps_ActionName, tile.ActionText, tile.Name));
            button.Click += OnAiAppActionClick;
            actions.Children.Add(button);
        }

        var card = new SettingsCard
        {
            Header = tile.Name,
            Description = status,
            HeaderIcon = tile.Icon != null ? new ImageIcon { Source = tile.Icon } : new FontIcon { Icon = tile.Id == "other" ? SegoeFluentIcons.Link : SegoeFluentIcons.AppIconDefault },
            Content = actions,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, tile.Name);
        return card;
    }

    private void OnAiAppActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AiAppTile tile)
            return;
        AiMessage.IsOpen = false;
        string exe = AppPaths.ExecutablePath;
        string? error = null;
        string? done = null;
        // The button's text tells Connect from Remove (both in the language shown).
        bool connect = tile.ActionText == Strings.AiApps_Connect;
        switch (tile.Id, connect)
        {
            case ("claude", true):
                error = AiApps.ClaudeAdd(AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder), exe);
                break;
            case ("claude", false):
                error = AiApps.ClaudeRemove(AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder));
                break;
            case ("vscode", true):
                error = Open(AiApps.VsCodeInstallUri(exe));
                break;
            case ("vscode", false):
                error = AiApps.VsCodeRemove(AiApps.VsCodeConfigPath(AppDataFolder));
                break;
            case ("codex", true):
                error = AiApps.CodexAdd(AiApps.CodexConfigPath(UserProfileFolder), exe);
                break;
            case ("codex", false):
                error = AiApps.CodexRemove(AiApps.CodexConfigPath(UserProfileFolder));
                break;
            case ("other", _):
                Clipboard.SetText(AiApps.GenericConfiguration(exe));
                done = Strings.AiApps_ConfigurationCopied;
                break;
        }
        ShowAiMessage(error, done);
        RefreshAiPage();
    }

    private void ShowAiMessage(string? error, string? done)
    {
        if (error == null && done == null)
            return;
        AiMessage.Severity = error != null ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        AiMessage.Message = error ?? done;
        AiMessage.IsOpen = true;
    }

    /// <summary>Opens a link with the app Windows gives it (vscode: opens VS Code). Null when done, otherwise why not.</summary>
    private static string? Open(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiApps_VsCodeDidNotOpen, ex.Message);
        }
    }

    private void OnAiViewAllClick(object sender, RoutedEventArgs e)
    {
        var window = new AiActivityWindow(_aiLog, RestoreActivity) { Owner = this };
        ShowUserDialog(window.ShowDialog);
    }

    private void OnAiRestoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AiActivityRow { CanRestore: true } row)
            ShowAiMessage(RestoreActivity(row.Entry), null);
    }

    /// <summary>
    /// Restore of an AI app's change or deletion: what was there before comes back, as the user's own change (in the History
    /// too). Null when done, otherwise why not.
    /// </summary>
    private string? RestoreActivity(AiActivity entry)
    {
        if (entry.Before == null || entry.Restored)
            return null;
        try
        {
            if (entry.ItemKind == "settings")
            {
                if (JsonSerializer.Deserialize<AiSettingsValues>(entry.Before, CodeJson.Options) is not { } values)
                    return Strings.AiActivity_CantRestoreSettings;
                if (_aiTools.RestoreSettings(values) is string problem)
                    return problem;
            }
            else
            {
                if (entry.ItemKind == "component")
                {
                    if (JsonSerializer.Deserialize<LibraryComponent>(entry.Before, CodeJson.Options) is not { } component)
                        return Strings.AiActivity_CantRestore;
                    if (SaveComponent(component, null) is string notRestored)
                        return notRestored;
                    _aiLog.MarkRestored(entry);
                    return null;
                }
                object? item = entry.ItemKind switch
                {
                    "loop" => JsonSerializer.Deserialize<LoopDefinition>(entry.Before, CodeJson.Options),
                    "macro" => JsonSerializer.Deserialize<MacroDefinition>(entry.Before, CodeJson.Options),
                    "remap" => JsonSerializer.Deserialize<RemapDefinition>(entry.Before, CodeJson.Options),
                    _ => null,
                };
                if (item == null)
                    return Strings.AiActivity_CantRestore;
                // A macro version that links a component the Library doesn't have any more can't play: not restored.
                if (item is MacroDefinition linking && Library.Missing(linking).Count > 0)
                    return Strings.Code_ComponentMissing;
                // The images of the version before the change: the item's History keeps them.
                if (item is MacroDefinition && entry.ItemId is Guid restoredId)
                    _itemHistory?.RestoreImages(restoredId, entry.Before);
                SaveItem(item);
            }
        }
        catch (JsonException ex)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.AiActivity_CantRestoreReason, ex.Message);
        }
        _aiLog.MarkRestored(entry);
        return null;
    }

    // ================= IAiHost: what the AI tools ask the window =================

    AppSettings IAiHost.Settings => _settings;

    MacroLibrary IAiHost.Macros => _macros;

    bool IAiHost.IsRunning(Guid id) => _engine.IsRunning(id);

    bool IAiHost.AnyRunning => _engine.AnyRunning;

    bool IAiHost.OverlayModeActive => _overlayModeActive;

    /// <summary>
    /// An editor, a confirmation, the placement overlay or a recording (they all suspend the hotkeys), or another dialog
    /// (<see cref="ShowUserDialog"/>) is open.
    /// </summary>
    bool IAiHost.UserIsEditing => _engine.HotkeysSuspended || _userDialogs > 0;

    Task<T> IAiHost.Ui<T>(Func<Task<T>> action) => Dispatcher.InvokeAsync(action).Task.Unwrap();

    // Why not in English: it is the AI app's answer (i18n.md); a saved change shows in the user's language.
    string? IAiHost.AddCollections(IReadOnlyList<LibraryCollection> collections, string app) =>
        SavingAs(app, () => AddCollections(collections, CultureInfo.InvariantCulture));

    string? IAiHost.SaveComponent(LibraryComponent component, LibraryCollection? created, string app,
        IReadOnlyList<MacroDefinition>? macros, IReadOnlyList<LibraryComponent>? callers) =>
        SavingAs(app, () => SaveComponent(component, created, macros, callers, CultureInfo.InvariantCulture));

    void IAiHost.Save(object item, string app) => SavingAs(app, () =>
    {
        AiTools.KeepUsersDebug(item, _macros);
        SaveItem(item);
        return true;
    });

    /// <summary>Adds a loop, macro or remap, or replaces the one with its Id, as the editors' Save does (a running one stops).</summary>
    private void SaveItem(object item)
    {
        switch (item)
        {
            case LoopDefinition loop:
                if (_items.FirstOrDefault(i => i.Definition.Id == loop.Id) is { } loopItem)
                    ApplyLoopEdit(loopItem, loop);
                else
                    AddLoop(loop);
                break;
            case MacroDefinition macro:
                if (_macroItems.FirstOrDefault(i => i.Definition.Id == macro.Id) is { } macroItem)
                {
                    _engine.StopMacro(macro.Id);
                    ApplyMacroEdit(macroItem, macro);
                }
                else
                {
                    AddMacro(macro);
                }
                break;
            case RemapDefinition remap:
                if (_remapItems.FirstOrDefault(i => i.Definition.Id == remap.Id) is { } remapItem)
                    ApplyRemapEdit(remapItem, remap);
                else
                    AddRemap(remap);
                break;
        }
    }

    void IAiHost.Delete(Guid id, string app) => SavingAs(app, () =>
    {
        if (_items.FirstOrDefault(i => i.Definition.Id == id) is { } loop)
            RemoveLoop(loop);
        else if (_macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macro)
            RemoveMacro(macro);
        else if (_remapItems.FirstOrDefault(i => i.Definition.Id == id) is { } remap)
            RemoveRemap(remap);
        return true;
    });

    /// <summary>As the card's switch: a disabled loop or macro stops, and it is saved.</summary>
    void IAiHost.SetEnabled(Guid id, bool enabled, string app) => SavingAs(app, () =>
    {
        if (_items.FirstOrDefault(i => i.Definition.Id == id) is { } loop)
            loop.IsLoopEnabled = enabled;
        else if (_macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macro)
            macro.IsLoopEnabled = enabled;
        else if (_remapItems.FirstOrDefault(i => i.Definition.Id == id) is { } remap)
            remap.IsLoopEnabled = enabled;
        return true;
    });

    /// <summary>Through the Settings page's controls, so everything they do on a change happens too.</summary>
    void IAiHost.ApplySettings(AiSettingsValues values)
    {
        if (values.Theme is AppTheme theme)
            ThemeComboBox.SelectedIndex = (int)theme;
        if (values.CloseToTray is bool tray)
            CloseToTraySwitch.IsOn = tray;
        if (values.ShowOverlayPanel is bool panel)
            ShowPanelSwitch.IsOn = panel;
        if (values.ClickItemsInPanel is bool click)
            ClickInPanelSwitch.IsOn = click;
        if (values.OverlayPanelOpacity is int opacity)
            OpacitySlider.Value = opacity;
        if (values.SoundVolume is int volume)
            VolumeSlider.Value = volume;
        if (values.RecognitionDevice is RecognitionDevice device)
            RecognitionDeviceComboBox.SelectedIndex = (int)device;
        if (values.StopAllHotkey is { IsSet: true } stopAll)
            _settings.StopAllHotkey = stopAll.Clone();
        if (values.OverlayModeHotkey is { IsSet: true } overlay)
            _settings.OverlayModeHotkey = overlay.Clone();
        if (values.RecordHotkey is { IsSet: true } record)
            _settings.RecordHotkey = record.Clone();
        if (values.OpenMainHotkey is { IsSet: true } openMain)
            _settings.OpenMainHotkey = openMain.Clone();
        UpdateHotkeyLabels();
        Save();
    }

    Task<AiAnswer> IAiHost.Confirm(string app, string title, IReadOnlyList<string> lines, string primary)
    {
        AiRequestWindow window = AiRequestWindow.Confirmation(app, title, lines, primary);
        ShowForAi(window);
        return Task.FromResult(window.Answer);
    }

    /// <summary>
    /// The "ready?" window: not modal and not activated, so the user keeps working in the app they prepare; Enter or Esc
    /// from any app (the engine's WatchReadyKeys), Start, or nothing for <see cref="AiTools.AnswerTimeout"/>.
    /// </summary>
    async Task<AiAnswer> IAiHost.Ready(string app, AiAsk ask)
    {
        var window = new AiReadyWindow(app, ask);
        var answered = new TaskCompletionSource<AiAnswer>();
        window.Answered += answer => answered.TrySetResult(answer);
        var timeout = new DispatcherTimer { Interval = AiTools.AnswerTimeout };
        timeout.Tick += (_, _) => window.Answer(AiAnswer.TimedOut);
        _engine.WatchReadyKeys(() => window.Answer(AiAnswer.Done), () => window.Answer(AiAnswer.Cancelled));
        window.Show();
        timeout.Start();
        try
        {
            return await answered.Task;
        }
        finally
        {
            timeout.Stop();
            _engine.StopReadyKeys();
            window.Close();
        }
    }

    Task<(AiAnswer Answer, List<(int X, int Y)> Points)> IAiHost.PickPoints(string app, string message, IReadOnlyList<string> labels)
    {
        var picker = new PickPointWindow(askedBy: app, reason: message, labels: labels);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, answer == AiAnswer.Done ? picker.Points.ToList() : new List<(int X, int Y)>()));
    }

    Task<(AiAnswer Answer, string? Exe)> IAiHost.PickApp(string app, string message)
    {
        var picker = new PickPointWindow(pickApp: true, askedBy: app, reason: message);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, answer == AiAnswer.Done ? picker.ResultApp : null));
    }

    Task<(AiAnswer Answer, AiScreenPick? Pick)> IAiHost.PickOnScreen(string app, string message, AiScreenPickKind kind)
    {
        var mode = kind switch { AiScreenPickKind.Image => ScreenPickMode.Image, AiScreenPickKind.Area => ScreenPickMode.Area, _ => ScreenPickMode.Pixel };
        if (ScreenPickWindow.Create(mode, out _, app, message) is not { } picker)
            return Task.FromResult<(AiAnswer, AiScreenPick?)>((AiAnswer.Cancelled, null));
        AiAnswer answer = ShowForAi(picker);
        AiScreenPick? pick = null;
        if (answer == AiAnswer.Done && picker.PickedPixel is { } pixel)
            pick = new AiScreenPick(pixel.X, pixel.Y, 1, 1, Color: pixel.Color);
        else if (answer == AiAnswer.Done && picker.PickedArea is { } area)
            pick = new AiScreenPick(area.Area.X, area.Area.Y, area.Area.Width, area.Area.Height,
                ImageId: area.Png is { } png ? TakeForAi(png) : null);
        return Task.FromResult((pick == null && answer == AiAnswer.Done ? AiAnswer.Cancelled : answer, pick));
    }

    /// <summary>An image an AI app took: held until PuppyMacro closes, so it is there when the AI saves the macro that uses it.</summary>
    private string TakeForAi(byte[] png)
    {
        string id = _macros.Images.Add(png);
        _macros.Images.HoldAlso(typeof(AiTools), id);
        return id;
    }

    Task<(AiAnswer Answer, HotkeyBinding? Keys)> IAiHost.CaptureKeys(string app, AiAsk ask)
    {
        AiRequestWindow window = AiRequestWindow.KeyRequest(app, ask, _engine);
        ShowForAi(window);
        return Task.FromResult((window.Answer, window.Keys));
    }

    /// <summary>
    /// As the editor's Floating button switch and Position…: Show in overlay and the button on (its label proposed from the
    /// name when empty), then the placement screen with the whole overlay. Saved only when the user presses Enter there.
    /// </summary>
    Task<AiAnswer> IAiHost.PlaceFloatingButton(Guid id, string app)
    {
        object? copy = null;
        string name = "";
        HotkeyBinding? hotkey = null;
        FloatingButton? button = null;
        if (_items.FirstOrDefault(i => i.Definition.Id == id) is { } loopItem)
        {
            LoopDefinition loop = loopItem.Definition.Clone();
            loop.ShowInOverlay = true;
            (copy, name, hotkey, button) = (loop, loop.Name, loop.Hotkey, loop.FloatingButton);
        }
        else if (_macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macroItem)
        {
            MacroDefinition macro = macroItem.Definition.Clone();
            macro.ShowInOverlay = true;
            (copy, name, hotkey, button) = (macro, macro.Name, macro.Hotkey, macro.FloatingButton);
        }
        if (copy == null || button == null)
            return Task.FromResult(AiAnswer.Cancelled);
        button.Enabled = true;
        if (string.IsNullOrEmpty(button.Label))
            button.Label = FloatingButton.DefaultLabel(name);

        AiAnswer answer = AiAnswer.Cancelled;
        Point? moved = OpenPlacement(new EditedFloatingButton(id, name, hotkey is { IsSet: true } ? KeyNames.Format(hotkey) : "", button),
            placement =>
            {
                answer = ShowForAi(placement);
                return answer == AiAnswer.Done;
            });
        if (answer != AiAnswer.Done)
            return Task.FromResult(answer);
        if (moved is Point at)
        {
            button.X = at.X;
            button.Y = at.Y;
        }
        SavingAs(app, () =>
        {
            SaveItem(copy);
            return true;
        });
        return Task.FromResult(AiAnswer.Done);
    }

    /// <summary>
    /// The item's editor, centered on the screen and in front, with the AI's title and description in its notice bar: the
    /// user's own changes (saved as You). Done when the user saved.
    /// </summary>
    Task<AiAnswer> IAiHost.OpenItem(Guid id, string app, string title, string description)
    {
        void ForAi(Window editor)
        {
            editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AiWindow.BringToFront(editor);
            AiWindow.Notice(editor switch
            {
                LoopEditorWindow loop => loop.NoticeBar,
                MacroEditorWindow macro => macro.NoticeBar,
                RemapEditorWindow remap => remap.NoticeBar,
                _ => throw new InvalidOperationException("Not an editor."),
            }, app, title, description);
        }

        bool saved = _items.FirstOrDefault(i => i.Definition.Id == id) is { } loop ? EditLoop(loop, ForAi)
            : _macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macro ? EditMacro(macro, ForAi)
            : _remapItems.FirstOrDefault(i => i.Definition.Id == id) is { } remap && EditRemap(remap, ForAi);
        return Task.FromResult(saved ? AiAnswer.Done : AiAnswer.Cancelled);
    }

    void IAiHost.StopAll() => _engine.StopAll();

    IReadOnlyList<string> IAiHost.RunningApps() => AppWindows.RunningApps();

    Task<ConditionReport> IAiHost.CheckCondition(MacroCondition condition)
    {
        // Off the UI thread, as the condition window checks (text recognition can take a moment).
        var device = _settings.RecognitionDevice;
        var images = _macros.Images;
        return Task.Run(() =>
        {
            using var checker = new ConditionChecker(ScreenCapture.Shared, images, device, 0);
            return checker.Inspect(condition);
        });
    }

    Task<List<(OpenCvSharp.Rect Bounds, OpenCvSharp.Mat Picture)>> IAiHost.TakeScreen() => Task.Run(() =>
    {
        using var snapshot = ScreenSnapshot.Take();
        return snapshot.Monitors.Select(m => (m.Bounds, m.Picture.Clone())).ToList();
    });

    /// <summary>
    /// The recorder for an AI app: every monitor's capture runs while it records, so each key press and click takes every
    /// monitor's picture at once (on a thread-pool thread: the input hook never waits). The main window is hidden
    /// meanwhile, as for the user's own recording, and shown again only if it was visible.
    /// </summary>
    Task<(AiAnswer Answer, AiRecording? Recording)> IAiHost.Record(string app, bool mouseMovement)
    {
        var result = new TaskCompletionSource<(AiAnswer, AiRecording?)>();
        var watches = ScreenCapture.MonitorBounds().Select(b => ScreenCapture.Shared.Start(b)).OfType<IScreenWatch>().ToList();
        var inputs = new List<AiRecordedInput>();
        int copying = 0;
        void OnInput(RawEventKind kind, int vk, int x, int y, double t)
        {
            Interlocked.Increment(ref copying);
            try
            {
                var screen = new List<(OpenCvSharp.Rect, OpenCvSharp.Mat)>();
                foreach (var watch in watches.Where(w => !w.Failed && w.Version > 0))
                {
                    try
                    {
                        if (watch.Copy(watch.Bounds) is { } picture)
                            screen.Add((watch.Bounds, picture));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        CaptureLog.Write($"a recorded input's picture failed: {ex.Message}");
                    }
                }
                bool click = kind == RawEventKind.ButtonDown;
                string what = Loc.English(() => click ? $"Click {KeyNames.Get(vk)} at {x},{y}" : $"Key down {KeyNames.Get(vk)}");
                lock (inputs)
                    inputs.Add(new AiRecordedInput(what, click, x, y, t, screen));
            }
            finally
            {
                Interlocked.Decrement(ref copying);
            }
        }
        _engine.RecordedInput += OnInput;
        bool wasVisible = IsVisible;
        RecordingSession.RunForAi(_engine, _settings, mouseMovement,
            hide: () => { if (wasVisible) Hide(); },
            restore: () => { if (wasVisible) Show(); },
            done: events =>
            {
                _engine.RecordedInput -= OnInput;
                _ = Task.Run(async () =>
                {
                    // The last inputs' pictures may still be on their way.
                    for (int waited = 0; Volatile.Read(ref copying) > 0 && waited < 2000; waited += 20)
                        await Task.Delay(20);
                    foreach (var watch in watches)
                        watch.Dispose();
                    List<AiRecordedInput> taken;
                    lock (inputs)
                        taken = inputs.OrderBy(i => i.TimeMs).ToList();
                    if (events == null)
                    {
                        foreach (var input in taken)
                            foreach (var (_, picture) in input.Screen)
                                picture.Dispose();
                        result.SetResult((AiAnswer.Cancelled, null));
                    }
                    else
                    {
                        result.SetResult((AiAnswer.Done, new AiRecording(MacroBuilder.Build(events), taken)));
                    }
                });
            });
        return result.Task;
    }

    /// <summary>
    /// An AI app's test (test_macro): the app the user brought in front gets the front back from the ready window (its
    /// Start may have activated PuppyMacro), then the macro plays as Play would, always logged, until it ends, Stop all or
    /// the time limit.
    /// </summary>
    async Task<AiTestRun?> IAiHost.TestMacro(Guid id, string app, double maxSeconds, string folder)
    {
        if (_macros.Find(id) is not { } macro || _engine.AnyRunning)
            return null;
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        if (pid == (uint)Environment.ProcessId && _engine.LastOtherWindow != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(_engine.LastOtherWindow);
        await Task.Delay(300);
        var ended = new TaskCompletionSource();
        MacroRunner? runner = _engine.StartMacroTest(macro, folder, $"a test by {app}", _ => ended.TrySetResult());
        if (runner == null)
            return null;
        if (await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(maxSeconds))) != ended.Task)
        {
            _engine.StopMacro(id, $"stopped at the test's time limit of {maxSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
            await Task.WhenAny(ended.Task, Task.Delay(5000));
        }
        // The last lines and pictures are written by the debug writer.
        await Task.Run(() => RunLog.WaitWritten(TimeSpan.FromSeconds(30)));
        return new AiTestRun(runner.EndReason ?? "it was still ending", folder);
    }

    /// <summary>The window that says the AI app's request on the screen is over (the app's own name, or none).</summary>
    void IAiHost.Finished(string app)
    {
        var window = new AiFinishedWindow(app);
        window.Show();
    }

    /// <summary>Dialogs open that do not suspend the hotkeys (file dialogs, AI activity, Import's confirmation).</summary>
    private int _userDialogs;

    /// <summary>Shows a dialog that does not suspend the hotkeys: meanwhile AI requests wait (<see cref="IAiHost.UserIsEditing"/>).</summary>
    private bool? ShowUserDialog(Func<bool?> show)
    {
        _userDialogs++;
        try
        {
            return show();
        }
        finally
        {
            _userDialogs--;
        }
    }

    /// <summary>
    /// Shows a window for an AI request as the editors open (hotkeys off, this window hidden meanwhile), in front of the
    /// other apps and centered on the screen, wherever this window is. Closes it when nobody used PuppyMacro (any key,
    /// click or move in any of its windows) for <see cref="AiTools.AnswerTimeout"/>.
    /// </summary>
    private AiAnswer ShowForAi(Window dialog)
    {
        // The picker and the placement screen cover the screen, are topmost and must stay so, and activate themselves.
        if (dialog is not (PickPointWindow or PlacementWindow or ScreenPickWindow))
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AiWindow.BringToFront(dialog);
        }

        bool timedOut = false;
        var timer = new DispatcherTimer { Interval = AiTools.AnswerTimeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timedOut = true;
            if (dialog is AiRequestWindow window)
                window.TimeOut();
            else
                dialog.DialogResult = false;
        };
        void OnInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyEventArgs && timer.IsEnabled)
            {
                timer.Stop();
                timer.Start();
            }
        }
        dialog.Loaded += (_, _) => timer.Start();
        InputManager.Current.PreProcessInput += OnInput;
        try
        {
            bool? result = ShowDialogWithoutHotkeys(dialog);
            return dialog is AiRequestWindow request ? request.Answer
                : timedOut ? AiAnswer.TimedOut : result == true ? AiAnswer.Done : AiAnswer.Cancelled;
        }
        finally
        {
            InputManager.Current.PreProcessInput -= OnInput;
            timer.Stop();
        }
    }
}
