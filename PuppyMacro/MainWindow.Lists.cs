using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The views of the Loops, Macros and Remap pages (ui.md): List, Table or Grid, chosen with the buttons next to the filter
/// and kept per page in the settings; a click on an item opens its editor; the width of the tiles and of the table's columns.
/// </summary>
public partial class MainWindow
{
    /// <summary>The narrowest tile (the user's choice); a page's tiles grow together when a hotkey needs more.</summary>
    private const double MinTileWidth = 320;
    /// <summary>Works in: two lines of "Toutes les applications" (the longest All apps) fit.</summary>
    private const double WorksInWidth = 120;
    /// <summary>The narrowest Name (Note) and What it does columns; narrower than that, What it does, then Works in, go.</summary>
    private const double MinNameWidth = 140, MinSummaryWidth = 100;
    /// <summary>The space of a table cell around its content (the library's cell padding and the column's gripper).</summary>
    private const double CellPadding = 16;

    private sealed record ListPage(
        Func<ItemsLayout> Get, Action<ItemsLayout> Set, PageViews Views, ItemsControl List, ItemsControl Tiles, ListView Table,
        FrameworkElement TableCard, FrameworkElement Scroll, ToggleButton ListButton, ToggleButton TableButton, ToggleButton GridButton, bool IsRemaps);

    private ListPage[]? _listPages;
    /// <summary>The view buttons are being set by <see cref="ApplyLayout"/>, not by the user.</summary>
    private bool _applyingLayout;

    private ListPage[] ListPages => _listPages ??=
    [
        new(() => _settings.LoopsLayout, layout => _settings.LoopsLayout = layout, _view, LoopList, LoopTiles, LoopTable,
            LoopTableCard, LoopsScroll, LoopsListButton, LoopsTableButton, LoopsGridButton, false),
        new(() => _settings.MacrosLayout, layout => _settings.MacrosLayout = layout, _macroView, MacroList, MacroTiles, MacroTable,
            MacroTableCard, MacrosScroll, MacrosListButton, MacrosTableButton, MacrosGridButton, false),
        new(() => _settings.RemapsLayout, layout => _settings.RemapsLayout = layout, _remapView, RemapList, RemapTiles, RemapTable,
            RemapTableCard, RemapsScroll, RemapsListButton, RemapsTableButton, RemapsGridButton, true),
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
        page.TableCard.Visibility = layout == ItemsLayout.Table ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// A click on a card or a tile: its editor. Only the card's own click: the Click of its buttons (Play, More options)
    /// bubbles up to it, as the card is a button too.
    /// </summary>
    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        if ((sender as FrameworkElement)?.Tag is { } item)
            OpenItem(item);
    }

    /// <summary>The table row pressed (not on its buttons or switch), until the mouse is released on it.</summary>
    private ListViewItem? _pressedRow;

    /// <summary>
    /// A press on a table row, not on its buttons or switch: kept from the ListView, so the row is not selected (a click opens
    /// the editor; nothing is selected in these lists).
    /// </summary>
    private void OnTableRowPress(object sender, MouseButtonEventArgs e)
    {
        _pressedRow = null;
        if (sender is not ListViewItem row || IsInsideInteractiveControl(e.OriginalSource as DependencyObject, row))
            return;
        e.Handled = true;
        _pressedRow = row;
    }

