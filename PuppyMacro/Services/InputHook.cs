using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>
/// Global WH_KEYBOARD_LL and WH_MOUSE_LL hooks. Must be installed on a thread with a
/// message loop (the WPF UI thread). The handler returns true to block the event.
/// </summary>
internal sealed class InputHook : IDisposable
{
    /// <param name="physicalModifierEvent">
    /// False for events that must not change the physical modifier state: injected by another
    /// program, or the fake left Ctrl that Windows adds when AltGr is pressed.
    /// </param>
    public delegate bool KeyEventHandler(int vk, bool isDown, bool physicalModifierEvent);

    private const uint LLKHF_INJECTED = 0x10;
    private const uint FakeCtrlScanCodeFlag = 0x200; // AltGr's synthesized left Ctrl

    // Delegates are kept in fields so the GC never collects them while hooked.
    private readonly NativeMethods.LowLevelHookProc _keyboardProc;
    private readonly NativeMethods.LowLevelHookProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    public InputHook()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public KeyEventHandler? Handler { get; set; }

    public enum MouseKind { Move, ButtonDown, ButtonUp, Wheel, HWheel }

    /// <summary>Detailed mouse event (screen pixels). Return true to block it.</summary>
    public delegate bool MouseEventHandler(MouseKind kind, int vk, int x, int y, int wheelDelta);

    /// <summary>Optional: set only while something needs positions (recording, clickable panel).</summary>
    public MouseEventHandler? MouseDetail { get; set; }

    public void Install()
    {
        IntPtr module = NativeMethods.GetModuleHandle(null);

        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");

        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the mouse hook.");
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != InputSender.Signature)
                {
                    int message = (int)wParam;
                    bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                    bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                    bool physical = (data.flags & LLKHF_INJECTED) == 0 && (data.scanCode & FakeCtrlScanCodeFlag) == 0;
                    if ((isDown || isUp) && Handler?.Invoke((int)data.vkCode, isDown, physical) == true)
                        return new IntPtr(1);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Keyboard hook error: {ex}");
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int message = (int)wParam;
                var detail = MouseDetail;

                // Mouse moves are very frequent: skip all work unless someone needs them.
                if (message == NativeMethods.WM_MOUSEMOVE && detail == null)
                    return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo == InputSender.Signature)
                    return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                int vk = 0;
                bool isDown = false;
                InputHook.MouseKind? kind = null;
                int wheel = 0;

                switch (message)
                {
                    case NativeMethods.WM_MOUSEMOVE: kind = MouseKind.Move; break;
                    case NativeMethods.WM_LBUTTONDOWN: vk = KeyNames.VK_LBUTTON; isDown = true; break;
                    case NativeMethods.WM_LBUTTONUP: vk = KeyNames.VK_LBUTTON; break;
                    case NativeMethods.WM_RBUTTONDOWN: vk = KeyNames.VK_RBUTTON; isDown = true; break;
                    case NativeMethods.WM_RBUTTONUP: vk = KeyNames.VK_RBUTTON; break;
                    case NativeMethods.WM_MBUTTONDOWN: vk = KeyNames.VK_MBUTTON; isDown = true; break;
                    case NativeMethods.WM_MBUTTONUP: vk = KeyNames.VK_MBUTTON; break;
                    case NativeMethods.WM_XBUTTONDOWN:
                    case NativeMethods.WM_XBUTTONUP:
                        isDown = message == NativeMethods.WM_XBUTTONDOWN;
                        vk = (data.mouseData >> 16) == NativeMethods.XBUTTON1 ? KeyNames.VK_XBUTTON1 : KeyNames.VK_XBUTTON2;
                        break;
                    case NativeMethods.WM_MOUSEWHEEL:
                        kind = MouseKind.Wheel;
                        wheel = unchecked((short)(data.mouseData >> 16));
                        break;
                    case NativeMethods.WM_MOUSEHWHEEL:
                        kind = MouseKind.HWheel;
                        wheel = unchecked((short)(data.mouseData >> 16));
                        break;
                }
                if (vk != 0)
                    kind = isDown ? MouseKind.ButtonDown : MouseKind.ButtonUp;

                if (kind != null && detail != null && detail(kind.Value, vk, data.pt.X, data.pt.Y, wheel))
                    return new IntPtr(1);

                if (vk != 0 && Handler?.Invoke(vk, isDown, true) == true)
                    return new IntPtr(1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Mouse hook error: {ex}");
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
