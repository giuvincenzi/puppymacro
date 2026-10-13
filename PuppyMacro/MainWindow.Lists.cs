using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The views of the Loops, Macros and Remap pages (ui.md): List, Table or Grid, chosen with the buttons next to the filter
/// and kept per page in the settings; a click on an item opens its editor; the width of the tiles and of the table's columns.
/// </summary>
public partial class MainWindow
{
    /// <summary>The narrowest tile; a page's tiles grow together when a hotkey needs more.</summary>
    private const double MinTileWidth = 260;
    /// <summary>Works in: two lines of "Toutes les applications" (the longest All apps) fit.</summary>
    private const double WorksInWidth = 120;
    /// <summary>The narrowest Name and What it does columns.</summary>
    private const double MinNameWidth = 160, MinSummaryWidth = 100;
    /// <summary>The space of a table cell around its content (the library's cell padding and the column's gripper).</summary>
    private const double CellPadding = 16;

    private sealed record ListPage(
        Func<ItemsLayout> Get, Action<ItemsLayout> Set, PageViews Views, ItemsControl List, ItemsControl Tiles, ListView Table,
        FrameworkElement Scroll, ToggleButton ListButton, ToggleButton TableButton, ToggleButton GridButton, bool IsRemaps);

    private ListPage[]? _listPages;
    /// <summary>The view buttons are being set by <see cref="ApplyLayout"/>, not by the user.</summary>
    private bool _applyingLayout;
    private double _actionsWidth = double.NaN, _remapActionsWidth = double.NaN;

    private ListPage[] ListPages => _listPages ??=
    [
        new(() => _settings.LoopsLayout, layout => _settings.LoopsLayout = layout, _view, LoopList, LoopTiles, LoopTable,
            LoopsScroll, LoopsListButton, LoopsTableButton, LoopsGridButton, false),
        new(() => _settings.MacrosLayout, layout => _settings.MacrosLayout = layout, _macroView, MacroList, MacroTiles, MacroTable,
            MacrosScroll, MacrosListButton, MacrosTableButton, MacrosGridButton, false),
        new(() => _settings.RemapsLayout, layout => _settings.RemapsLayout = layout, _remapView, RemapList, RemapTiles, RemapTable,
            RemapsScroll, RemapsListButton, RemapsTableButton, RemapsGridButton, true),
    ];

    private void InitializeLists()
    {
        foreach (var page in ListPages)
            ApplyLayout(page);
    }

    /// <summary>
    /// Shows the page's chosen view. The other two get no items, so screen readers and UI Automation see each item once.
    /// </summary>
    private void ApplyLayout(ListPage page)
    {
        ItemsLayout layout = page.Get();
        page.List.ItemsSource = layout == ItemsLayout.List ? page.Views.Grouped : null;
        page.Tiles.ItemsSource = layout == ItemsLayout.Grid ? page.Views.Grouped : null;
        page.Table.ItemsSource = layout == ItemsLayout.Table ? page.Views.Table : null;
        page.List.Visibility = layout == ItemsLayout.List ? Visibility.Visible : Visibility.Collapsed;
        page.Tiles.Visibility = layout == ItemsLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        page.Scroll.Visibility = layout == ItemsLayout.Table ? Visibility.Collapsed : Visibility.Visible;
        page.Table.Visibility = layout == ItemsLayout.Table ? Visibility.Visible : Visibility.Collapsed;
        _applyingLayout = true;
        try
        {
            page.ListButton.IsChecked = layout == ItemsLayout.List;
            page.TableButton.IsChecked = layout == ItemsLayout.Table;
            page.GridButton.IsChecked = layout == ItemsLayout.Grid;
        }
        finally
        {
            _applyingLayout = false;
        }
        if (layout == ItemsLayout.Grid)
            FitTiles(page);
        else if (layout == ItemsLayout.Table)
            FitColumns(page);
    }

    /// <summary>After the items changed (added, edited, deleted, filtered): the widths that depend on their keys.</summary>
    private void RefitLists()
    {
        foreach (var page in ListPages)
        {
            if (page.Get() == ItemsLayout.Grid)
                FitTiles(page);
            else if (page.Get() == ItemsLayout.Table)
                FitColumns(page);
        }
    }

