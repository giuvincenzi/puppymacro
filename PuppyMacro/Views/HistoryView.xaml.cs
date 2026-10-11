using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>One version in the History list.</summary>
internal sealed class HistoryRow
{
    public HistoryRow(HistoryVersion version, bool current)
    {
        Version = version;
        Current = current;
        TimeText = version.Time.ToString("HH:mm");
        DayText = version.Time.Date == DateTime.Today ? "Today"
            : version.Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : version.Time.ToString("D");
        By = version.By.Length > 0 ? version.By : "—";
        SummaryText = current ? $"{version.Summary} (current)" : version.Summary;
    }

    public HistoryVersion Version { get; }
    public bool Current { get; }
    public string TimeText { get; }
    public string DayText { get; }
    public string By { get; }
    public string SummaryText { get; }
}

/// <summary>
/// An editor's History view: the item's saved versions (<see cref="ItemHistory"/>), newest first, by day, and the chosen
/// one's changes. Restore asks the editor to show that version in its fields (<see cref="RestoreRequested"/>).
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    /// <summary>The user chose to restore a version (its JSON).</summary>
    public event Action<HistoryVersion>? RestoreRequested;

    /// <summary>Shows the versions, newest first; the newest is the current one.</summary>
    internal void Load(IReadOnlyList<HistoryVersion> versions)
    {
        List<HistoryRow> rows = versions.Where(v => !v.Deleted).Select((v, i) => new HistoryRow(v, i == 0)).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.DayText)));
        VersionList.ItemsSource = view;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        VersionList.SelectedItem = rows.FirstOrDefault();
        ShowDetail();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    private void ShowDetail()
    {
        if (VersionList.SelectedItem is not HistoryRow row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }
        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = $"{row.Version.Time:g} · {row.By}";
        ChangeList.ItemsSource = row.Version.Changes.Count > 0 ? row.Version.Changes : new List<string> { row.Version.Summary };
        RestoreButton.IsEnabled = !row.Current;
        RestoreHint.Text = row.Current
            ? "This is the current version."
            : "Restore puts this version in the editor: select Save to keep it. The current version stays in the History.";
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is HistoryRow { Current: false } row)
            RestoreRequested?.Invoke(row.Version);
    }
}
