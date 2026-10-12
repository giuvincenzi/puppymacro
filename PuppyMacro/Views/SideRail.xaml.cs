using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        // The names are measured once laid out: put the selected page's icon in the middle at once.
        Loaded += (_, _) =>
        {
            foreach (ListViewItem item in AllItems())
                Slide(item, item.IsSelected, animate: false);
        };
    }

    private System.Collections.Generic.IEnumerable<ListViewItem> AllItems() =>
        Pages.Items.Cast<ListViewItem>().Concat(Footer.Items.Cast<ListViewItem>());

    /// <summary>
    /// Selected, a page's icon slides down by half its name's height and margin, so it ends in the middle of the item while the
    /// name fades out (ui.md); back to its place when another page is selected. Measured, as a long name is shrunk to fit.
    /// 167 ms with the fast-out slow-in curve, as WinUI's ControlFastAnimationDuration and ControlFastOutSlowInKeySpline.
    /// </summary>
    private static void Slide(ListViewItem item, bool selected, bool animate = true)
    {
        Grid? icon = Descendants(item).OfType<Grid>().FirstOrDefault(g => g.Tag as string == "Icon");
        Viewbox? name = Descendants(item).OfType<Viewbox>().FirstOrDefault();
        if (icon?.RenderTransform is not TranslateTransform move || name == null)
            return;
        double to = selected ? (name.ActualHeight + name.Margin.Top + name.Margin.Bottom) / 2 : 0;
        if (!animate)
        {
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.Y = to;
            return;
        }
        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(167)), new KeySpline(0, 0, 0, 1)));
        move.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject element)
                continue;
            yield return element;
            foreach (DependencyObject below in Descendants(element))
                yield return below;
        }
    }

    /// <summary>The selected page: "Loops", "Macros", "Remap" or "Settings".</summary>
    public string SelectedPage { get; private set; } = "Loops";

    /// <summary>Another page was selected.</summary>
    public event Action<string>? PageChanged;

    /// <summary>Selects a page by its name ("AI"), as a click on it does.</summary>
    public void Select(string page)
    {
        if (AllItems().FirstOrDefault(i => (string)i.Tag == page) is { } item)
            item.IsSelected = true;
    }

    /// <summary>The dot on Settings while an update is available.</summary>
    public void ShowUpdateDot(bool show) => UpdateDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (ListViewItem item in e.AddedItems.OfType<ListViewItem>())
            Slide(item, selected: true);
        foreach (ListViewItem item in e.RemovedItems.OfType<ListViewItem>())
            Slide(item, selected: false);
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
