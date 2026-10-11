using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Services;
using Velopack;
using InfoBar = iNKORE.UI.WPF.Modern.Controls.InfoBar;

namespace PuppyMacro;

// Updates: the bar at the top, the badge on Settings and the Updates card share one state.
public partial class MainWindow
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };
    private UpdateInfo? _availableUpdate;
    private string? _dismissedUpdateVersion;
    private CancellationTokenSource? _downloadCancel;
    private int _downloadPercent;
    private bool _checkingForUpdates;
    private DateTime? _lastUpdateCheck;
    private string? _updateError;

    private string? AvailableVersion => _availableUpdate?.TargetFullRelease.Version.ToString();

    private void InitializeUpdates()
    {
        CheckForUpdatesSwitch.IsOn = _settings.CheckForUpdates;
        CheckForUpdatesSwitch.IsEnabled = _updates.IsInstalled;
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        RefreshUpdateUi();

        if (_updates.IsInstalled && _settings.CheckForUpdates)
        {
            _updateTimer.Start();
            Dispatcher.InvokeAsync(CheckForUpdatesAsync, DispatcherPriority.ApplicationIdle);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_updates.IsInstalled || _checkingForUpdates || _downloadCancel != null)
            return;

        _checkingForUpdates = true;
        _updateError = null;
        RefreshUpdateUi();
        try
        {
            _availableUpdate = await _updates.CheckAsync();
            _lastUpdateCheck = DateTime.Now;
        }
        catch (Exception ex)
        {
            // Network, GitHub or release feed errors: shown in the card, never fatal.
            _updateError = $"Could not check for updates. {ex.Message}";
        }
        finally
        {
            _checkingForUpdates = false;
            RefreshUpdateUi();
        }
    }

    private async Task DownloadAndApplyUpdateAsync()
    {
        if (_availableUpdate is not { } update || _downloadCancel != null)
            return;

        // Loops and macros must not keep sending input while PuppyMacro restarts.
        _engine.StopAll();
        _updateError = null;
        _downloadPercent = 0;
        using var cancel = new CancellationTokenSource();
        _downloadCancel = cancel;
        RefreshUpdateUi();
        try
        {
            await _updates.DownloadAsync(update, percent => Dispatcher.InvokeAsync(() =>
            {
                _downloadPercent = percent;
                RefreshUpdateUi();
            }), cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _updateError = $"Could not download the update. {ex.Message}";
            return;
        }
        finally
        {
            _downloadCancel = null;
            RefreshUpdateUi();
        }

        _updates.ApplyAfterExit(update);
        ExitApp();
    }

    private void RefreshUpdateUi()
    {
        if (!_updates.IsInstalled)
        {
            UpdateBar.IsOpen = false;
            UpdateBar.Visibility = Visibility.Collapsed;
            ShowSettingsBadge(false);
            UpdateStatusText.Text = "Install PuppyMacro with Setup to get updates";
            UpdateWhatsNew.Visibility = Visibility.Collapsed;
            UpdateCardProgress.Visibility = Visibility.Collapsed;
            UpdateCardButton.Visibility = Visibility.Collapsed;
            return;
        }

        string? version = AvailableVersion;
        bool downloading = _downloadCancel != null;

        ShowSettingsBadge(version != null);
        UpdateBar.IsOpen = version != null && (downloading || version != _dismissedUpdateVersion);
        UpdateBar.Visibility = UpdateBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _updateError != null && !downloading ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");

        string status;
        string action;
        if (downloading)
        {
            status = $"Downloading version {version}… {_downloadPercent}%";
            action = "Cancel";
        }
        else if (_updateError != null)
        {
            status = _updateError;
            action = "Try again";
        }
        else if (version != null)
        {
            status = $"Version {version} is available (you have {_updates.CurrentVersion})";
            action = "Update";
        }
        else if (_checkingForUpdates)
        {
            status = "Checking for updates…";
            action = "Check now";
        }
        else
        {
            status = _lastUpdateCheck is DateTime checkedAt
                ? $"PuppyMacro {_updates.CurrentVersion} is up to date. Checked at {checkedAt:t}"
                : $"PuppyMacro {_updates.CurrentVersion}";
            action = "Check now";
        }

        UpdateStatusText.Text = status;
        UpdateCardButton.Visibility = Visibility.Visible;
        UpdateCardButton.Content = action;
        UpdateCardButton.IsEnabled = !_checkingForUpdates;
        UpdateCardProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardProgress.Value = _downloadPercent;
        UpdateWhatsNew.Visibility = version != null && !downloading ? Visibility.Visible : Visibility.Collapsed;
        if (version != null)
            UpdateWhatsNew.NavigateUri = new Uri(UpdateService.ReleaseUrl(version));

        UpdateBar.Title = "New version available";
        UpdateBar.Message = downloading
            ? $"Downloading… {_downloadPercent}%"
            : _updateError ?? $"Version {version}. Updating restarts the app.";
        UpdateBarProgress.Visibility = UpdateCardProgress.Visibility;
        UpdateBarProgress.Value = _downloadPercent;
        UpdateBarButton.Content = action;
        UpdateBar.IsClosable = !downloading; // Later
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCancel != null)
            _downloadCancel.Cancel();
        else if (_availableUpdate != null)
            await DownloadAndApplyUpdateAsync();
        else
            await CheckForUpdatesAsync();
    }

    /// <summary>The bar's close button (Later): hides the bar until PuppyMacro restarts or a newer version comes out; the badge stays.</summary>
    private void OnUpdateLaterClick(InfoBar sender, object args)
    {
        _dismissedUpdateVersion = AvailableVersion;
        RefreshUpdateUi();
    }

    /// <summary>The dot on Settings in the side rail while an update is available. It stays until the update is installed.</summary>
    private void ShowSettingsBadge(bool show) => Rail.ShowUpdateDot(show);

    /// <summary>About's User guide and GitHub cards: opens the card's Tag in the browser.</summary>
    private void OnOpenLinkClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnCheckForUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.CheckForUpdates = CheckForUpdatesSwitch.IsOn;
        Save();
        if (_settings.CheckForUpdates)
        {
            _updateTimer.Start();
            await CheckForUpdatesAsync();
        }
        else
        {
            _updateTimer.Stop();
        }
    }
}
