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
    /// The press and the release are each sent under <see cref="Gate"/>, the hold waits outside it: a hold never keeps
    /// other loop rows, macros and remaps waiting. With <paramref name="x"/> and <paramref name="y"/>, a mouse button is
    /// pressed at that point: the move and the press together under the lock, so another row cannot move the pointer in
    /// between. The key is always released, also when <paramref name="stop"/> ends the hold.
    /// Returns false if <paramref name="stop"/> was signaled.
    /// </summary>
    public static bool Tap(int vk, double holdMs, PreciseTimer timer, WaitHandle stop, int? x = null, int? y = null)
    {
        lock (Gate)
        {
            ReleaseHeldModifiers();
            if (x is int px && y is int py)
                MoveTo(px, py);
            Down(vk);
        }
        bool keepGoing;
        try
        {
            keepGoing = timer.Wait(holdMs, stop);
        }
        finally
        {
            lock (Gate)
                Up(vk);
        }
        return keepGoing;
    }

    /// <summary>
    /// Presses and releases a combination (Ctrl+C), holding the key for <paramref name="holdMs"/>: held
    /// Ctrl/Alt/Shift/Win are released first as by <see cref="Tap"/>, then the combination's modifiers go
    /// down on their side (either side: the left one), the key, and everything comes up again in reverse
    /// order, also when <paramref name="stop"/> ends the wait. All under <see cref="Gate"/>, hold included, so nothing
    /// else PuppyMacro sends lands between them: a key sent while the combination's modifiers are down would carry
    /// them (Ctrl+ another key), the reason Hold down never holds a combination. Modifiers in <paramref name="alreadyDown"/> (a macro's
    /// Key down) are neither pressed nor released. Returns false if <paramref name="stop"/> was signaled.
    /// </summary>
    public static bool TapBinding(HotkeyBinding binding, double holdMs, PreciseTimer timer, WaitHandle stop,
        ISet<int>? alreadyDown = null)
    {
        var modifiers = new List<int>();
        if (binding.Ctrl) modifiers.Add(binding.CtrlSide == ModifierSide.Right ? KeyNames.VK_RCONTROL : KeyNames.VK_LCONTROL);
        if (binding.Alt) modifiers.Add(binding.AltSide == ModifierSide.Right ? KeyNames.VK_RMENU : KeyNames.VK_LMENU);
        if (binding.Shift) modifiers.Add(binding.ShiftSide == ModifierSide.Right ? KeyNames.VK_RSHIFT : KeyNames.VK_LSHIFT);
        if (binding.Win) modifiers.Add(binding.WinSide == ModifierSide.Right ? KeyNames.VK_RWIN : KeyNames.VK_LWIN);
        if (alreadyDown != null)
            modifiers.RemoveAll(alreadyDown.Contains);

        lock (Gate)
        {
            ReleaseHeldModifiers();
            var pressed = new List<int>();
            try
            {
                foreach (int vk in modifiers)
                {
                    Down(vk);
                    pressed.Add(vk);
                }
                Down(binding.Vk);
                bool keepGoing;
                try
                {
                    keepGoing = timer.Wait(holdMs, stop);
                }
                finally
                {
                    Up(binding.Vk);
                }
                return keepGoing;
            }
            finally
            {
                // Releasing Alt or Win right after pressing it alone would open a menu: the key went in between.
                for (int i = pressed.Count - 1; i >= 0; i--)
                    Up(pressed[i]);
            }
        }
    }

    /// <summary>
    /// Presses and keeps <paramref name="vk"/> down (held modifiers are released first); with <paramref name="x"/> and
    /// <paramref name="y"/>, a mouse button at that point, the move and the press together under <see cref="Gate"/>.
    /// </summary>
    public static void PressAndHold(int vk, int? x = null, int? y = null)
    {
        lock (Gate)
        {
            ReleaseHeldModifiers();
            if (x is int px && y is int py)
                MoveTo(px, py);
            Down(vk);
        }
    }

    /// <summary>
    /// A mouse button down or up at a point (a macro's Mouse button down and up): the move and the button together under
    /// <see cref="Gate"/>, so another loop row or macro cannot move the pointer in between.
    /// </summary>
    public static void SendAt(int vk, bool isDown, int x, int y)
    {
        lock (Gate)
        {
            MoveTo(x, y);
            Send(vk, isDown);
        }
    }

    /// <summary>
    /// Sends a combination down (modifiers first) or up (main key first), each modifier on its side (either side:
    /// the left one). Used by remaps. With <paramref name="releaseHeld"/> (a source with modifiers), the modifiers
    /// held for the source are released first, so the app gets only the combination sent; they are not pressed again.
    /// </summary>
    public static void SendBinding(HotkeyBinding binding, bool isDown, bool releaseHeld = false)
    {
        int ctrl = binding.CtrlSide == ModifierSide.Right ? KeyNames.VK_RCONTROL : KeyNames.VK_LCONTROL;
        int alt = binding.AltSide == ModifierSide.Right ? KeyNames.VK_RMENU : KeyNames.VK_LMENU;
        int shift = binding.ShiftSide == ModifierSide.Right ? KeyNames.VK_RSHIFT : KeyNames.VK_LSHIFT;
        int win = binding.WinSide == ModifierSide.Right ? KeyNames.VK_RWIN : KeyNames.VK_LWIN;
        lock (Gate)
        {
            if (isDown)
            {
                if (releaseHeld)
                    ReleaseHeldModifiers();
                if (binding.Ctrl) Down(ctrl);
                if (binding.Alt) Down(alt);
                if (binding.Shift) Down(shift);
                if (binding.Win) Down(win);
                Down(binding.Vk);
            }
            else
            {
                Up(binding.Vk);
                if (binding.Win) Up(win);
                if (binding.Shift) Up(shift);
                if (binding.Alt) Up(alt);
                if (binding.Ctrl) Up(ctrl);
            }
        }
    }

    /// <summary>
    /// Taps the mask key, then releases <paramref name="vk"/>: a lone Alt or Win release that opens no menu (a modifier
    /// pressed alone while a key field waits for it).
    /// </summary>
    public static void MaskedUp(int vk)
    {
        lock (Gate)
        {
            Down(MaskKeyVk);
            Up(MaskKeyVk);
            Up(vk);
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
