using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace PuppyMacro.Views;

/// <summary>
/// The items of a list page (Loops, Macros, Remap) as its views show them, with the page's search and filter: grouped by
/// the app they work in for List and Grid (<see cref="Grouped"/>: All apps first, then the apps by name, each in the
/// user's order), and in the user's order for Table (<see cref="Table"/>, the app is a column there). The groups show
/// only when the page has items for more than one app.
/// </summary>
public sealed class PageViews
{
    /// <summary>The property of every item the groups are made of: the app it works in, or All apps.</summary>
    public const string GroupProperty = "GroupName";

    private readonly IList _items;
    private readonly Func<object, string?> _app;

    /// <param name="items">The page's items, in the user's order.</param>
    /// <param name="app">An item's app (.exe name), null for all apps.</param>
    public PageViews(IList items, Predicate<object> filter, Func<object, string?> app)
    {
        _items = items;
        _app = app;
        Grouped = new ListCollectionView(items) { Filter = filter };
        Table = new ListCollectionView(items) { Filter = filter };
        UpdateGroups();
    }

    public ListCollectionView Grouped { get; }
    public ListCollectionView Table { get; }

    /// <summary>The items the search and the filter let through.</summary>
    public int Count => Table.Count;

    /// <summary>Whether List and Grid show a title per app.</summary>
    public bool IsGrouped => Grouped.GroupDescriptions.Count > 0;

    /// <summary>After a search, a filter or a change of the items: both views again, and the groups when the apps changed.</summary>
    public void Refresh()
    {
        UpdateGroups();
        Grouped.Refresh();
        Table.Refresh();
    }

    private void UpdateGroups()
    {
        bool needed = NeedsGroups(_items.Cast<object>().Select(_app));
        if (needed == IsGrouped)
            return;
        using (Grouped.DeferRefresh())
        {
            if (needed)
            {
                Grouped.GroupDescriptions.Add(new PropertyGroupDescription(GroupProperty) { StringComparison = StringComparison.OrdinalIgnoreCase });
                Grouped.CustomSort = new AppOrder(_items, _app);
            }
            else
            {
                Grouped.GroupDescriptions.Clear();
                Grouped.CustomSort = null;
            }
        }
    }

    /// <summary>Groups help only when the items work in more than one place (all apps counts as one).</summary>
    public static bool NeedsGroups(IEnumerable<string?> apps) =>
        apps.Select(app => app?.ToLowerInvariant()).Distinct().Skip(1).Any();

    /// <summary>
    /// The order of the grouped view: the items for all apps first, then each app by name (as Windows compares names, without
    /// case), and inside each the user's order.
    /// </summary>
    public sealed class AppOrder : IComparer
    {
        private readonly IList _items;
        private readonly Func<object, string?> _app;

        public AppOrder(IList items, Func<object, string?> app)
        {
            _items = items;
            _app = app;
        }

        public int Compare(object? x, object? y)
        {
            if (x == null || y == null)
                return x == null ? (y == null ? 0 : -1) : 1;
            string? a = _app(x), b = _app(y);
            if ((a == null) != (b == null))
                return a == null ? -1 : 1;
            int byApp = a == null ? 0 : string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            return byApp != 0 ? byApp : _items.IndexOf(x).CompareTo(_items.IndexOf(y));
        }
    }
}
