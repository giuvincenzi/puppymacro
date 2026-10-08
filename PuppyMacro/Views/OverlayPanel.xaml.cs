using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Controls;
using PuppyMacro.Services;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PuppyMacro.Views;

/// <summary>Content of the overlay panel. Used by the overlay mode window and by the placement overlay.</summary>
public partial class OverlayPanel
{
    private ListCollectionView? _loops;
    private ListCollectionView? _macros;

    public OverlayPanel()
    {
        InitializeComponent();
        TitleText.Text = App.DisplayTitle;
    }

    /// <summary>Shows the enabled loops and macros, in the same order as their lists.</summary>
    public void Bind(IList loops, IList macros)
    {
        _loops = new ListCollectionView(loops) { Filter = IsEnabledItem };
        _macros = new ListCollectionView(macros) { Filter = IsEnabledItem };
        LoopList.ItemsSource = _loops;
        MacroList.ItemsSource = _macros;
        if (loops is INotifyCollectionChanged l)
            l.CollectionChanged += OnItemsChanged;
        if (macros is INotifyCollectionChanged m)
            m.CollectionChanged += OnItemsChanged;
        UpdateEmptyState();
    }

    /// <summary>Enabled items, except those shown as floating buttons.</summary>
    private static bool IsEnabledItem(object item) =>
        item is IListItem { IsItemEnabled: true } listItem && !FloatingButtons.HasButton(listItem);

    public void SetHotkeyLabels(string exitHotkey, string stopAllHotkey)
    {
        StopAllHotkeyText.Text = stopAllHotkey;
        ExitButton.ToolTip = $"Exit overlay mode ({exitHotkey})";
    }

    private static readonly Brush StopRed = Frozen(0xFF, 0x99, 0xA4);
    private static readonly Brush StopRedBorder = Frozen(0xFF, 0x6B, 0x78);
    private static readonly Brush StopRedFill = Frozen(0x1F, 0xFF, 0x6B, 0x78);
    private static readonly Brush Neutral = Frozen(0xC5, 0xC5, 0xC5);
    private static readonly Brush NeutralSquare = Frozen(0x9E, 0x9E, 0x9E);
    private static readonly Brush NeutralBorder = Frozen(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Brush NeutralFill = Frozen(0x0F, 0xFF, 0xFF, 0xFF);

    private static Brush Frozen(byte r, byte g, byte b) => Frozen(0xFF, r, g, b);

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Stop all is red while something runs and the panel can be clicked; the exit button is dimmed
    /// when the panel cannot be clicked (Click items in the panel to start or stop them is off).
    /// </summary>
    public void SetState(bool anyRunning, bool clickable)
    {
        bool red = anyRunning && clickable;
        StopAllButton.BorderBrush = red ? StopRedBorder : NeutralBorder;
        StopAllButton.Background = red ? StopRedFill : NeutralFill;
        StopAllLabel.Foreground = red ? StopRed : Neutral;
        StopAllSquare.Fill = red ? StopRed : NeutralSquare;
        ExitButton.Opacity = clickable ? 1 : 0.5;
    }

    /// <summary>Sets the panel background opacity (percent). Text is not affected.</summary>
    public void SetBackgroundOpacity(int percent)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);
        PanelBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x20, 0x20, 0x20));
    }

    /// <summary>Call when an item is enabled, disabled, added, edited or moved.</summary>
    public void RefreshList()
    {
        _loops?.Refresh();
        _macros?.Refresh();
        UpdateEmptyState();
    }

    /// <summary>Screen rectangles (physical pixels) of the clickable rows (enabled, not Hold), the exit button and Stop all.</summary>
    internal List<PanelTarget> GetClickTargets()
    {
        var targets = new List<PanelTarget>();
        if (!IsVisible)
            return targets;
        AddTargets(LoopList, targets);
        AddTargets(MacroList, targets);
        AddTarget(ExitButton, PanelTarget.ExitOverlayId, targets);
        AddTarget(StopAllButton, PanelTarget.StopAllId, targets);
        return targets;
    }

    private static void AddTarget(FrameworkElement element, Guid id, List<PanelTarget> targets)
    {
        if (!element.IsVisible)
            return;
        Point topLeft = element.PointToScreen(new Point(0, 0));
        Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        targets.Add(new PanelTarget((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y, id));
    }

    private static void AddTargets(ItemsControl list, List<PanelTarget> targets)
    {
        foreach (var item in list.Items)
        {
            if (item is not IListItem { IsItemEnabled: true, IsHoldMode: false } listItem)
                continue;
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement row || !row.IsVisible)
                continue;
            Point topLeft = row.PointToScreen(new Point(0, 0));
            Point bottomRight = row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight));
            targets.Add(new PanelTarget((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y, listItem.Id));
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        int loops = _loops?.Count ?? 0;
        int macros = _macros?.Count ?? 0;
        bool anyButton = HasAnyButton(_loops) || HasAnyButton(_macros);
        EmptyText.Text = anyButton ? "Everything is on floating buttons." : "No enabled loops.";
        EmptyText.Visibility = loops + macros == 0 ? Visibility.Visible : Visibility.Collapsed;
        MacrosHeader.Visibility = macros > 0 && loops > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool HasAnyButton(ListCollectionView? view)
    {
        if (view?.SourceCollection is not IEnumerable items)
            return false;
        foreach (object item in items)
        {
            if (item is IListItem listItem && FloatingButtons.HasButton(listItem))
                return true;
        }
        return false;
    }
}
