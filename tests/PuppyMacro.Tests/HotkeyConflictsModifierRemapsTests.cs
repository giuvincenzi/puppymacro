using System;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// HotkeyConflicts against a remap whose source is a modifier key alone (Right Alt → F24). Such a modifier is blocked by
/// the remap and never reaches ModifierTracker (input.md), so no key press can hold it as a modifier: a hotkey or a remap
/// source that needs it on that side can never be pressed. The LoopEngine part only decides (OnKey); the only key sent is
/// F24, a remap's target. ModifierTracker is static: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class HotkeyConflictsModifierRemapsTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, RAlt = 0xA5, LAlt = 0xA4;

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly MacroLibrary _macros;

    public HotkeyConflictsModifierRemapsTests()
    {
        ModifierTracker.Reset();
        _macros = new MacroLibrary(_folder.Path);
        _macros.Load(Array.Empty<Guid>(), out _);
        _settings.Remaps.Add(new RemapDefinition { Source = HotkeyBinding.FromKey(RAlt), Target = HotkeyBinding.FromKey(F24) });
    }

    public void Dispose()
    {
        ModifierTracker.Reset();
        _folder.Dispose();
    }

    private static HotkeyBinding RightAltF23 => new() { Vk = F23, Alt = true, AltSide = ModifierSide.Right };

    /// <summary>
    /// The engine side, which the conflict check below must agree with: with Right Alt remapped for all apps, a loop on
    /// Right Alt + F23 for all apps never starts, as Right Alt is taken by the remap and does not count as Alt.
    /// </summary>
    [Fact]
    public void A_hotkey_with_a_remapped_modifier_on_its_side_can_never_be_pressed()
    {
        var loop = new LoopDefinition
        {
            Name = "Dead",
            Hotkey = RightAltF23,
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = F24, IntervalValue = 10, IntervalUnit = IntervalUnit.Seconds } },
        };
        var engine = new LoopEngine(new EngineSnapshot { Loops = new[] { loop }, Remaps = _settings.Remaps.ToArray() },
            Dispatcher.CurrentDispatcher);
        try
        {
            Assert.True(engine.OnKey(RAlt, true, true));   // the remap takes it
            Assert.False(ModifierTracker.Alt);
            Assert.False(engine.OnKey(F23, true, true));   // plain F23: not the loop's hotkey
            engine.OnKey(F23, false, true);
            Assert.True(engine.OnKey(RAlt, false, true));
            Assert.False(engine.IsRunning(loop.Id));
        }
        finally
        {
            engine.StopAll();
            engine.Dispose();
        }
    }

    /// <summary>The other side is another key press: Left Alt + F23 works with Right Alt remapped, and is accepted.</summary>
    [Fact]
    public void A_hotkey_with_the_modifier_on_the_other_side_is_accepted()
    {
        var leftAltF23 = new HotkeyBinding { Vk = F23, Alt = true, AltSide = ModifierSide.Left };
        Assert.Null(HotkeyConflicts.Find(leftAltF23, _settings, _macros));
        Assert.Null(HotkeyConflicts.RemapClash(leftAltF23, null, _settings, _macros, Guid.NewGuid()));
    }

    /// <summary>A remap of the modifier alone for the same apps clashes with another remap of it on an overlapping side.</summary>
    [Fact]
    public void Two_remaps_of_the_same_modifier_alone_for_the_same_apps_clash()
    {
        Assert.NotNull(HotkeyConflicts.AlreadyRemapped(HotkeyBinding.FromKey(RAlt), null, _settings, Guid.NewGuid()));
        Assert.Null(HotkeyConflicts.AlreadyRemapped(HotkeyBinding.FromKey(LAlt), null, _settings, Guid.NewGuid()));
        Assert.Null(HotkeyConflicts.AlreadyRemapped(HotkeyBinding.FromKey(RAlt), "game.exe", _settings, Guid.NewGuid()));
    }
}
