using System.Linq;
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
    public void A_hotkey_shows_the_side_of_a_modifier_when_only_one_counts()
    {
        var hotkey = new HotkeyBinding { Vk = 0x41, Alt = true, AltSide = ModifierSide.Right };
        Assert.Equal(new[] { "Right Alt", "A" }, KeyCaptureField.DisplayParts(hotkey, keyOnly: false));
        Assert.Equal(new[] { "Right Alt" }, KeyCaptureField.DisplayParts(HotkeyBinding.FromKey(0xA5), keyOnly: false));
    }

    [Fact]
    public void The_menu_offers_the_sides_of_each_modifier_held()
    {
        var hotkey = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left, Shift = true };

        var groups = KeyCaptureField.SideChoices(hotkey, keyOnly: false);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "Left Ctrl", "Right Ctrl", "Left or right Ctrl" }, groups[0].Select(c => c.Label));
        Assert.Equal(new[] { true, false, false }, groups[0].Select(c => c.IsChecked));
        Assert.Equal(new[] { "Left Shift", "Right Shift", "Left or right Shift" }, groups[1].Select(c => c.Label));
        Assert.Equal(new[] { false, false, true }, groups[1].Select(c => c.IsChecked));
        Assert.Empty(KeyCaptureField.SideChoices(hotkey, keyOnly: true));
        Assert.Empty(KeyCaptureField.SideChoices(HotkeyBinding.FromKey(0xA5), keyOnly: false)); // a modifier alone names its side
    }

    [Fact]
    public void Choosing_a_side_changes_only_that_modifier()
    {
        var hotkey = new HotkeyBinding { Vk = 0x41, Ctrl = true, CtrlSide = ModifierSide.Left, Alt = true, AltSide = ModifierSide.Right };

        var changed = KeyCaptureField.WithSide(hotkey, "Ctrl", ModifierSide.Any);

        Assert.True(changed.SameAs(new HotkeyBinding { Vk = 0x41, Ctrl = true, Alt = true, AltSide = ModifierSide.Right }));
        Assert.Equal(ModifierSide.Left, hotkey.CtrlSide); // a copy
    }

    [Fact]
    public void A_single_key_field_shows_only_the_key()
    {
        var pressed = new HotkeyBinding { Vk = 0x41, Ctrl = true };
        Assert.Equal(new[] { "A" }, KeyCaptureField.DisplayParts(pressed, keyOnly: true));
    }
}
