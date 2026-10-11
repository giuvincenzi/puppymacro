using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using iNKORE.UI.WPF.Modern.Controls;
using PuppyMacro.Services.Ai;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>The AI page's View all: every request of the last 30 days, filtered by period, app, type, result and text.</summary>
public partial class AiActivityWindow
{
    private const string AllApps = "All apps";
    private const string AllTypes = "All types";
    private const string AllResults = "All results";

    private readonly AiActivityLog _log;
    private readonly Func<AiActivity, string?> _restore;
    private bool _ready;

    /// <param name="restore">Puts back what an AI app's change replaced (MainWindow): null when done, otherwise why not.</param>
    internal AiActivityWindow(AiActivityLog log, Func<AiActivity, string?> restore)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _log = log;
        _restore = restore;

        PeriodBox.SelectedIndex = 1; // Last 7 days
        KindBox.ItemsSource = new[] { AllTypes }.Concat(Enum.GetValues<AiActivityKind>().Select(AiActivityRow.KindName)).ToList();
        KindBox.SelectedIndex = 0;
        ResultBox.ItemsSource = new[] { AllResults }
            .Concat(Enum.GetValues<AiActivityResult>().Select(AiActivityRow.ResultName).Where(n => n.Length > 0)).ToList();
        ResultBox.SelectedIndex = 0;

        _log.Changed += OnLogChanged;
        Closed += (_, _) => _log.Changed -= OnLogChanged;
        _ready = true;
        Refresh();
    }

    private void OnLogChanged() => Dispatcher.InvokeAsync(Refresh);

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (!_ready)
            return;
        List<AiActivity> entries = _log.Entries();

        // The apps that asked something, kept selected when the list changes.
        string? app = AppBox.SelectedItem as string;
        var apps = new[] { AllApps }.Concat(entries.Select(e => e.App).Distinct().OrderBy(a => a)).ToList();
        if (!apps.SequenceEqual(AppBox.ItemsSource as IEnumerable<string> ?? Array.Empty<string>()))
        {
            _ready = false;
            AppBox.ItemsSource = apps;
            AppBox.SelectedItem = app != null && apps.Contains(app) ? app : AllApps;
            _ready = true;
        }

        DateTime from = PeriodBox.SelectedIndex switch
        {
            0 => DateTime.Today,
            1 => DateTime.Today.AddDays(-6),
            _ => DateTime.MinValue,
        };
        string? appFilter = AppBox.SelectedItem as string is { } a && a != AllApps ? a : null;
        string? kind = KindBox.SelectedItem as string is { } k && k != AllTypes ? k : null;
        string? result = ResultBox.SelectedItem as string is { } r && r != AllResults ? r : null;
        string search = SearchBox.Text?.Trim() ?? "";

        List<AiActivityRow> rows = entries
            .Where(e => e.Time >= from)
            .Where(e => appFilter == null || e.App == appFilter)
            .Select(e => new AiActivityRow(e))
            .Where(row => kind == null || row.KindText == kind)
            .Where(row => result == null || AiActivityRow.ResultName(row.Entry.Result) == result)
            .Where(row => search.Length == 0
                || row.Request.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || row.App.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || row.ResultText.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AiActivityRow.DayText)));
        ActivityList.ItemsSource = view;
        EmptyText.Text = entries.Count == 0 ? "No requests yet." : "No requests match the filters.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = entries.Count > 0;
        ShowDetail();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    private void ShowDetail()
    {
        var row = ActivityList.SelectedItem as AiActivityRow;
        string detail = row?.DetailText ?? "";
        DetailText.Text = detail;
        RestoreButton.Visibility = row?.CanRestore == true ? Visibility.Visible : Visibility.Collapsed;
        DetailCard.Visibility = detail.Length > 0 || row?.CanRestore == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (ActivityList.SelectedItem is not AiActivityRow { CanRestore: true } row)
            return;
        if (_restore(row.Entry) is string error)
            System.Windows.MessageBox.Show(this, error, "AI activity", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Clear history",
            Content = "Delete the history of AI requests? This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync(this) == ContentDialogResult.Primary)
            await Task.Run(_log.Clear);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
