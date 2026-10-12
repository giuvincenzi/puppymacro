using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PuppyMacro.Views;

/// <summary>Export's content: the user's loops, macros and remaps to choose from, with Search and the kind.</summary>
public partial class ExportItemsView : UserControl
{
    private readonly List<ExportItemRow> _rows;
    private readonly ListCollectionView _view;

    /// <param name="items">The loops, macros and remaps with their Ids, in the lists' order.</param>
    /// <param name="chosen">The Ids checked at first.</param>
    internal ExportItemsView(IEnumerable<(object Item, Guid Id)> items, ICollection<Guid> chosen)
    {
        InitializeComponent();
        _rows = items.Select(i => new ExportItemRow(i.Item, i.Id)).ToList();
        foreach (ExportItemRow row in _rows)
        {
            row.SetChecked(chosen.Contains(row.Id));
            row.CheckedChanged += _ => Refresh();
        }
        _view = new ListCollectionView(_rows) { Filter = row => Shown((ExportItemRow)row) };
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemFileRow.Group)));
        ExportItemList.ItemsSource = _view;
        Refresh();
    }

    /// <summary>The boxes changed (the dialog's button names how many items).</summary>
    public event Action? ChoiceChanged;

    /// <summary>The checked items, in the lists' order (also the ones Search hides now).</summary>
    internal List<object> ChosenItems => _rows.Where(r => r.IsChecked).Select(r => r.Item).ToList();

    public int ChosenCount => _rows.Count(r => r.IsChecked);

    private bool Shown(ExportItemRow row)
    {
        string group = ExportKindBox.SelectedIndex switch
        {
            1 => "Loops",
            2 => "Macros",
            3 => "Remaps",
            _ => "",
        };
        string search = ExportSearchBox.Text.Trim();
        return (group.Length == 0 || row.Group == group)
               && (search.Length == 0 || row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_view == null)
            return; // while InitializeComponent sets the first kind
        _view.Refresh();
        Refresh();
    }

    private List<ExportItemRow> ShownRows() => _view.Cast<ExportItemRow>().ToList();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        List<ExportItemRow> shown = ShownRows();
        bool check = !shown.All(r => r.IsChecked);
        foreach (ExportItemRow row in shown)
            row.SetChecked(check);
        Refresh();
    }

    private void Refresh()
    {
        List<ExportItemRow> shown = ShownRows();
        int shownChecked = shown.Count(r => r.IsChecked);
        ExportSelectAllBox.IsEnabled = shown.Count > 0;
        ExportSelectAllBox.IsChecked = shownChecked == 0 ? false : shownChecked == shown.Count ? true : null;
        ExportCountText.Text = $"{shown.Count} shown · {ChosenCount} selected";
        ExportEmptyText.Text = _rows.Count == 0 ? "You have no loops, macros or remaps yet." : "Nothing matches the search.";
        ExportEmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChoiceChanged?.Invoke();
    }
}
