using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The AI page and what AI apps ask through the MCP server (<see cref="AiServer"/>, <see cref="AiTools"/>): this window
/// is their <see cref="IAiHost"/>. Every request opens a PuppyMacro window the user answers in.
/// </summary>
public partial class MainWindow : IAiHost
{
    private const int RecentCount = 4;

    private AiActivityLog _aiLog = null!;
    private AiTools _aiTools = null!;
    private AiServer _aiServer = null!;
    private DispatcherTimer _aiRefreshTimer = null!;

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

    private void RefreshAiPage()
    {
        if (_aiServer == null)
            return;
        List<AiClient> clients = _aiServer.Clients;
        string exe = AppPaths.ExecutablePath;
        bool access = _settings.AllowAiAccess;

        AiClient? Live(string name) => clients.FirstOrDefault(c => c.Name == name);
        string Since(AiClient client) => $"Connected since {client.Since:HH:mm}";

        var tiles = new List<AiAppTile>();

        // Claude Desktop
        string claudeConfig = AiApps.ClaudeConfigPath(AppDataFolder, LocalAppDataFolder);
        bool claudeInstalled = AiApps.ClaudeInstalled(AppDataFolder, LocalAppDataFolder);
        bool claudeAdded = AiApps.ClaudeHas(claudeConfig, exe);
        AiClient? claude = Live("Claude Desktop");
        tiles.Add(new AiAppTile
        {
            Id = "claude",
            Name = "Claude Desktop",
            Icon = AiAppTile.ExeIcon(Path.Combine(LocalAppDataFolder, "AnthropicClaude", "claude.exe")),
            Status = claude != null ? Since(claude)
                : claudeAdded ? "Added. Restart Claude Desktop to connect."
                : claudeInstalled ? "Installed, not connected" : "Not installed",
            Connected = claude != null,
            Waiting = claude == null && claudeAdded,
            ActionText = claudeAdded ? "Remove" : claudeInstalled ? "Connect" : null,
            ActionIsAccent = !claudeAdded,
            ActionEnabled = access,
            LinkText = claudeInstalled ? null : "Get Claude Desktop",
            LinkUri = claudeInstalled ? null : new Uri("https://claude.ai/download"),
        });

        // Visual Studio Code
        string? codeExe = AiApps.VsCodeExe(LocalAppDataFolder, ProgramFilesFolder);
        bool codeAdded = AiApps.VsCodeHas(AiApps.VsCodeConfigPath(AppDataFolder), exe);
        AiClient? code = Live("Visual Studio Code");
        tiles.Add(new AiAppTile
        {
            Id = "vscode",
            Name = "Visual Studio Code",
            Icon = AiAppTile.ExeIcon(codeExe),
            Status = code != null ? Since(code)
                : codeAdded ? "Added. Open Copilot Chat in Agent mode to connect."
                : codeExe != null ? "Installed. Uses GitHub Copilot, with the model you choose there." : "Not installed",
            Connected = code != null,
            Waiting = code == null && codeAdded,
            ActionText = codeAdded ? "Remove" : codeExe != null ? "Connect" : null,
            ActionIsAccent = !codeAdded,
            ActionEnabled = access,
            LinkText = codeExe != null ? null : "Get Visual Studio Code",
            LinkUri = codeExe != null ? null : new Uri("https://code.visualstudio.com/"),
        });

        // Codex
        string codexConfig = AiApps.CodexConfigPath(UserProfileFolder);
        bool codexInstalled = AiApps.CodexInstalled(UserProfileFolder);
        bool codexAdded = AiApps.CodexHas(codexConfig, exe);
        AiClient? codex = Live("Codex");
        tiles.Add(new AiAppTile
        {
            Id = "codex",
            Name = "Codex",
            Status = codex != null ? Since(codex)
                : codexAdded ? "Added. Restart Codex to connect."
                : codexInstalled ? "Installed, not connected" : "Not installed",
            Connected = codex != null,
            Waiting = codex == null && codexAdded,
            ActionText = codexAdded ? "Remove" : codexInstalled ? "Connect" : null,
            ActionIsAccent = !codexAdded,
            ActionEnabled = access,
            LinkText = codexInstalled ? null : "Get Codex",
            LinkUri = codexInstalled ? null : new Uri("https://chatgpt.com/codex"),
        });

        // Any other app: the configuration to paste
        var others = clients.Where(c => c.Name is not ("Claude Desktop" or "Visual Studio Code" or "Codex")).ToList();
        tiles.Add(new AiAppTile
        {
            Id = "other",
            Name = "Other apps",
            Status = others.Count > 0
                ? $"Connected: {string.Join(", ", others.Select(c => c.Name).Distinct())}"
                : "Any app that supports local MCP servers: paste the configuration in its MCP settings.",
            Connected = others.Count > 0,
            ActionText = "Copy configuration",
            ActionEnabled = access,
        });

        // Replaced only when something changed: a new list would take the keyboard focus and the hover off the buttons.
        string tilesShown = string.Join("|", tiles.Select(t => $"{t.Id}:{t.Status}:{t.ActionText}:{t.ActionEnabled}:{t.LinkText}"));
        if (tilesShown != _aiTilesShown)
        {
            _aiTilesShown = tilesShown;
            AiAppsList.ItemsSource = tiles;
        }

        List<AiActivityRow> recent = _aiLog.Entries().Take(RecentCount).Select(e => new AiActivityRow(e)).ToList();
        string recentShown = string.Join("|", recent.Select(r => $"{r.Entry.Time:O}:{r.Request}:{r.ResultText}"));
        if (recentShown != _aiRecentShown)
        {
            _aiRecentShown = recentShown;
            AiRecentList.ItemsSource = recent;
        }
        AiRecentList.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiNoActivityText.Visibility = recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? _aiTilesShown;
    private string? _aiRecentShown;

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
        if (error != null || done != null)
        {
            AiMessage.Severity = error != null ? iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Error
                : iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success;
            AiMessage.Message = error ?? done;
            AiMessage.IsOpen = true;
        }
        RefreshAiPage();
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
        var window = new AiActivityWindow(_aiLog) { Owner = this };
        ShowUserDialog(window.ShowDialog);
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

    Task<AiEdited<LoopDefinition>> IAiHost.EditLoop(LoopDefinition proposal, bool isNew, string app)
    {
        LoopItemViewModel? item = isNew ? null : _items.FirstOrDefault(i => i.Definition.Id == proposal.Id);
        if (!isNew && item == null)
            return Task.FromResult(new AiEdited<LoopDefinition>(AiAnswer.Cancelled, null));
        var editor = new LoopEditorWindow(_engine, _settings, _macros, _sounds, item?.Definition, PlaceFromEditor, proposal);
        editor.ShowProposal(app);
        AiAnswer answer = ShowForAi(editor);
        if (answer != AiAnswer.Done || editor.Result == null)
            return Task.FromResult(new AiEdited<LoopDefinition>(answer, null));
        if (item == null)
            AddLoop(editor.Result);
        else
            ApplyLoopEdit(item, editor.Result);
        return Task.FromResult(new AiEdited<LoopDefinition>(AiAnswer.Done, editor.Result.Clone()));
    }

    Task<AiEdited<MacroDefinition>> IAiHost.EditMacro(MacroDefinition proposal, bool isNew, string app)
    {
        MacroItemViewModel? item = isNew ? null : _macroItems.FirstOrDefault(i => i.Definition.Id == proposal.Id);
        if (!isNew && item == null)
            return Task.FromResult(new AiEdited<MacroDefinition>(AiAnswer.Cancelled, null));
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, item?.Definition, PlaceFromEditor, proposal: proposal);
        editor.ShowProposal(app);
        AiAnswer answer = ShowForAi(editor);
        Save(); // the editor keeps its size in the settings
        if (answer != AiAnswer.Done || editor.Result == null)
            return Task.FromResult(new AiEdited<MacroDefinition>(answer, null));
        if (item == null)
            AddMacro(editor.Result);
        else
            ApplyMacroEdit(item, editor.Result);
        return Task.FromResult(new AiEdited<MacroDefinition>(AiAnswer.Done, editor.Result.Clone()));
    }

