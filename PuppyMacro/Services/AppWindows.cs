using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>A window of another app: its visible bounds (physical pixels) and its process.</summary>
internal readonly record struct AppWindow(IntPtr Handle, NativeMethods.RECT Bounds, uint ProcessId);

/// <summary>The apps' windows on screen: which ones the user can see, and their .exe names.</summary>
internal static class AppWindows
{
    /// <summary>Window classes of the shell (taskbar, desktop): never an app window.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
    };

    private static readonly uint OwnProcess = (uint)Environment.ProcessId;

    /// <summary>File name of a process, e.g. "Diablo IV.exe"; "" when it cannot be read.</summary>
    public static string ExeName(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return "";
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size)
                ? Path.GetFileName(new string(buffer, 0, (int)size))
                : "";
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// A top-level window the user can see and could work in, not PuppyMacro's: shown, not minimized, not cloaked
    /// (suspended store apps, other virtual desktops), not a tool or no-activate window, not the taskbar or desktop.
    /// </summary>
    public static bool IsAppWindow(IntPtr w)
    {
        if (!NativeMethods.IsWindowVisible(w) || NativeMethods.IsIconic(w))
            return false;
        NativeMethods.GetWindowThreadProcessId(w, out uint process);
        if (process == OwnProcess)
            return false;
        long exStyle = NativeMethods.GetWindowLongPtr(w, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
            return false;
        if (NativeMethods.DwmGetWindowAttribute(w, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        var className = new StringBuilder(64);
        NativeMethods.GetClassName(w, className, className.Capacity);
        return !ShellClasses.Contains(className.ToString());
    }

    /// <summary>The first app window from the top of the z-order, or <see cref="IntPtr.Zero"/>.</summary>
    public static IntPtr TopAppWindow()
    {
        for (IntPtr w = NativeMethods.GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
        {
            if (IsAppWindow(w))
                return w;
        }
        return IntPtr.Zero;
    }

    /// <summary>The topmost app window at a screen point (physical pixels), skipping PuppyMacro's own windows.</summary>
    public static AppWindow? At(int x, int y)
    {
        for (IntPtr w = NativeMethods.GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
        {
            if (!IsAppWindow(w) || !TryGetBounds(w, out var bounds))
                continue;
            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                continue;
            NativeMethods.GetWindowThreadProcessId(w, out uint pid);
            return new AppWindow(w, bounds, pid);
        }
        return null;
    }

    /// <summary>The window as drawn: without the invisible resize borders that <c>GetWindowRect</c> includes.</summary>
    private static bool TryGetBounds(IntPtr w, out NativeMethods.RECT bounds) =>
        NativeMethods.DwmGetWindowAttribute(w, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out bounds,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
        || NativeMethods.GetWindowRect(w, out bounds);
}
