using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// The main window's side rail: Loops, Macros and Remap on top, Settings at the bottom, always closed.
/// Two standard ListViews that share one selection: selecting a page in one clears the other.
/// </summary>
public partial class SideRail : UserControl
{
    private bool _switching;

    public SideRail()
    {
        InitializeComponent();
        LoopsItem.IsSelected = true;
    }

    /// <summary>The selected page: "Loops", "Macros", "Remap" or "Settings".</summary>
    public string SelectedPage { get; private set; } = "Loops";

    /// <summary>Another page was selected.</summary>
    public event Action<string>? PageChanged;

    /// <summary>Selects a page by its name ("AI"), as a click on it does.</summary>
    public void Select(string page)
    {
        if (Pages.Items.Cast<ListViewItem>().Concat(Footer.Items.Cast<ListViewItem>()).FirstOrDefault(i => (string)i.Tag == page) is { } item)
            item.IsSelected = true;
    }

    /// <summary>The dot on Settings while an update is available.</summary>
    public void ShowUpdateDot(bool show) => UpdateDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || sender is not ListView list)
            return;
        if (list.SelectedItem is not ListViewItem { Tag: string page })
        {
            // A click cannot leave the rail without a page (Ctrl+click would clear it): select it again.
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ListViewItem previous && list.SelectedItem == null
                && (list == Pages ? Footer : Pages).SelectedItem == null)
                previous.IsSelected = true;
            return;
        }
        _switching = true;
        (list == Pages ? Footer : Pages).SelectedItem = null;
        _switching = false;
        SelectedPage = page;
        PageChanged?.Invoke(page);
    }
}
