using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class HotkeyRulesTests
{
    [Theory]
    [InlineData(KeyNames.VK_LBUTTON)]
    [InlineData(KeyNames.VK_RBUTTON)]
    [InlineData(KeyNames.VK_WHEEL_UP)]
    [InlineData(KeyNames.VK_WHEEL_RIGHT)]
    [InlineData(KeyNames.VK_ESCAPE)]
    public void Click_wheel_and_Esc_need_a_modifier(int vk)
    {
        Assert.NotNull(HotkeyRules.Problem(HotkeyBinding.FromKey(vk), hold: false));
        Assert.Null(HotkeyRules.Problem(new HotkeyBinding { Vk = vk, Ctrl = true }, hold: false));
        Assert.Null(HotkeyRules.Problem(new HotkeyBinding { Vk = vk, Alt = true }, hold: false));
        Assert.Null(HotkeyRules.Problem(new HotkeyBinding { Vk = vk, Shift = true }, hold: false));
        Assert.Null(HotkeyRules.Problem(new HotkeyBinding { Vk = vk, Win = true }, hold: false));
    }

    [Theory]
    [InlineData(0x75)]                    // F6
    [InlineData(KeyNames.VK_MBUTTON)]
    [InlineData(KeyNames.VK_XBUTTON1)]
    public void Other_keys_and_buttons_work_alone(int vk)
    {
        Assert.Null(HotkeyRules.Problem(HotkeyBinding.FromKey(vk), hold: true));
    }

    [Fact]
    public void Click_can_be_held_but_the_wheel_cannot()
    {
        Assert.Null(HotkeyRules.Problem(new HotkeyBinding { Vk = KeyNames.VK_RBUTTON, Shift = true }, hold: true));
        Assert.NotNull(HotkeyRules.Problem(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true }, hold: true));
    }

    [Fact]
    public void Wheel_hotkeys_have_readable_names()
    {
        Assert.Equal("Ctrl + Wheel up", KeyNames.Format(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_UP, Ctrl = true }));
        Assert.Equal("Alt + Shift + Wheel left", KeyNames.Format(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_LEFT, Alt = true, Shift = true }));
        Assert.Equal(KeyNames.VK_WHEEL_UP, KeyNames.WheelVk(horizontal: false, delta: 120));
        Assert.Equal(KeyNames.VK_WHEEL_DOWN, KeyNames.WheelVk(horizontal: false, delta: -30));
        Assert.Equal(KeyNames.VK_WHEEL_RIGHT, KeyNames.WheelVk(horizontal: true, delta: 120));
        Assert.Equal(KeyNames.VK_WHEEL_LEFT, KeyNames.WheelVk(horizontal: true, delta: -120));
    }
}
