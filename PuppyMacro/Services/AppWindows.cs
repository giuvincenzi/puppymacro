using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    /// <summary>The .exe names of the apps that have a window open, PuppyMacro left out, sorted.</summary>
    public static List<string> RunningApps() => Process.GetProcesses()
        .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
        .Select(p => p.ProcessName + ".exe")
        .Where(n => !n.Equals("PuppyMacro.exe", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>File name of a process, e.g. "Diablo IV.exe"; "" when it cannot be read.</summary>
    public static string ExeName(uint pid) => ExePath(pid) is string path ? Path.GetFileName(path) : "";

    /// <summary>Full path of a process's exe; null when it cannot be read (gone, or another user's).</summary>
    public static string? ExePath(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static readonly int? OwnIntegrity = IntegrityLevel(NativeMethods.GetCurrentProcess());

    /// <summary>
    /// The process runs at a higher integrity level than PuppyMacro (an app run as administrator while PuppyMacro is
    /// not): while its window is in front, Windows gives PuppyMacro's hooks none of the keys. False when either level
    /// cannot be read.
    /// </summary>
    public static bool IsAboveOwnIntegrity(uint pid)
    {
        if (OwnIntegrity is not int own)
            return false;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return false;
        try
        {
            return IntegrityLevel(process) > own;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>The integrity level (the last sub-authority of the token's label, 0x2000 medium, 0x3000 high); null when it cannot be read.</summary>
    private static int? IntegrityLevel(IntPtr process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out IntPtr token))
            return null;
        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0)
                return null;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, size, out _))
                    return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                return count == 0 ? null : Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
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