    /// <summary>
    /// List, Table or Grid checked, by a click or by UI Automation's Toggle (which raises no Click): that page's view.
    /// </summary>
    private void OnLayoutChecked(object sender, RoutedEventArgs e)
    {
        if (_applyingLayout)
            return;
        foreach (var page in ListPages)
        {
            ItemsLayout? layout = ReferenceEquals(sender, page.ListButton) ? ItemsLayout.List
                : ReferenceEquals(sender, page.TableButton) ? ItemsLayout.Table
                : ReferenceEquals(sender, page.GridButton) ? ItemsLayout.Grid
                : null;
            if (layout is not ItemsLayout chosen)
                continue;
            bool changed = page.Get() != chosen;
            page.Set(chosen);
            ApplyLayout(page);
            if (changed)
                Save();
            return;
        }
    }

    /// <summary>The view in use stays checked: clicking it again keeps it, as the choices of a segmented switch.</summary>
    private void OnLayoutUnchecked(object sender, RoutedEventArgs e)
    {
        if (!_applyingLayout && sender is ToggleButton button)
            button.IsChecked = true;
    }

    /// <summary>A click on a card or a tile (not on its buttons): its editor.</summary>
    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is { } item)
            OpenItem(item);
    }

    /// <summary>A click on a table row, not on its buttons or switch: its editor.</summary>
    private void OnTableRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem row || IsInsideInteractiveControl(e.OriginalSource as DependencyObject, row))
            return;
        e.Handled = true;
        OpenItem(row.Content);
    }

    /// <summary>Enter on a table row: its editor, as a click.</summary>
    private void OnTableRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListViewItem row || !ReferenceEquals(e.OriginalSource, row))
            return;
        e.Handled = true;
        OpenItem(row.Content);
    }

    /// <summary>Opens the item's editor; loops and macros only while nothing runs (as Edit did).</summary>
    private void OpenItem(object item)
    {
        switch (item)
        {
            case LoopItemViewModel loop when !_engine.AnyRunning:
                EditLoop(loop);
                break;
            case MacroItemViewModel macro when !_engine.AnyRunning:
                EditMacro(macro);
                break;
            case RemapItemViewModel remap:
                EditRemap(remap);
                break;
        }
    }

    private void OnItemPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MacroItemViewModel)
            OnMacroStartStopClick(sender, e);
        else
            OnStartStopClick(sender, e);
    }

    private void OnItemMoreClick(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            case LoopItemViewModel:
                OnMoreClick(sender, e);
                break;
            case MacroItemViewModel:
                OnMacroMoreClick(sender, e);
                break;
            case RemapItemViewModel:
                OnRemapMoreClick(sender, e);
                break;
        }
    }

    /// <summary>Drag to move: an item goes only among the items of its own kind and, in a grouped view, its own group.</summary>
    private bool CanDropOn(IListItem dragged, IListItem target)
    {
        if (ReferenceEquals(dragged, target) || dragged.GetType() != target.GetType())
            return false;
        PageViews views = dragged is MacroItemViewModel ? _macroView : _view;
        return !views.IsGrouped || string.Equals(dragged.AppExe, target.AppExe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>In a list before or after by height, in the grid (tiles side by side) by width.</summary>
    private static bool DropsBefore(FrameworkElement card, DragEventArgs e) =>
        card is iNKORE.UI.WPF.Modern.Controls.SettingsCard { ContentAlignment: iNKORE.UI.WPF.Modern.Controls.ContentAlignment.Vertical }
            ? e.GetPosition(card).X < card.ActualWidth / 2
            : e.GetPosition(card).Y < card.ActualHeight / 2;

    private void OnTilesSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && ListPages.FirstOrDefault(page => ReferenceEquals(page.Tiles, sender)) is { } page)
            FitTiles(page);
    }

    private void OnTableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && ListPages.FirstOrDefault(page => ReferenceEquals(page.Table, sender)) is { } page)
            FitColumns(page);
    }

    /// <summary>
    /// Every tile of a page as wide: as many columns as fit tiles of at least <see cref="MinTileWidth"/>, or of the width
    /// the page's longest hotkey needs, which then grows them all. The width includes the 8 px gap after each tile.
    /// </summary>
    private void FitTiles(ListPage page)
    {
        double available = page.Tiles.ActualWidth;
        if (available <= 0)
            return;
        double keys = page.Views.Grouped.Cast<object>().Select(KeysWidth).DefaultIfEmpty(0).Max();
        // The card's padding (16 + 16), its icon (20) and the icon's margin (2 + 20), the gap before the buttons (8), the
        // card's border (2) and the gap after the tile (8): the library's SettingsCard values.
        double needed = keys + ActionsWidth(page.IsRemaps) + 92;
        double tile = Math.Max(MinTileWidth, needed);
        int columns = Math.Max(1, (int)Math.Floor(available / tile));
        page.Tiles.Tag = Math.Floor(Math.Max(tile, available / columns));
    }

    /// <summary>
    /// The table's columns: the icon, the keys, Works in, State and the buttons as wide as their content (State as its
    /// longest word in this language, so it never changes with the items), Name and What it does the rest, 3 to 2.
    /// A remap's Note takes the whole rest.
    /// </summary>
    private void FitColumns(ListPage page)
    {
        if (page.Table.View is not GridView grid || page.Table.ActualWidth <= 0)
            return;
        var columns = grid.Columns;
        var items = page.Views.Table.Cast<object>().ToList();
        double state = new[] { Strings.Card_StatusRunning, Strings.Card_StatusIdle, Strings.Card_StatusDisabled, Strings.Card_StatusEnabled }
            .Select(CaptionWidth).Max() + 14; // the dot and its gap
        double header(int index) => columns[index].Header is string text ? CaptionWidth(text) + 6 : 0;
        // The vertical scroll bar's place, so the last column never goes under it.
        double available = page.Table.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;

        double fixedWidth = columns[0].Width;
        void Set(int index, double content)
        {
            columns[index].Width = Math.Ceiling(Math.Max(content, header(index)) + CellPadding);
            fixedWidth += columns[index].Width;
        }
        if (page.IsRemaps)
        {
            Set(2, items.Cast<RemapItemViewModel>().Select(SourceWidth).DefaultIfEmpty(0).Max());
            Set(3, items.Cast<RemapItemViewModel>().Select(remap => KeyCapsWidth(remap.TargetParts)).DefaultIfEmpty(0).Max());
            Set(4, WorksInWidth);
            Set(5, state);
            Set(6, ActionsWidth(true));
            columns[1].Width = Math.Max(MinNameWidth, Math.Floor(available - fixedWidth));
        }
        else
        {
            Set(3, items.Select(KeysWidth).DefaultIfEmpty(0).Max() + (items.OfType<IListItem>().Any(item => item.IsHoldMode && item.HasHotkey)
                ? CaptionWidth(Strings.Card_Hold) + 2 : 0));
            Set(4, WorksInWidth);
            Set(5, state);
            Set(6, ActionsWidth(false));
            double rest = Math.Max(MinNameWidth + MinSummaryWidth, available - fixedWidth);
            columns[1].Width = Math.Floor(Math.Max(MinNameWidth, rest * 3 / 5));
            columns[2].Width = Math.Floor(Math.Max(MinSummaryWidth, rest - columns[1].Width));
        }
    }

    /// <summary>How wide an item's keys are in the list and the grid: its hotkey, or "No hotkey"; a remap's from → to.</summary>
    private double KeysWidth(object item) => item switch
    {
        RemapItemViewModel remap => SourceWidth(remap) + CaptionWidth("→") + 8 + KeyCapsWidth(remap.TargetParts),
        LoopItemViewModel loop => loop.HasHotkey ? KeyCapsWidth(loop.HotkeyParts) : CaptionWidth(Strings.Card_NoHotkey),
        MacroItemViewModel macro => macro.HasHotkey ? KeyCapsWidth(macro.HotkeyParts) : CaptionWidth(Strings.Card_NoHotkey),
        _ => 0,
    };

    private double SourceWidth(RemapItemViewModel remap) =>
        remap.HasSource ? KeyCapsWidth(remap.SourceParts) : CaptionWidth(Strings.Remaps_NoHotkey) + 18; // the warning icon

    /// <summary>Key caps as the lists draw them (App.xaml's KeyCapItem), measured.</summary>
    private static double KeyCapsWidth(IEnumerable<string> keys)
    {
        var template = (DataTemplate)Application.Current.FindResource("KeyCapItem");
        double width = 0;
        foreach (string key in keys)
        {
            if (template.LoadContent() is not FrameworkElement cap)
                continue;
            cap.DataContext = key;
            cap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += cap.DesiredSize.Width;
        }
        return width;
    }

    /// <summary>A caption text's width, measured with the library's caption style.</summary>
    private static double CaptionWidth(string text)
    {
        var block = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("CaptionTextBlockStyle") };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    /// <summary>The buttons of an item (ItemCardActions), measured once: a remap's have no Play.</summary>
    private double ActionsWidth(bool remap)
    {
        ref double width = ref remap ? ref _remapActionsWidth : ref _actionsWidth;
        if (double.IsNaN(width))
        {
            var actions = new ItemCardActions { ShowPlay = !remap };
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = actions.DesiredSize.Width;
        }
        return width;
    }
}
