using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PuppyMacro.Resources;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>One version in the History list.</summary>
internal sealed class HistoryRow
{
    public HistoryRow(HistoryVersion version, bool current)
    {
        Version = version;
        Current = current;
        TimeText = version.Time.ToString("t", CultureInfo.CurrentCulture);
        DayText = DayName(version.Time);
        By = ByName(version.By);
        SummaryText = current ? string.Format(CultureInfo.CurrentCulture, Strings.History_CurrentVersion, version.ShownSummary)
            : version.ShownSummary;
    }

    /// <summary>"Today", "Yesterday" or the date, as the History and the AI activity group by day.</summary>
    public static string DayName(DateTime time) =>
        time.Date == DateTime.Today ? Strings.Day_Today
        : time.Date == DateTime.Today.AddDays(-1) ? Strings.Day_Yesterday
        : time.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>Who saved a version: "You" (kept in English in the file) in the user's language, an AI app's name, or "—".</summary>
    public static string ByName(string by) =>
        by == ItemHistory.You ? Strings.History_You : by.Length > 0 ? by : "—";

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
        DetailTitle.Text = $"{row.Version.Time.ToString("g", CultureInfo.CurrentCulture)} · {row.By}";
        List<string> changes = row.Version.ShownChanges;
        ChangeList.ItemsSource = changes.Count > 0 ? changes : new List<string> { row.Version.ShownSummary };
        RestoreButton.IsEnabled = !row.Current;
        RestoreHint.Text = row.Current ? Strings.History_ThisIsCurrent : Strings.History_RestoreHint;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is HistoryRow { Current: false } row)
            RestoreRequested?.Invoke(row.Version);
    }
}
