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

/// <summary>Content of the game mode panel. Used by the game mode window and by the placement overlay.</summary>
public partial class GameModePanel
{
    private ListCollectionView? _loops;
    private ListCollectionView? _macros;

    public GameModePanel()
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

    private static bool IsEnabledItem(object item) => item is IListItem { IsItemEnabled: true };

    public void SetHotkeyLabels(string exitHotkey, string stopAllHotkey)
    {
        ExitHotkeyText.Text = exitHotkey;
        StopAllHotkeyText.Text = stopAllHotkey;
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

    /// <summary>Screen rectangles (physical pixels) of the clickable rows: enabled, not Hold.</summary>
    internal List<PanelTarget> GetClickTargets()
    {
        var targets = new List<PanelTarget>();
        if (!IsVisible)
            return targets;
        AddTargets(LoopList, targets);
        AddTargets(MacroList, targets);
        return targets;
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
        EmptyText.Visibility = loops + macros == 0 ? Visibility.Visible : Visibility.Collapsed;
        MacrosHeader.Visibility = macros > 0 && loops > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
