using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class KeyNamesTests
{
    [Theory]
    [InlineData(0x01, "LButton")]
    [InlineData(0x02, "RButton")]
    [InlineData(0x05, "XButton1")]
    [InlineData(0x0D, "Enter")]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x41, "A")]
    [InlineData(0x39, "9")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x63, "Numpad3")]
    [InlineData(0xA3, "RCtrl")]
    public void Get_returns_the_display_name(int vk, string expected) =>
        Assert.Equal(expected, KeyNames.Get(vk));

    [Fact]
    public void Format_lists_modifiers_in_a_fixed_order_then_the_key()
    {
        var hotkey = new HotkeyBinding { Vk = 0x78, Win = true, Shift = true, Alt = true, Ctrl = true };

        Assert.Equal("Ctrl + Alt + Shift + Win + F9", KeyNames.Format(hotkey));
    }

    [Fact]
    public void Format_of_a_missing_hotkey_is_No_hotkey()
    {
        Assert.Equal("No hotkey", KeyNames.Format(null));
        Assert.Equal("No hotkey", KeyNames.Format(new HotkeyBinding()));
    }

    [Theory]
    [InlineData(0x01, true)]
    [InlineData(0x02, true)]
    [InlineData(0x04, false)]
    [InlineData(0x05, false)]
    [InlineData(0x41, false)]
    public void Only_left_and_right_click_are_primary_mouse_buttons(int vk, bool expected) =>
        Assert.Equal(expected, KeyNames.IsPrimaryMouse(vk));

    [Theory]
    [InlineData(0x11, true)]
    [InlineData(0xA2, true)]
    [InlineData(0xA5, true)]
    [InlineData(0x5B, true)]
    [InlineData(0x41, false)]
    [InlineData(0x01, false)]
    public void IsModifier_covers_generic_left_and_right_codes(int vk, bool expected) =>
        Assert.Equal(expected, KeyNames.IsModifier(vk));
}