    Task<AiEdited<RemapDefinition>> IAiHost.EditRemap(RemapDefinition proposal, bool isNew, string app)
    {
        RemapItemViewModel? item = isNew ? null : _remapItems.FirstOrDefault(i => i.Definition.Id == proposal.Id);
        if (!isNew && item == null)
            return Task.FromResult(new AiEdited<RemapDefinition>(AiAnswer.Cancelled, null));
        var editor = new RemapEditorWindow(_engine, _settings, _macros, item?.Definition, proposal);
        editor.ShowProposal(app);
        AiAnswer answer = ShowForAi(editor);
        if (answer != AiAnswer.Done || editor.Result == null)
            return Task.FromResult(new AiEdited<RemapDefinition>(answer, null));
        if (item == null)
            AddRemap(editor.Result);
        else
            ApplyRemapEdit(item, editor.Result);
        return Task.FromResult(new AiEdited<RemapDefinition>(AiAnswer.Done, editor.Result.Clone()));
    }

    Task<AiAnswer> IAiHost.Confirm(string app, string title, IReadOnlyList<string> lines, string primary)
    {
        AiRequestWindow window = AiRequestWindow.Confirmation(app, title, lines, primary);
        ShowForAi(window);
        return Task.FromResult(window.Answer);
    }

    void IAiHost.Delete(Guid id)
    {
        if (_items.FirstOrDefault(i => i.Definition.Id == id) is { } loop)
            RemoveLoop(loop);
        else if (_macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macro)
            RemoveMacro(macro);
        else if (_remapItems.FirstOrDefault(i => i.Definition.Id == id) is { } remap)
            RemoveRemap(remap);
    }

