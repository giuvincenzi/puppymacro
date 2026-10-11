using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
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
        AiClient? Live(string name) => clients.FirstOrDefault(c => c.Name == name);
        static string Since(AiClient client) => $"Connected since {client.Since:HH:mm}";

        string claudeConfig = AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder);
        bool claudeInstalled = AiApps.ClaudeInstalled(AppDataFolder, LocalAppDataFolder);
        bool claudeAdded = AiApps.ClaudeHas(claudeConfig, exe);
        AiClient? claude = Live("Claude Desktop");

        string? codeExe = AiApps.VsCodeExe(LocalAppDataFolder, ProgramFilesFolder);
        bool codeAdded = AiApps.VsCodeHas(AiApps.VsCodeConfigPath(AppDataFolder), exe);
        AiClient? code = Live("Visual Studio Code");

        bool codexInstalled = AiApps.CodexInstalled(UserProfileFolder);
        bool codexAdded = AiApps.CodexHas(AiApps.CodexConfigPath(UserProfileFolder), exe);
        AiClient? codex = Live("Codex");

        var others = clients.Where(c => c.Name is not ("Claude Desktop" or "Visual Studio Code" or "Codex")).Select(c => c.Name).Distinct().ToList();
        return new List<AiAppTile>
        {
            new()
            {
                Id = "claude", Name = "Claude Desktop",
                Icon = ExeIcon(Path.Combine(LocalAppDataFolder, "AnthropicClaude", "claude.exe")),
                Status = claude != null ? Since(claude)
                    : claudeAdded ? "Added. Restart Claude Desktop to connect."
                    : claudeInstalled ? "Installed, not connected" : "Not installed",
                Connected = claude != null, Waiting = claude == null && claudeAdded,
                ActionText = claudeAdded ? "Remove" : claudeInstalled ? "Connect" : null,
                ActionIsAccent = !claudeAdded, ActionEnabled = access,
                LinkText = claudeInstalled ? null : "Get Claude Desktop",
                LinkUri = claudeInstalled ? null : new Uri("https://claude.ai/download"),
            },
            new()
            {
                Id = "vscode", Name = "Visual Studio Code",
                Icon = ExeIcon(codeExe),
                Status = code != null ? Since(code)
                    : codeAdded ? "Added. Open Copilot Chat in Agent mode to connect."
                    : codeExe != null ? "Installed. Uses GitHub Copilot, with the model you choose there." : "Not installed",
                Connected = code != null, Waiting = code == null && codeAdded,
                ActionText = codeAdded ? "Remove" : codeExe != null ? "Connect" : null,
                ActionIsAccent = !codeAdded, ActionEnabled = access,
                LinkText = codeExe != null ? null : "Get Visual Studio Code",
                LinkUri = codeExe != null ? null : new Uri("https://code.visualstudio.com/"),
            },
            new()
            {
                Id = "codex", Name = "Codex",
                Status = codex != null ? Since(codex)
                    : codexAdded ? "Added. Restart Codex to connect."
                    : codexInstalled ? "Installed, not connected" : "Not installed",
                Connected = codex != null, Waiting = codex == null && codexAdded,
                ActionText = codexAdded ? "Remove" : codexInstalled ? "Connect" : null,
                ActionIsAccent = !codexAdded, ActionEnabled = access,
                LinkText = codexInstalled ? null : "Get Codex",
                LinkUri = codexInstalled ? null : new Uri("https://chatgpt.com/codex"),
            },
            new()
            {
                Id = "other", Name = "Other apps",
                Status = others.Count > 0 ? $"Connected: {string.Join(", ", others)}"
                    : "Any app that supports local MCP servers: paste the configuration in its MCP settings.",
                Connected = others.Count > 0,
                ActionText = "Copy configuration", ActionEnabled = access,
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
            System.Windows.Automation.AutomationProperties.SetName(button, $"{tile.ActionText} {tile.Name}");
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
        switch (tile.Id, tile.ActionText)
        {
            case ("claude", "Connect"):
                error = AiApps.ClaudeAdd(AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder), exe);
                break;
            case ("claude", "Remove"):
                error = AiApps.ClaudeRemove(AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder));
                break;
            case ("vscode", "Connect"):
                error = Open(AiApps.VsCodeInstallUri(exe));
                break;
            case ("vscode", "Remove"):
                error = AiApps.VsCodeRemove(AiApps.VsCodeConfigPath(AppDataFolder));
                break;
            case ("codex", "Connect"):
                error = AiApps.CodexAdd(AiApps.CodexConfigPath(UserProfileFolder), exe);
                break;
            case ("codex", "Remove"):
                error = AiApps.CodexRemove(AiApps.CodexConfigPath(UserProfileFolder));
                break;
            case ("other", _):
                Clipboard.SetText(AiApps.GenericConfiguration(exe));
                done = "Configuration copied. Paste it in your AI app's MCP settings, then restart the app.";
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
            return "Visual Studio Code did not open: " + ex.Message;
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
                if (JsonSerializer.Deserialize<AiSettingsValues>(entry.Before, CodeJson.Options) is { } values)
                    ((IAiHost)this).ApplySettings(values);
            }
            else
            {
                object? item = entry.ItemKind switch
                {
                    "loop" => JsonSerializer.Deserialize<LoopDefinition>(entry.Before, CodeJson.Options),
                    "macro" => JsonSerializer.Deserialize<MacroDefinition>(entry.Before, CodeJson.Options),
                    "remap" => JsonSerializer.Deserialize<RemapDefinition>(entry.Before, CodeJson.Options),
                    _ => null,
                };
                if (item == null)
                    return "This change can't be restored.";
                SaveItem(item);
            }
        }
        catch (JsonException ex)
        {
            return "This change can't be restored: " + ex.Message;
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

    void IAiHost.Save(object item, string app) => SavingAs(app, () =>
    {
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
        if (values.StopAllHotkey is { IsSet: true } stopAll)
            _settings.StopAllHotkey = stopAll.Clone();
        if (values.OverlayModeHotkey is { IsSet: true } overlay)
            _settings.OverlayModeHotkey = overlay.Clone();
        if (values.RecordHotkey is { IsSet: true } record)
            _settings.RecordHotkey = record.Clone();
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
    async Task<AiAnswer> IAiHost.Ready(string app, string message, string context)
    {
        var window = new AiReadyWindow(app, message, context);
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

    Task<(AiAnswer Answer, List<(int X, int Y)> Points)> IAiHost.PickPoints(string app, string message, int count)
    {
        var picker = new PickPointWindow(askedBy: app, reason: message, count: count);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, answer == AiAnswer.Done ? picker.Points.ToList() : new List<(int X, int Y)>()));
    }

    Task<(AiAnswer Answer, string? Exe)> IAiHost.PickApp(string app, string message)
    {
        var picker = new PickPointWindow(pickApp: true, askedBy: app, reason: message);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, answer == AiAnswer.Done ? picker.ResultApp : null));
    }

    Task<(AiAnswer Answer, HotkeyBinding? Keys)> IAiHost.CaptureKeys(string app, string message, string context)
    {
        AiRequestWindow window = AiRequestWindow.KeyRequest(app, message, context, _engine);
        ShowForAi(window);
        return Task.FromResult((window.Answer, window.Keys));
    }

    void IAiHost.StopAll() => _engine.StopAll();

    IReadOnlyList<string> IAiHost.RunningApps() => AppWindows.RunningApps();

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
    /// other apps, centered on this window or, in the tray, on the screen. Closes it when nobody used PuppyMacro (any key,
    /// click or move in any of its windows) for <see cref="AiTools.AnswerTimeout"/>.
    /// </summary>
    private AiAnswer ShowForAi(Window dialog)
    {
        if (IsVisible)
            dialog.Owner = this;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The picker is topmost and must stay so (it covers the screen), and activates itself.
        if (dialog is not PickPointWindow)
            AiWindow.BringToFront(dialog);

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
