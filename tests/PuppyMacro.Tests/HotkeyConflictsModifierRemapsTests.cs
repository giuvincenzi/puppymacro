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

    /// <summary>
    /// Scenario: Right Alt is remapped to another key for all apps (a common choice on keyboards without AltGr use). The user
    /// then gives a loop (or a macro, or a global hotkey) the hotkey Right Alt + F23 for all apps. HotkeyConflicts compares
    /// only main keys (Overlaps: F23 against Right Alt) and accepts it, but the hotkey can never start the loop (test above).
    /// Expected: the editor refuses it, as for any other clash with a remap for the same apps ("one rule for loops, macros
    /// and remaps", input.md).
    /// </summary>
    [Fact]
    public void A_loop_hotkey_that_needs_a_remapped_modifier_clashes_with_that_remap()
    {
        Assert.NotNull(HotkeyConflicts.Find(RightAltF23, _settings, _macros));
    }

    /// <summary>
    /// The same for a global hotkey: Stop all set to Right Alt + F23 could never stop anything while Right Alt is remapped.
    /// </summary>
    [Fact]
    public void A_global_hotkey_that_needs_a_remapped_modifier_clashes_with_that_remap()
    {
        Assert.NotNull(HotkeyConflicts.Find(RightAltF23, _settings, _macros, ignoreGlobal: "StopAll"));
    }

    /// <summary>
    /// And for another remap: a remap of Right Alt + F23 next to the remap of Right Alt (both for all apps) never applies.
    /// </summary>
    [Fact]
    public void A_remap_source_that_needs_a_remapped_modifier_clashes_with_that_remap()
    {
        Assert.NotNull(HotkeyConflicts.RemapClash(RightAltF23, null, _settings, _macros, Guid.NewGuid()));
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
