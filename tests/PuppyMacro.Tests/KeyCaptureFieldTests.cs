using PuppyMacro.Models;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>What the key field shows for a value (no WPF window: only the static logic).</summary>
public class KeyCaptureFieldTests
{
    [Fact]
    public void Without_a_value_it_shows_no_keys()
    {
        Assert.Empty(KeyCaptureField.DisplayParts(null, keyOnly: true));
        Assert.Empty(KeyCaptureField.DisplayParts(new HotkeyBinding(), keyOnly: false));
    }

    [Fact]
    public void A_hotkey_shows_its_modifiers_then_its_key()
    {
        var hotkey = new HotkeyBinding { Vk = 0x75, Ctrl = true, Shift = true };
        Assert.Equal(new[] { "Ctrl", "Shift", "F6" }, KeyCaptureField.DisplayParts(hotkey, keyOnly: false));
    }

    [Fact]
    public void A_single_key_field_shows_only_the_key()
    {
        var pressed = new HotkeyBinding { Vk = 0x41, Ctrl = true };
        Assert.Equal(new[] { "A" }, KeyCaptureField.DisplayParts(pressed, keyOnly: true));
    }
}
