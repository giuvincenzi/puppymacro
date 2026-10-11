using System;
using System.Collections.Generic;
using PuppyMacro.Models;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>Display names and classification for virtual-key codes.</summary>
internal static class KeyNames
{
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;
    public const int VK_XBUTTON1 = 0x05;
    public const int VK_XBUTTON2 = 0x06;
    public const int VK_ESCAPE = 0x1B;

    public const int VK_RETURN = 0x0D;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_V = 0x56;

    // Scroll wheel as a hotkey key. Not virtual-key codes: keyboard hooks only report 1 to 254
    // (KBDLLHOOKSTRUCT.vkCode), so these can never collide with a real key.
    public const int VK_WHEEL_UP = 0x100;
    public const int VK_WHEEL_DOWN = 0x101;
    public const int VK_WHEEL_LEFT = 0x102;
    public const int VK_WHEEL_RIGHT = 0x103;

    /// <summary>One notch of the scroll wheel (Windows' WHEEL_DELTA).</summary>
    public const int WheelDelta = 120;

    public static bool IsWheel(int vk) => vk is >= VK_WHEEL_UP and <= VK_WHEEL_RIGHT;

    /// <summary>The wheel "key" for a wheel event: positive delta is up (vertical) or right (horizontal).</summary>
    public static int WheelVk(bool horizontal, int delta) =>
        horizontal ? (delta > 0 ? VK_WHEEL_RIGHT : VK_WHEEL_LEFT) : (delta > 0 ? VK_WHEEL_UP : VK_WHEEL_DOWN);

    /// <summary>Ctrl, Alt, Shift and Win (generic, left and right codes).</summary>
    public static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL
            or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    /// <summary>A modifier key alone, by its side (left or right Ctrl, Alt, Shift, Win): a remap's source or target.</summary>
    public static bool IsSidedModifier(int vk) =>
        vk is VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    /// <summary>Display parts of a hotkey, e.g. ["Left Ctrl", "Shift", "F9"] (no side: either one).</summary>
    public static List<string> Parts(HotkeyBinding? hotkey)
    {
        var parts = new List<string>();
        if (hotkey == null || !hotkey.IsSet)
        {
            parts.Add("No hotkey");
            return parts;
        }
        if (hotkey.Ctrl) parts.Add(Modifier("Ctrl", hotkey.CtrlSide));
        if (hotkey.Alt) parts.Add(Modifier("Alt", hotkey.AltSide));
        if (hotkey.Shift) parts.Add(Modifier("Shift", hotkey.ShiftSide));
        if (hotkey.Win) parts.Add(Modifier("Win", hotkey.WinSide));
        parts.Add(Get(hotkey.Vk));
        return parts;
    }

    /// <summary>"Ctrl", "Left Ctrl" or "Right Ctrl".</summary>
    public static string Modifier(string name, ModifierSide side) => side switch
    {
        ModifierSide.Left => "Left " + name,
        ModifierSide.Right => "Right " + name,
        _ => name,
    };

    public static string Format(HotkeyBinding? hotkey) => string.Join(" + ", Parts(hotkey));

    public static bool IsMouse(int vk) =>
        vk is VK_LBUTTON or VK_RBUTTON or VK_MBUTTON or VK_XBUTTON1 or VK_XBUTTON2;

    /// <summary>Left and right click: hotkeys only with a modifier, since blocking them alone would break the mouse.</summary>
    public static bool IsPrimaryMouse(int vk) => vk is VK_LBUTTON or VK_RBUTTON;

    public static string Get(int vk) => vk switch
    {
        0 => "None",
        VK_WHEEL_UP => "Wheel up",
        VK_WHEEL_DOWN => "Wheel down",
        VK_WHEEL_LEFT => "Wheel left",
        VK_WHEEL_RIGHT => "Wheel right",
        VK_LBUTTON => "LButton",
        VK_RBUTTON => "RButton",
        VK_MBUTTON => "MButton",
        VK_XBUTTON1 => "XButton1",
        VK_XBUTTON2 => "XButton2",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x13 => "Pause",
        0x14 => "CapsLock",
        VK_ESCAPE => "Esc",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        0x5B => "Left Win",
        0x5C => "Right Win",
        >= 0x60 and <= 0x69 => "Numpad" + (vk - 0x60),
        0x6A => "Numpad*",
        0x6B => "Numpad+",
        0x6D => "Numpad-",
        0x6E => "Numpad.",
        0x6F => "Numpad/",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x90 => "NumLock",
        0x91 => "ScrollLock",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xA4 => "Left Alt",
        0xA5 => "Right Alt",
        _ => FromSystem(vk),
    };

    private static string FromSystem(int vk)
    {
        uint scanCode = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        if (scanCode != 0)
        {
            var buffer = new char[64];
            int length = NativeMethods.GetKeyNameText((int)(scanCode << 16), buffer, buffer.Length);
            if (length > 0)
                return new string(buffer, 0, length);
        }
        return $"Key 0x{vk:X2}";
    }
}