    /// <summary>A click on a table row (pressed and released on it, not on its buttons or switch): its editor.</summary>
    private void OnTableRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem row || !ReferenceEquals(row, _pressedRow))
            return;
        _pressedRow = null;
        e.Handled = true;
        OpenItem(row.Content);
    }

    /// <summary>
    /// Nothing is selected in these lists (a click opens the editor): a row the ListView selects anyway (the focus coming back
    /// after the editor, the keyboard) is let go at once, so its selection mark never shows.
    /// </summary>
    private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView table && table.SelectedItem != null)
            table.SelectedItem = null;
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
        // The bottom row: the keys, 12, the state (its longest word in this language), then Play and More options.
        double keys = page.Views.Grouped.Cast<object>().Select(KeysWidth).DefaultIfEmpty(0).Max();
        double state = StatusWords.Select(CaptionWidth).Max() + 14; // the dot and its gap
        // The card's padding (16 + 16) and border (2), and the gap after the tile (8): the library's SettingsCard values.
        double needed = keys + 12 + state + 8 + ActionsWidth(page.IsRemaps, withSwitch: false) + 42;
        double tile = Math.Max(MinTileWidth, needed);
        int columns = Math.Max(1, (int)Math.Floor(available / tile));
        page.Tiles.Tag = Math.Floor(Math.Max(tile, available / columns));
    }

    /// <summary>
    /// The table's columns, in the room there is (the table never scrolls sideways). Loops and macros: the icon, Name, Hotkey
    /// (second, the user's choice), What it does, Works in, State, the buttons; remaps: the icon, Note, From, To, Works in,
    /// State, the buttons. Always shown: the icon, Name (Note), the keys, State and the buttons, the keys at least as wide as a
    /// long combination and wider when the page has a longer one, State as its longest word in this language. Then, while there is room, Works in and, the least important (the user's
    /// choice), What it does: a third of what is left, the first to go when the window narrows, then Works in.
    /// </summary>
    private void FitColumns(ListPage page)
    {
        if (page.Table.View is not GridView grid || page.Table.ActualWidth <= 0)
            return;
        var columns = grid.Columns;
        var items = page.Views.Table.Cast<object>().ToList();
        double width(int index, double content) =>
            Math.Ceiling(Math.Max(content, columns[index].Header is string text ? CaptionWidth(text) + 6 : 0) + CellPadding);
        // The vertical scroll bar's place, so nothing goes under it.
        double available = page.Table.ActualWidth - SystemParameters.VerticalScrollBarWidth;

        const int name = 1, worksIn = 4, stateColumn = 5, buttons = 6;
        double state = StatusWords.Select(CaptionWidth).Max() + 14; // the dot and its gap
        int? summary = page.IsRemaps ? null : 3;
        var always = page.IsRemaps
            ? new Dictionary<int, double>
            {
                [2] = width(2, Math.Max(UsualKeysWidth, items.Cast<RemapItemViewModel>().Select(SourceWidth).DefaultIfEmpty(0).Max())),
                [3] = width(3, Math.Max(UsualKeysWidth, items.Cast<RemapItemViewModel>().Select(remap => KeyCapsWidth(remap.TargetParts))
                    .DefaultIfEmpty(0).Max())),
                [stateColumn] = width(stateColumn, state),
                [buttons] = width(buttons, ActionsWidth(true)),
            }
            : new Dictionary<int, double>
            {
                [2] = width(2, Math.Max(UsualKeysWidth, items.Select(KeysWidth).DefaultIfEmpty(0).Max())
                    + (items.OfType<IListItem>().Any(item => item.IsHoldMode && item.HasHotkey) ? CaptionWidth(Strings.Card_Hold) + 2 : 0)),
                [stateColumn] = width(stateColumn, state),
                [buttons] = width(buttons, ActionsWidth(false)),
            };
        foreach (var (index, w) in always)
            columns[index].Width = w;
        double rest = available - columns[0].Width - always.Values.Sum();

        double works = width(worksIn, WorksInWidth);
        bool showWorksIn = rest - works >= MinNameWidth;
        columns[worksIn].Width = showWorksIn ? works : 0;
        if (showWorksIn)
            rest -= works;

        if (summary is int column)
        {
            bool showSummary = rest - MinNameWidth >= MinSummaryWidth && rest / 3 >= MinSummaryWidth;
            columns[column].Width = showSummary ? Math.Floor(rest / 3) : 0;
            rest -= columns[column].Width;
        }
        columns[name].Width = Math.Max(MinNameWidth, Math.Floor(rest));
    }

    /// <summary>
    /// A long combination, Ctrl + Alt + Shift + Win + F12, as key caps with this language's key names: the least width of the
    /// keys' columns (the hotkey matters most in the table: the user's choice), so they never jump with the items.
    /// </summary>
    private double UsualKeysWidth =>
        KeyCapsWidth(KeyNames.Parts(new HotkeyBinding { Vk = 0x7B, Ctrl = true, Alt = true, Shift = true, Win = true }));

    /// <summary>The table's card clips its rows at its rounded corners, so the hover of the first and last rows follows them.</summary>
    private void OnTableCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border card)
            card.Clip = new RectangleGeometry(new Rect(card.RenderSize), card.CornerRadius.TopLeft, card.CornerRadius.TopLeft);
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

    /// <summary>
    /// Keys as the lists draw them (KeyCaps, Small), measured with KeyCaps' own sizes and the window's font: never a key cap
    /// made and measured out of the window, whose text comes through a binding that is not there yet.
    /// </summary>
    private double KeyCapsWidth(IEnumerable<string> keys) =>
        KeyCaps.MeasureWidth(keys, KeyCapSize.Small, TextElement.GetFontFamily(this),
            (double)Application.Current.FindResource("ControlContentThemeFontSize"), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A caption text's width, measured with the library's caption style and this window's font.</summary>
    private double CaptionWidth(string text)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = TextElement.GetFontFamily(this), Style = (Style)Application.Current.FindResource("CaptionTextBlockStyle"),
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    /// <summary>Every state a list shows.</summary>
    private static string[] StatusWords =>
        [Strings.Card_StatusRunning, Strings.Card_StatusIdle, Strings.Card_StatusDisabled, Strings.Card_StatusEnabled];

    /// <summary>
    /// The buttons of an item (ItemCardActions), measured: a remap's have no Play, a tile's switch is at its top instead.
    /// </summary>
    private static double ActionsWidth(bool remap, bool withSwitch = true)
    {
        var actions = new ItemCardActions { ShowPlay = !remap, ShowSwitch = withSwitch };
        actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return actions.DesiredSize.Width;
    }
}
