using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PuppyMacro.Native;

namespace PuppyMacro.Views;

/// <summary>
/// Keeps a window inside the work area (the screen without the taskbar) of the monitor it opens
/// on: shorter or narrower when the screen is too small, and moved back inside when it would
/// reach past an edge. Windows that fit are not changed. Editors scroll their content, so their
/// buttons stay visible when they get shorter.
/// </summary>
internal static class WindowFit
{
    /// <summary>Call from the window's constructor.</summary>
    public static void Apply(Window window)
    {
        // Before the first layout, so a window that sizes to its content is measured with the limit.
        window.SourceInitialized += (_, _) => FitSize(window);
        window.Loaded += (_, _) =>
        {
            FitSize(window);
            MoveInside(window);
        };
    }

    private static void FitSize(Window window)
    {
        if (WorkArea(window) is not Rect area)
            return;

        if (window.MinHeight > area.Height)
            window.MinHeight = area.Height;
        if (window.MaxHeight > area.Height)
            window.MaxHeight = area.Height;
        if (!double.IsNaN(window.Height) && window.Height > area.Height)
            window.Height = area.Height;

        if (window.MinWidth > area.Width)
            window.MinWidth = area.Width;
        if (window.MaxWidth > area.Width)
            window.MaxWidth = area.Width;
        if (!double.IsNaN(window.Width) && window.Width > area.Width)
            window.Width = area.Width;
    }

    private static void MoveInside(Window window)
    {
        if (WorkArea(window) is not Rect area)
            return;

        double left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
        double top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
        if (left != window.Left)
            window.Left = left;
        if (top != window.Top)
            window.Top = top;
    }

    /// <summary>
    /// Work area of the monitor the window is on, in WPF units. Before the window is shown, a dialog
    /// uses its owner's monitor (it opens centered on the owner).
    /// </summary>
    private static Rect? WorkArea(Window window)
    {
        Window placedBy = !window.IsLoaded && window.Owner != null ? window.Owner : window;
        IntPtr hwnd = new WindowInteropHelper(placedBy).Handle;
        if (hwnd == IntPtr.Zero)
            hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return null;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return null;

        // Monitor coordinates are physical pixels; WPF positions and sizes are in device-independent units.
        if (PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
            return null;
        Matrix toDip = target.TransformFromDevice;
        Point topLeft = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        Point bottomRight = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
