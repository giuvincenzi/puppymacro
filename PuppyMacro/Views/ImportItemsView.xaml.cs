using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>
/// Import's content: what a .puppyitems file holds, read against the user's data (<see cref="ItemFile.Read"/>). Each change
/// of the boxes reads it again with the chosen items, as names and hotkeys go to the first item that takes them.
/// </summary>
public partial class ImportItemsView : UserControl
{
    private Func<ICollection<int>?, ItemFileContent>? _read;
    private List<ImportItemRow> _rows = new();

    public ImportItemsView() => InitializeComponent();

    /// <summary>Choose another file… was clicked.</summary>
    public event Action? OtherFileRequested;

    /// <summary>The boxes changed (the dialog's button names how many items).</summary>
    public event Action? ChoiceChanged;

    /// <summary>The file can't be read: its error, shown instead of the list.</summary>
    public string? Error { get; private set; }

    /// <summary>The chosen items, ready to add, read with these choices.</summary>
    internal List<object> ChosenItems() => _read == null || Error != null
        ? new List<object>()
        : _read(Chosen()).SelectedItems.ToList();

    public int ChosenCount => _rows.Count(r => r.IsChecked);

    /// <summary>Shows a file: <paramref name="read"/> reads it with the items to add (null: every item that can be).</summary>
    internal void Show(string fileName, Func<ICollection<int>?, ItemFileContent> read)
    {
        _read = read;
        ItemFileContent content = read(null);
        FileText.Text = fileName;
        Error = content.Error;
        ErrorText.Text = content.Error ?? "";
        ErrorText.Visibility = content.Error == null ? Visibility.Collapsed : Visibility.Visible;
        ListPanel.Visibility = content.Error == null ? Visibility.Visible : Visibility.Collapsed;
        FromText.Text = content.Error != null ? "" : Counts(content);

        foreach (ImportItemRow old in _rows)
            old.CheckedChanged -= OnRowChanged;
        _rows = content.Entries.Select(e => new ImportItemRow(e)).ToList();
        foreach (ImportItemRow row in _rows)
            row.CheckedChanged += OnRowChanged;
        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemFileRow.Group)));
        ItemList.ItemsSource = view;
        RefreshSelectAll();
        ChoiceChanged?.Invoke();
    }

    /// <summary>"From PuppyMacro 1.17.0 · 2 loops, 1 macro".</summary>
    private static string Counts(ItemFileContent content)
    {
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        var parts = new List<string>();
        foreach ((string kind, string word) in new[] { ("loop", "loop"), ("macro", "macro"), ("remap", "remap") })
        {
            int n = content.Entries.Count(e => e.Kind == kind);
            if (n > 0)
                parts.Add(Count(n, word));
        }
        int other = content.Entries.Count(e => e.Kind is not ("loop" or "macro" or "remap"));
        if (other > 0)
            parts.Add(Count(other, "other item"));
        string items = parts.Count == 0 ? "No items" : string.Join(", ", parts);
        return content.App == null ? items : $"From PuppyMacro {content.App} · {items}";
    }

    private List<int> Chosen() => _rows.Where(r => r.IsChecked).Select(r => r.Index).ToList();

    private void OnRowChanged(ItemFileRow row) => Reread();

    /// <summary>Reads the file again with the chosen items: their notes and the rows that can't be imported any more.</summary>
    private void Reread()
    {
        if (_read == null)
            return;
        ItemFileContent content = _read(Chosen());
        foreach (ImportItemRow row in _rows)
            if (content.Entries.FirstOrDefault(e => e.Index == row.Index) is { } entry)
                row.Update(entry);
        RefreshSelectAll();
        ChoiceChanged?.Invoke();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // Checks every row unless all are checked already (an indeterminate box checks them all, as in File Explorer).
        bool check = !_rows.Where(r => r.CanImport).All(r => r.IsChecked);
        foreach (ImportItemRow row in _rows)
            row.SetChecked(check && row.CanImport);
        Reread();
    }

    private void RefreshSelectAll()
    {
        List<ImportItemRow> usable = _rows.Where(r => r.CanImport).ToList();
        SelectAllBox.IsEnabled = usable.Count > 0;
        int checkedCount = usable.Count(r => r.IsChecked);
        SelectAllBox.IsChecked = checkedCount == 0 ? false : checkedCount == usable.Count ? true : null;
    }

    private void OnOtherFileClick(object sender, RoutedEventArgs e) => OtherFileRequested?.Invoke();
}
