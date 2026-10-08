using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using PuppyMacro.Services;
using Velopack;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace PuppyMacro;

// Updates: the bar at the top, the dot on Settings and the Updates card share one state.
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
        CheckForUpdatesSwitch.IsChecked = _settings.CheckForUpdates;
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
            UpdateBar.Visibility = Visibility.Collapsed;
            SettingsUpdateDot.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "Updates are available when PuppyMacro is installed with Setup.";
            UpdateWhatsNew.Visibility = Visibility.Collapsed;
            UpdateCardProgress.Visibility = Visibility.Collapsed;
            UpdateCardButton.Visibility = Visibility.Collapsed;
            return;
        }

        string? version = AvailableVersion;
        bool downloading = _downloadCancel != null;

        SettingsUpdateDot.Visibility = version != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBar.Visibility = version != null && (downloading || version != _dismissedUpdateVersion)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCard.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
            version != null ? "AccentTextFillColorPrimaryBrush" : "CardStrokeColorDefaultBrush");
        UpdateCardIcon.SetResourceReference(ForegroundProperty,
            version != null ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        UpdateStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _updateError != null && !downloading ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");

        string status;
        string action;
        bool primary = false;
        if (downloading)
        {
            status = $"Downloading PuppyMacro {version}… {_downloadPercent}%";
            action = "Cancel";
        }
        else if (_updateError != null)
        {
            status = _updateError;
            action = "Try again";
        }
        else if (version != null)
        {
            status = $"PuppyMacro {version} is available. Installed: {_updates.CurrentVersion}";
            action = "Update";
            primary = true;
        }
        else if (_checkingForUpdates)
        {
            status = "Checking for updates…";
            action = "Check now";
        }
        else
        {
            status = _lastUpdateCheck is DateTime checkedAt
                ? $"PuppyMacro {_updates.CurrentVersion} is up to date. Last checked at {checkedAt:t}"
                : $"PuppyMacro {_updates.CurrentVersion}";
            action = "Check now";
        }

        var appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary;

        UpdateStatusText.Text = status;
        UpdateCardButton.Visibility = Visibility.Visible;
        UpdateCardButton.Content = action;
        UpdateCardButton.Appearance = appearance;
        UpdateCardButton.IsEnabled = !_checkingForUpdates;
        UpdateCardProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateCardProgress.Value = _downloadPercent;
        UpdateWhatsNew.Visibility = version != null && !downloading ? Visibility.Visible : Visibility.Collapsed;
        if (version != null)
            UpdateWhatsNewLink.NavigateUri = new Uri(UpdateService.ReleaseUrl(version));

        UpdateBarTitle.Text = $"PuppyMacro {version} is available";
        UpdateBarText.Text = downloading
            ? $"Downloading… {_downloadPercent}%"
            : _updateError ?? "Loops and macros stop, then PuppyMacro restarts with the new version.";
        UpdateBarProgress.Visibility = UpdateCardProgress.Visibility;
        UpdateBarProgress.Value = _downloadPercent;
        UpdateBarButton.Content = action;
        UpdateBarButton.Appearance = appearance;
        UpdateLaterButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>Hides the bar until PuppyMacro restarts or a newer version comes out; the dot stays.</summary>
    private void OnUpdateLaterClick(object sender, RoutedEventArgs e)
    {
        _dismissedUpdateVersion = AvailableVersion;
        RefreshUpdateUi();
    }

    private void OnWhatsNewNavigate(object sender, RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnCheckForUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _settings.CheckForUpdates = CheckForUpdatesSwitch.IsChecked == true;
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
