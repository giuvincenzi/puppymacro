using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>Sends key and mouse button events with SendInput.</summary>
internal static class InputSender
{
    /// <summary>
    /// Tag stored in dwExtraInfo of every event we inject, so the input hook
    /// can ignore our own events (a loop can never trigger a hotkey).
    /// </summary>
    public static readonly IntPtr Signature = new(0x50555050);

    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();

    /// <summary>Moves the cursor to a screen position (physical pixels), as real mouse input.</summary>
    public static void MoveTo(int x, int y)
    {
        int left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int width = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        int height = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        input.U.mi = new NativeMethods.MOUSEINPUT
        {
            // Absolute coordinates are normalized to 0..65535 over the whole virtual desktop.
            dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
            dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
            dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            dwExtraInfo = Signature,
        };
        NativeMethods.SendInput(1, new[] { input }, InputSize);
    }

    /// <summary>One wheel notch (120) in the given direction.</summary>
    public static void Wheel(ScrollDirection direction)
    {
        bool horizontal = direction is ScrollDirection.Left or ScrollDirection.Right;
        int delta = direction is ScrollDirection.Up or ScrollDirection.Right ? 120 : -120;
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        input.U.mi = new NativeMethods.MOUSEINPUT
        {
            mouseData = unchecked((uint)delta),
            dwFlags = horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL,
            dwExtraInfo = Signature,
        };
        NativeMethods.SendInput(1, new[] { input }, InputSize);
    }

    /// <summary>Unassigned key used to stop Alt/Win from opening menus (same idea as AutoHotkey's menu mask key).</summary>
    private const int MaskKeyVk = 0xE8;

    /// <summary>Serializes injected input, so loops running in parallel never mix modifier states.</summary>
    private static readonly object Gate = new();

    public static void Down(int vk) => Send(vk, isDown: true);

    public static void Up(int vk) => Send(vk, isDown: false);

    /// <summary>
    /// Presses and releases <paramref name="vk"/>, holding it for <paramref name="holdMs"/>.
    /// Held Ctrl/Alt/Shift/Win are released first so the target sees the plain key.
    /// They are never pressed again afterwards: re-pressing could leave a modifier stuck
    /// if the user lets go of it at the same moment.
    /// Returns false if <paramref name="stop"/> was signaled.
    /// </summary>
    public static bool Tap(int vk, double holdMs, PreciseTimer timer, WaitHandle stop)
    {
        lock (Gate)
        {
            ReleaseHeldModifiers();
            Down(vk);
            bool keepGoing = timer.Wait(holdMs, stop);
            Up(vk);
            return keepGoing;
        }
    }

    /// <summary>Presses and keeps <paramref name="vk"/> down (held modifiers are released first).</summary>
    public static void PressAndHold(int vk)
    {
        lock (Gate)
        {
            ReleaseHeldModifiers();
            Down(vk);
        }
    }

    /// <summary>Sends a combination down (modifiers first) or up (main key first). Used by remaps.</summary>
    public static void SendBinding(HotkeyBinding binding, bool isDown)
    {
        lock (Gate)
        {
            if (isDown)
            {
                if (binding.Ctrl) Down(KeyNames.VK_LCONTROL);
                if (binding.Alt) Down(KeyNames.VK_LMENU);
                if (binding.Shift) Down(KeyNames.VK_LSHIFT);
                if (binding.Win) Down(KeyNames.VK_LWIN);
                Down(binding.Vk);
            }
            else
            {
                Up(binding.Vk);
                if (binding.Win) Up(KeyNames.VK_LWIN);
                if (binding.Shift) Up(KeyNames.VK_LSHIFT);
                if (binding.Alt) Up(KeyNames.VK_LMENU);
                if (binding.Ctrl) Up(KeyNames.VK_LCONTROL);
            }
        }
    }

    /// <summary>Sends Ctrl+V with the same modifier handling as <see cref="Tap"/>.</summary>
    public static void SendPasteShortcut()
    {
        lock (Gate)
        {
            ReleaseHeldModifiers();
            Down(KeyNames.VK_LCONTROL);
            Down(KeyNames.VK_V);
            Up(KeyNames.VK_V);
            Up(KeyNames.VK_LCONTROL);
        }
    }

    /// <summary>Taps the mask key so a lone Alt or Win release does not open a menu.</summary>
    public static void SendMaskKey()
    {
        lock (Gate)
        {
            Down(MaskKeyVk);
            Up(MaskKeyVk);
        }
    }

    private static void ReleaseHeldModifiers()
    {
        var held = ModifierTracker.HeldKeys();
        if (held.Count == 0)
            return;

        // Releasing Alt or Win with nothing in between would open a menu.
        if (ModifierTracker.Alt || ModifierTracker.Win)
        {
            Down(MaskKeyVk);
            Up(MaskKeyVk);
        }
        foreach (int vk in held)
            Up(vk);
    }

    private static void Send(int vk, bool isDown)
    {
        var inputs = new NativeMethods.INPUT[1];
        inputs[0] = KeyNames.IsMouse(vk) ? BuildMouse(vk, isDown) : BuildKeyboard(vk, isDown);
        NativeMethods.SendInput(1, inputs, InputSize);
    }

    private static NativeMethods.INPUT BuildMouse(int vk, bool isDown)
    {
        uint flags;
        uint data = 0;
        switch (vk)
        {
            case KeyNames.VK_LBUTTON:
                flags = isDown ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
                break;
            case KeyNames.VK_RBUTTON:
                flags = isDown ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
                break;
            case KeyNames.VK_MBUTTON:
                flags = isDown ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
                break;
            case KeyNames.VK_XBUTTON1:
                flags = isDown ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                data = NativeMethods.XBUTTON1;
                break;
            default:
                flags = isDown ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                data = NativeMethods.XBUTTON2;
                break;
        }

        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        input.U.mi = new NativeMethods.MOUSEINPUT
        {
            dwFlags = flags,
            mouseData = data,
            dwExtraInfo = Signature,
        };
        return input;
    }

    private static NativeMethods.INPUT BuildKeyboard(int vk, bool isDown)
    {
        // Many fullscreen apps read scan codes, so send the scan code when Windows knows one.
        uint scanCode = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC_EX);
        uint flags;
        ushort scan = 0;

        if (scanCode != 0)
        {
            flags = NativeMethods.KEYEVENTF_SCANCODE;
            if ((scanCode & 0xFF00) is 0xE000 or 0xE100)
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            scan = (ushort)(scanCode & 0xFF);
        }
        else
        {
            flags = 0; // fall back to the virtual-key code
        }

        if (!isDown)
            flags |= NativeMethods.KEYEVENTF_KEYUP;

        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.U.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = (ushort)vk,
            wScan = scan,
            dwFlags = flags,
            dwExtraInfo = Signature,
        };
        return input;
    }
}
