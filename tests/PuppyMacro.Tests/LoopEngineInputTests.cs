using System;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// LoopEngine's decisions on input that the other LoopEngine classes do not cover: AltGr's fake left Ctrl with other
/// modifiers held, the app in front for remaps against loops, the desktop switch, an app run as administrator in front.
/// Only decisions (OnKey); the only key
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

    /// <summary>
    /// Ctrl + Shift + Esc opens Task Manager, run as administrator: the releases of Ctrl and Shift go to it and the hooks
    /// never get them. When it leaves the front they are forgotten, so a hotkey without modifiers works again.
    /// </summary>
    [Fact]
    public void An_admin_window_leaving_the_front_forgets_the_modifiers_held_when_it_came()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        Assert.False(Press(LCtrl));
        Assert.False(Press(LShift));

        _engine.OnAdminWindowInFront(true);
        Assert.True(ModifierTracker.Ctrl && ModifierTracker.Shift); // still held while it is in front
        _engine.OnAdminWindowInFront(false);

        Assert.False(ModifierTracker.Any);
        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.True(_engine.IsRunning(loop.Id));
    }

    /// <summary>A hotkey whose release went to the admin window starts its loop again: its next press is not an auto-repeat.</summary>
    [Fact]
    public void An_admin_window_leaving_the_front_forgets_a_hotkey_held_when_it_came()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));

        _engine.OnAdminWindowInFront(true);
        _engine.OnAdminWindowInFront(false);

        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    /// <summary>Between windows that are not run as administrator nothing is forgotten: a modifier held stays held.</summary>
    [Fact]
    public void Other_windows_coming_in_front_keep_the_modifiers_held()
    {
        Assert.False(Press(LCtrl));

        _engine.OnAdminWindowInFront(false);
        _engine.OnAdminWindowInFront(false);

        Assert.True(ModifierTracker.Ctrl);
    }
}
