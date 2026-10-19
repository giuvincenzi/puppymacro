using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// KeyNames for the keys its table does not list, which Windows names (<c>KeyNames.FromSystem</c>).
/// </summary>
public class KeyNamesEdgesTests
{

    [Theory]
    [InlineData(0x30, "0")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x60, "Numpad0")]
    [InlineData(0x69, "Numpad9")]
    [InlineData(0x6F, "Numpad/")]
    [InlineData(0x2E, "Delete")]
    [InlineData(0xA4, "Left Alt")]
    [InlineData(0x5C, "Right Win")]
    [InlineData(KeyNames.VK_WHEEL_UP, "Wheel up")]
    [InlineData(KeyNames.VK_WHEEL_RIGHT, "Wheel right")]
    public void The_table_names_the_keys_it_lists(int vk, string expected) =>
        Assert.Equal(expected, KeyNames.Get(vk));

    [Fact]
    public void WheelVk_takes_the_direction_from_the_sign_and_the_axis()
    {
        Assert.Equal(KeyNames.VK_WHEEL_UP, KeyNames.WheelVk(horizontal: false, delta: 120));
        Assert.Equal(KeyNames.VK_WHEEL_DOWN, KeyNames.WheelVk(horizontal: false, delta: -1));
        Assert.Equal(KeyNames.VK_WHEEL_RIGHT, KeyNames.WheelVk(horizontal: true, delta: 30));
        Assert.Equal(KeyNames.VK_WHEEL_LEFT, KeyNames.WheelVk(horizontal: true, delta: -120));
        // Wheel codes are outside what a keyboard hook can report (1 to 254), so never a real key or a mouse button.
        for (int vk = 1; vk <= 254; vk++)
            Assert.False(KeyNames.IsWheel(vk));
        Assert.False(KeyNames.IsMouse(KeyNames.VK_WHEEL_UP));
    }

    [Fact]
    public void A_sided_modifier_is_a_modifier_and_a_generic_one_is_not_sided()
    {
        foreach (int vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            Assert.True(KeyNames.IsSidedModifier(vk));
            Assert.True(KeyNames.IsModifier(vk));
        }
        foreach (int vk in new[] { 0x10, 0x11, 0x12 })
        {
            Assert.False(KeyNames.IsSidedModifier(vk));
            Assert.True(KeyNames.IsModifier(vk));
        }
    }
}
