using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>PuppyMacro's icon in the system tray (Shell_NotifyIcon), with click and menu callbacks.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = NativeMethods.WM_APP + 1;
    private const int WM_RBUTTONUP = 0x0205;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly string _tooltip;
    private bool _added;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        // Message-only window that receives the icon's mouse messages.
        _window = new HwndSource(new HwndSourceParameters("PuppyMacroTray") { ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        var small = new IntPtr[1];
        NativeMethods.ExtractIconEx(AppPaths.ExecutablePath, 0, null, small, 1);
        _icon = small[0];
        Add();
    }

    /// <summary>Double click on the icon.</summary>
    public event Action? Open;

    /// <summary>Right click on the icon (show the menu).</summary>
    public event Action? MenuRequested;

    public IntPtr Handle => _window.Handle;

    /// <summary>Shows a Windows notification next to the icon.</summary>
    public void Notify(string title, string text)
    {
        var data = NewData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NativeMethods.NIIF_INFO;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private NativeMethods.NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var data = NewData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse == NativeMethods.WM_LBUTTONDBLCLK)
                Open?.Invoke();
            else if (mouse == WM_RBUTTONUP)
                MenuRequested?.Invoke();
            handled = true;
        }
        else if (msg == (int)_taskbarCreated)
        {
            Add(); // Explorer restarted: the icon must be added again
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
            NativeMethods.DestroyIcon(_icon);
        _window.Dispose();
    }
}
