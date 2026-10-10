using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using PuppyMacro.Native;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// One floating button in overlay mode: borderless, always on top, click-through and never
/// activated, like the overlay panel. Clicks are handled by the input hook.
/// </summary>
public partial class FloatingButtonWindow
{
    private readonly IListItem _item;

    internal FloatingButtonWindow(IListItem item, Point position, int opacity)
    {
        InitializeComponent();
        _item = item;
        Title = $"PuppyMacro floating button: {item.Name}";
        ButtonView.Show(LabelOf(item), item.HasHotkey ? item.HotkeyText : "", item.FloatingButton.Size);
        ButtonView.SetBackgroundOpacity(opacity);
        ButtonView.IsRunning = item.IsRunning;
        ButtonView.IsItemEnabled = item.IsItemEnabled;

        // The position is the circle's top-left; the window starts at the halo.
        Left = position.X - FloatingButtonView.Inset;
        Top = position.Y - FloatingButtonView.Inset;

        if (item is INotifyPropertyChanged notify)
            notify.PropertyChanged += OnItemChanged;
        Closed += (_, _) =>
        {
            if (item is INotifyPropertyChanged n)
                n.PropertyChanged -= OnItemChanged;
        };
        SourceInitialized += OnSourceInitialized;
    }

    internal Guid ItemId => _item.Id;

    /// <summary>The label set by the user, or one made from the name.</summary>
    internal static string LabelOf(IListItem item) =>
        string.IsNullOrEmpty(item.FloatingButton.Label)
            ? Models.FloatingButton.DefaultLabel(item.Name)
            : item.FloatingButton.Label;

    /// <summary>The circle's rectangle in physical screen pixels, or null when not on screen.</summary>
    /// <summary>The loop or macro it starts and stops.</summary>
    internal IListItem Item => _item;

    internal PanelTarget? GetClickTarget()
    {
        FrameworkElement circle = ButtonView.ClickArea;
        if (!IsVisible || !circle.IsVisible)
            return null;
        Point topLeft = circle.PointToScreen(new Point(0, 0));
        Point bottomRight = circle.PointToScreen(new Point(circle.ActualWidth, circle.ActualHeight));
        return new PanelTarget((int)topLeft.X, (int)topLeft.Y, (int)bottomRight.X, (int)bottomRight.Y, _item.Id,
            Startable: _item.IsItemEnabled, PassThrough: _item.OverlayClickPassesThrough);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IListItem.IsRunning) or "" or null)
            ButtonView.IsRunning = _item.IsRunning;
        if (e.PropertyName is nameof(IListItem.IsItemEnabled) or "" or null)
            ButtonView.IsItemEnabled = _item.IsItemEnabled;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Click-through + never activated + hidden from Alt+Tab.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }
}
