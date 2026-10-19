using System.Reflection;
using PuppyMacro.Native;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The keyboard events InputSender builds (nothing is sent): with KEYEVENTF_SCANCODE Windows ignores the virtual-key code
/// and reads the key from the scan code and the extended flag, so two keys built with the same scan code and flags are the
/// same key for the app.
/// </summary>
public sealed class InputSenderTests
{
    private const int Pause = 0x13, RightCtrl = 0xA3, LeftCtrl = 0xA2, A = 0x41, Left = 0x25, F24 = 0x87;

    private static NativeMethods.KEYBDINPUT Built(int vk, bool isDown = true)
    {
        var build = typeof(InputSender).GetMethod("BuildKeyboard", BindingFlags.NonPublic | BindingFlags.Static)!;
        var input = (NativeMethods.INPUT)build.Invoke(null, new object[] { vk, isDown })!;
        return input.U.ki;
    }

    private static (ushort Scan, uint Flags) Key(int vk) => (Built(vk).wScan, Built(vk).dwFlags);

    [Fact]
    public void Every_key_carries_the_signature_and_its_release_the_key_up_flag()
    {
        foreach (int vk in new[] { A, Left, F24, LeftCtrl })
        {
            Assert.Equal(InputSender.Signature, Built(vk).dwExtraInfo);
            Assert.Equal(0u, Built(vk, isDown: true).dwFlags & NativeMethods.KEYEVENTF_KEYUP);
            Assert.NotEqual(0u, Built(vk, isDown: false).dwFlags & NativeMethods.KEYEVENTF_KEYUP);
        }
    }

    [Fact]
    public void Left_and_right_Ctrl_are_different_keys_and_a_letter_is_not_extended()
    {
        Assert.NotEqual(Key(LeftCtrl), Key(RightCtrl));
        Assert.NotEqual(0u, Key(A).Flags & NativeMethods.KEYEVENTF_SCANCODE);
        Assert.Equal(0u, Key(A).Flags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    /// <summary>
    /// The arrows and Insert, Delete, Home, End, Page Up, Page Down share their scan codes with the number pad's keys and
    /// differ only by the E0 prefix (the extended flag). MapVirtualKey(VK_LEFT, MAPVK_VK_TO_VSC_EX) gives 0x4B with no E0
    /// (as Windows maps these keys), so BuildKeyboard sends them without KEYEVENTF_EXTENDEDKEY: exactly the
    /// number pad's keys. With Num Lock on (the usual state) Windows turns them into the digits: a macro or loop that
    /// presses Left types "4", Delete types the decimal point, Home "7"; a game that reads scan codes sees the number pad.
    /// </summary>
    [Theory]
    [InlineData(0x25, 0x64)] // Left, number pad 4
    [InlineData(0x26, 0x68)] // Up, number pad 8
    [InlineData(0x27, 0x66)] // Right, number pad 6
    [InlineData(0x28, 0x62)] // Down, number pad 2
    [InlineData(0x2D, 0x60)] // Insert, number pad 0
    [InlineData(0x2E, 0x6E)] // Delete, number pad decimal point
    [InlineData(0x24, 0x67)] // Home, number pad 7
    [InlineData(0x23, 0x61)] // End, number pad 1
    [InlineData(0x21, 0x69)] // Page Up, number pad 9
    [InlineData(0x22, 0x63)] // Page Down, number pad 3
    public void A_navigation_key_is_not_sent_as_its_number_pad_key(int key, int numberPadKey)
    {
        Assert.NotEqual(Key(numberPadKey), Key(key));
    }

    /// <summary>
    /// Pause has the scan code E1 1D 45, and MapVirtualKey(VK_PAUSE, MAPVK_VK_TO_VSC_EX) gives 0xE11D. BuildKeyboard treats
    /// the E1 prefix as E0 (KEYEVENTF_EXTENDEDKEY) and keeps 1D: E0 1D is Right Ctrl. A loop or macro that presses Pause (a
    /// game's pause key) sends Right Ctrl.
    /// </summary>
    [Fact]
    public void Pause_is_not_sent_as_Right_Ctrl()
    {
        Assert.NotEqual(Key(RightCtrl), Key(Pause));
    }
}