    /// <summary>As the card's switch: a disabled loop or macro stops, and it is saved.</summary>
    void IAiHost.SetEnabled(Guid id, bool enabled)
    {
        if (_items.FirstOrDefault(i => i.Definition.Id == id) is { } loop)
            loop.IsLoopEnabled = enabled;
        else if (_macroItems.FirstOrDefault(i => i.Definition.Id == id) is { } macro)
            macro.IsLoopEnabled = enabled;
        else if (_remapItems.FirstOrDefault(i => i.Definition.Id == id) is { } remap)
            remap.IsLoopEnabled = enabled;
    }

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

    Task<(AiAnswer Answer, int X, int Y)> IAiHost.PickPoint(string app, string reason)
    {
        var picker = new PickPointWindow(askedBy: app, reason: reason);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, picker.ResultX, picker.ResultY));
    }

    Task<(AiAnswer Answer, string? Exe)> IAiHost.PickApp(string app, string reason)
    {
        var picker = new PickPointWindow(pickApp: true, askedBy: app, reason: reason);
        AiAnswer answer = ShowForAi(picker);
        return Task.FromResult((answer, answer == AiAnswer.Done ? picker.ResultApp : null));
    }

    Task<(AiAnswer Answer, HotkeyBinding? Keys)> IAiHost.CaptureKeys(string app, string reason)
    {
        AiRequestWindow window = AiRequestWindow.KeyRequest(app, reason, _engine);
        ShowForAi(window);
        return Task.FromResult((window.Answer, window.Keys));
    }

    void IAiHost.StopAll() => _engine.StopAll();

    IReadOnlyList<string> IAiHost.RunningApps() => AppWindows.RunningApps();

    /// <summary>
    /// Shows a window for an AI request as the editors open (hotkeys off, this window hidden meanwhile), in front of the
    /// other apps, centered on this window or, in the tray, on the screen. Closes it when nobody used PuppyMacro (any key,
    /// click or move in any of its windows, also a dialog the editor opened) for <see cref="AiTools.AnswerTimeout"/>.
    /// </summary>
    private AiAnswer ShowForAi(Window dialog)
    {
        if (IsVisible)
            dialog.Owner = this;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (dialog is PickPointWindow)
            dialog.Loaded += (_, _) => dialog.Activate(); // already topmost, and must stay so: it covers the screen
        else
            AiProposal.BringToFront(dialog);

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
            if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyEventArgs && timer.IsEnabled)
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
