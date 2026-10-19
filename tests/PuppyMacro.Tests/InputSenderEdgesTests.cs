using System;
using System.Reflection;
using System.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// InputSender's lock and the events it builds. The only key sent is the release of F24, which no keyboard has; the
/// others are built and not sent. In ModifierTracker's collection, which runs alone: the test below holds InputSender's
/// lock for a moment, which would only delay other tests' sends.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class InputSenderEdgesTests
{
    private const int F24 = 0x87;

    private static object Gate => typeof(InputSender).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static NativeMethods.INPUT Built(string method, int vk, bool isDown) =>
        (NativeMethods.INPUT)typeof(InputSender).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { vk, isDown })!;

    /// <summary>
    /// Scenario: a loop row presses Ctrl+S (TapBinding: Ctrl down, S down, the hold, all under InputSender's lock so that
    /// "nothing else PuppyMacro sends lands between them", input.md) while a macro runs a Key down / Key up action, or a
    /// remap repeats its target, or a macro's Move to runs. Those go through InputSender.Down / Up / MoveTo, which send
    /// without the lock: the macro's key lands while the loop's Ctrl is down and the app gets Ctrl + that key; a macro's
    /// move lands between a loop's move and its click at a fixed point, so the click happens elsewhere. Expected: every
    /// send waits for the lock while another holds it. Here the test holds the lock and Up(F24) goes through anyway.
    /// </summary>
    [Fact]
    public void A_single_key_event_waits_while_another_sender_holds_the_lock()
    {
        var sent = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            InputSender.Up(F24);
            sent.Set();
        }) { IsBackground = true };

        bool sentWhileHeld;
        lock (Gate)
        {
            thread.Start();
            sentWhileHeld = sent.Wait(500, TestContext.Current.CancellationToken);
        }
        Assert.True(thread.Join(5000));
        Assert.False(sentWhileHeld, "InputSender.Up sent while another combination held InputSender's lock");
    }

    [Theory]
    [InlineData(0x01, NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP, 0u)]
    [InlineData(0x02, NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP, 0u)]
    [InlineData(0x04, NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u)]
    [InlineData(0x05, NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON1)]
    [InlineData(0x06, NativeMethods.MOUSEEVENTF_XDOWN, NativeMethods.MOUSEEVENTF_XUP, NativeMethods.XBUTTON2)]
    public void Every_mouse_button_is_built_with_its_own_flags_and_the_signature(int vk, uint down, uint up, uint data)
    {
        var press = Built("BuildMouse", vk, true);
        var release = Built("BuildMouse", vk, false);
        Assert.Equal(NativeMethods.INPUT_MOUSE, press.type);
        Assert.Equal(down, press.U.mi.dwFlags);
        Assert.Equal(up, release.U.mi.dwFlags);
        Assert.Equal(data, press.U.mi.mouseData);
        Assert.Equal(data, release.U.mi.mouseData);
        Assert.Equal(InputSender.Signature, press.U.mi.dwExtraInfo);
        Assert.Equal(InputSender.Signature, release.U.mi.dwExtraInfo);
    }

    /// <summary>
    /// The keys whose real scan code has E0 are sent with the extended flag, so the app reads the same key the keyboard
    /// sends (right Ctrl and Alt, the Windows and Menu keys, the media keys, number pad's Divide).
    /// </summary>
    [Theory]
    [InlineData(0xA3)] // Right Ctrl
    [InlineData(0xA5)] // Right Alt
    [InlineData(0x5B)] // Left Win
    [InlineData(0x5C)] // Right Win
    [InlineData(0x5D)] // Menu
    [InlineData(0x6F)] // number pad's Divide
    [InlineData(0xAF)] // Volume Up
    [InlineData(0xB3)] // Play/Pause
    public void An_extended_key_is_sent_with_the_extended_flag(int vk)
    {
        var ki = Built("BuildKeyboard", vk, true).U.ki;
        Assert.NotEqual(0u, ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    [Theory]
    [InlineData(0xA2)] // Left Ctrl
    [InlineData(0xA0)] // Left Shift
    [InlineData(0xA1)] // Right Shift: not extended (scan code 36)
    [InlineData(0x0D)] // Enter (the main one)
    [InlineData(0x6A)] // number pad's Multiply
    public void A_key_that_is_not_extended_is_sent_without_the_flag(int vk)
    {
        var ki = Built("BuildKeyboard", vk, true).U.ki;
        Assert.NotEqual(0u, ki.dwFlags & NativeMethods.KEYEVENTF_SCANCODE);
        Assert.Equal(0u, ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY);
    }

    /// <summary>The wheel is a hotkey only: its codes are not virtual keys and are never in a mouse event.</summary>
    [Fact]
    public void Wheel_codes_are_not_mouse_buttons()
    {
        Assert.False(KeyNames.IsMouse(KeyNames.VK_WHEEL_UP));
        Assert.False(KeyNames.IsMouse(KeyNames.VK_WHEEL_LEFT));
    }

    /// <summary>A combination's modifiers go on their side; either side is the left one (input.md).</summary>
    [Fact]
    public void HotkeyBinding_sides_default_to_either_and_SameAs_ignores_the_side_of_an_unused_modifier()
    {
        var a = new HotkeyBinding { Vk = 0x41, Ctrl = true };
        var b = new HotkeyBinding { Vk = 0x41, Ctrl = true, AltSide = ModifierSide.Right };
        Assert.Equal(ModifierSide.Any, a.CtrlSide);
        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left }));
    }
}
