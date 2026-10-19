using System;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// LoopEngine's decisions on input that the other LoopEngine classes do not cover: AltGr's fake left Ctrl with other
/// modifiers held, the app in front for remaps against loops, the desktop switch. Only decisions (OnKey); the only key
/// sent is F24 (a remap's target). ModifierTracker is static: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class LoopEngineInputTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, LCtrl = 0xA2, LShift = 0xA0, RAlt = 0xA5;

    private readonly LoopEngine _engine;

    public LoopEngineInputTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _engine.CancelCapture();
        _engine.StopAll();
        _engine.Dispose();
        ModifierTracker.Reset();
    }

    private static LoopDefinition TapLoop(HotkeyBinding hotkey) => new()
    {
        Name = "Tap",
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = F24, IntervalValue = 10, IntervalUnit = IntervalUnit.Seconds } },
        Hotkey = hotkey,
    };

    private static RemapDefinition Remap(HotkeyBinding source, string? app = null) =>
        new() { Source = source, Target = HotkeyBinding.FromKey(F24), AppExe = app };

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    private bool FakeCtrl(bool isDown) => _engine.OnKey(LCtrl, isDown, physicalModifierEvent: false, altGrCtrl: true);

    /// <summary>
    /// Scenario: Right Alt is remapped (Right Alt → F24, all apps), and the user presses Shift + AltGr (on many layouts
    /// AltGr + Shift types characters: Italian "{" is AltGr + Shift + è). Windows sends AltGr as a fake left Ctrl, then
    /// Right Alt. OnModifier blocks the fake Ctrl when FindRemap(Right Alt alone) applies, ignoring the modifiers held; but
    /// Right Alt itself is matched exactly with the modifiers held (Shift + Right Alt), so the remap does not apply and
    /// Right Alt reaches the app. The app gets Shift + Right Alt without AltGr's Ctrl: not AltGr, so the character is
    /// not typed (or a menu shortcut runs). Expected: the fake Ctrl is blocked only when Right Alt will be remapped.
    /// </summary>
    [Fact]
    public void The_fake_Ctrl_of_AltGr_passes_when_Right_Alt_is_pressed_with_Shift_and_its_remap_does_not_apply()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(RAlt)) } });

        Press(LShift);
        bool fakeCtrlBlocked = FakeCtrl(true);
        bool rightAltBlocked = Press(RAlt);
        Release(RAlt);
        FakeCtrl(false);
        Release(LShift);

        Assert.False(rightAltBlocked); // with Shift held it is not the remap's source (exact match)
        Assert.Equal(rightAltBlocked, fakeCtrlBlocked);
    }

    /// <summary>The usual case stays as it is: AltGr alone with Right Alt remapped, both halves are blocked.</summary>
    [Fact]
    public void AltGr_alone_with_Right_Alt_remapped_blocks_both_halves_and_counts_as_no_modifier()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(RAlt)) } });

        Assert.True(FakeCtrl(true));
        Assert.True(Press(RAlt));
        Assert.False(ModifierTracker.Any);
        Assert.True(Release(RAlt));
        Assert.True(FakeCtrl(false));
        Assert.False(ModifierTracker.Any);
    }

    /// <summary>
    /// Scenario: the user's game (Game.exe) is the app in front for PuppyMacro (_foregroundApp, which never takes
    /// PuppyMacro's own windows). A remap for Game.exe and a loop for all apps share F23: "a remap for the app in front beats
    /// the loops and macros for all apps" (input.md). But FindRemap decides the app in front with GetForegroundWindow, not
    /// with _foregroundApp as RunHotkey does: whenever the two differ (a PuppyMacro window in front, here the test's own
    /// process) the loop for all apps starts and the remap for the game is skipped, while the loops for Game.exe still
    /// count as in front. Expected: remaps and loops agree on which app is in front.
    /// </summary>
    [Fact]
    public void A_remap_for_the_app_in_front_wins_over_a_loop_for_all_apps_with_the_same_app_as_the_loops_see()
    {
        LoopDefinition everywhere = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { everywhere }, Remaps = new[] { Remap(HotkeyBinding.FromKey(F23), "Game.exe") } });
        _engine.OnForegroundApp("Game.exe");

        Press(F23);
        Release(F23);

        Assert.False(_engine.IsRunning(everywhere.Id), "the loop for all apps took the key from the remap for the app in front");
    }

    /// <summary>The loops' side of the same: a loop for Game.exe counts Game.exe as in front (passes today).</summary>
    [Fact]
    public void A_loop_for_the_app_in_front_starts_on_its_hotkey()
    {
        LoopDefinition forGame = TapLoop(HotkeyBinding.FromKey(F23));
        forGame.AppExe = "Game.exe";
        _engine.Publish(new EngineSnapshot { Loops = new[] { forGame } });
        _engine.OnForegroundApp("game.exe");

        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.True(_engine.IsRunning(forGame.Id));
    }

    /// <summary>
    /// Scenario: a Hold loop on Ctrl + F23 runs (started from its card's Play, or with its hotkey); the user presses and
    /// releases F23 alone, the hotkey of a Toggle loop. ReleaseOwned stops every Hold loop and macro whose hotkey has the
    /// released main key (loop.Hotkey.Vk == vk), whatever its modifiers, app and whether this press started it: the Hold
    /// loop on Ctrl + F23 stops although its hotkey was never pressed. Expected: only the items the press started stop.
    /// </summary>
    [Fact]
    public void Releasing_a_Toggle_hotkey_does_not_stop_a_Hold_loop_with_another_hotkey_on_the_same_key()
    {
        LoopDefinition hold = TapLoop(new HotkeyBinding { Vk = F23, Ctrl = true });
        hold.Mode = ActivationMode.Hold;
        LoopDefinition toggle = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { hold, toggle } });

        _engine.StartLoop(hold);
        Assert.True(Press(F23));
        Assert.True(Release(F23));

        Assert.True(_engine.IsRunning(toggle.Id));
        Assert.True(_engine.IsRunning(hold.Id), "the Hold loop on Ctrl + F23 stopped when F23 alone was released");
    }

    /// <summary>
    /// A remap of a combination (Ctrl + F23): released key first, then its modifier. The target is released with the source,
    /// and the modifier's release passes (it was held before and is the user's).
    /// </summary>
    [Fact]
    public void A_remap_of_a_combination_releases_its_target_with_the_key_and_lets_the_modifier_pass()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(new HotkeyBinding { Vk = F23, Ctrl = true }) } });

        Assert.False(Press(LCtrl));
        Assert.True(Press(F23));
        Assert.False(Release(LCtrl));  // the modifier first
        Assert.True(Press(F23));       // auto-repeat without Ctrl: still the remap's key
        Assert.True(Release(F23));
        Assert.False(Press(F23));      // now plain F23: not the remap's source
        Assert.False(Release(F23));
    }

    /// <summary>
    /// After a desktop switch (lock screen, UAC) the releases may never come: the modifiers are forgotten and a remap held
    /// before is not held any more, so the source's release that arrives later passes.
    /// </summary>
    [Fact]
    public void A_desktop_switch_forgets_the_modifiers_and_the_remaps_held()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(F23)) } });
        Assert.True(Press(F23));
        Assert.False(Press(LShift));

        typeof(LoopEngine).GetMethod("OnDesktopSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null);

        Assert.False(ModifierTracker.Any);
        Assert.False(Release(F23));
    }
}
