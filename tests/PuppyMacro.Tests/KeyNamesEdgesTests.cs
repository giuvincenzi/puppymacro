using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// KeyNames for the keys its table does not list, which Windows names (<c>KeyNames.FromSystem</c>).
/// </summary>
public class KeyNamesEdgesTests
{
    /// <summary>
    /// A key whose scan code has the E0 prefix (the volume and media keys, Break) and that KeyNames' table does not list is
    /// named by FromSystem with MAPVK_VK_TO_VSC, which drops the prefix, and GetKeyNameText without the extended bit (bit
    /// 24): Windows then names the plain key with the same scan code. Volume Up (E0 30) shows as "B", Volume Down (E0 2E)
    /// as "C", Mute (E0 20) as "D", Play/Pause (E0 22) as "G", Break (E0 46) as "Scroll Lock". A hotkey captured on a
    /// keyboard's media key is shown everywhere (its card, the editor, the conflict messages) as a letter the user never
    /// pressed, and "Volume Up" and "B" look like the same hotkey.
    /// </summary>
    [Theory]
    [InlineData(0xAF, 0x42)] // Volume Up (E0 30) and B (30)
    [InlineData(0xAE, 0x43)] // Volume Down (E0 2E) and C (2E)
    [InlineData(0xAD, 0x44)] // Volume Mute (E0 20) and D (20)
    [InlineData(0xB3, 0x47)] // Play/Pause (E0 22) and G (22)
    [InlineData(0x03, 0x91)] // Break, Ctrl+Pause (E0 46), and Scroll Lock (46)
    public void A_key_with_an_E0_scan_code_is_not_named_as_the_key_without_it(int extendedKey, int plainKey)
    {
        Assert.NotEqual(KeyNames.Get(plainKey).Replace(" ", ""), KeyNames.Get(extendedKey).Replace(" ", ""));
    }

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
