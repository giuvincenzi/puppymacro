using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// LoopEngine and the auto-repeat of a key held down: Windows sends its press again and again (modifiers too) until it is
/// released. A press PuppyMacro blocks (a hotkey, the record hotkey, an AI request's Enter, a captured key, a remapped
/// modifier) must have its auto-repeat blocked as well and its release too: a repeat that passes gives the app a key down
/// whose release never comes (a stuck key), or runs something a second time. Only decisions are driven (OnKey, OnWheel);
/// the only key sent is F24 (a loop's press, a remap's target). ModifierTracker is static: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class LoopEngineAutoRepeatTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, F22 = 0x85, LCtrl = 0xA2, RCtrl = 0xA3, Enter = 0x0D;

    private readonly LoopEngine _engine;

    public LoopEngineAutoRepeatTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _engine.CancelCapture();
        _engine.StopRecording();
        _engine.DisarmRecording();
        _engine.StopReadyKeys();
        _engine.StopAll();
        _engine.Dispose(); // releases the targets of the remaps still held
        ModifierTracker.Reset();
    }

    private static LoopDefinition TapLoop(HotkeyBinding? hotkey, ActivationMode mode = ActivationMode.Toggle) => new()
    {
        Name = "Tap",
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = F24, IntervalValue = 10, IntervalUnit = IntervalUnit.Seconds } },
        Hotkey = hotkey,
        Mode = mode,
    };

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    private static void RunDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Arms a recording that waits for the record hotkey; the events come back on stop.</summary>
    private Func<List<RawEvent>?> ArmRecording()
    {
        List<RawEvent>? recorded = null;
        _engine.ArmRecording(() => false, () => { }, events => recorded = events);
        return () =>
        {
            RunDispatcher();
            return recorded;
        };
    }

    // ================= The record hotkey =================

    // ================= An AI request's ready keys =================

    // ================= Key capture =================

    // ================= A remapped modifier =================

    // ================= What works (passing) =================

    /// <summary>A Toggle hotkey with a modifier held down: its auto-repeat is blocked and never toggles it back, also once the modifier is released.</summary>
    [Fact]
    public void A_Toggle_hotkey_with_Ctrl_held_down_toggles_once_even_when_Ctrl_is_released_first()
    {
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = F23, Ctrl = true });
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Press(LCtrl);
        Assert.True(Press(F23));
        Assert.True(Press(F23));
        Release(LCtrl);
        Assert.True(Press(F23)); // repeat, now without Ctrl: still the hotkey's
        Assert.True(Release(F23));
        Assert.True(_engine.IsRunning(loop.Id));
    }

    /// <summary>A remapped key's auto-repeat is blocked (it repeats the target), and its release ends the remap.</summary>
    [Fact]
    public void A_remapped_keys_auto_repeat_is_blocked_and_its_release_ends_the_remap()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { new RemapDefinition { Source = HotkeyBinding.FromKey(F23), Target = HotkeyBinding.FromKey(F24) } } });

        Assert.True(Press(F23));
        Assert.True(Press(F23));
        Assert.True(Press(F23));
        Assert.True(Release(F23));

        // Disabled (a new snapshot without it): the key is the app's again.
        _engine.Publish(new EngineSnapshot());
        Assert.False(Press(F23));
        Assert.False(Release(F23));
    }

    /// <summary>The wheel's small steps add up per direction: a step the other way starts again from zero.</summary>
    [Fact]
    public void Wheel_steps_in_the_other_direction_start_counting_again()
    {
        LoopDefinition up = TapLoop(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_UP, Ctrl = true });
        LoopDefinition down = TapLoop(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true });
        _engine.Publish(new EngineSnapshot { Loops = new[] { up, down } });
        ModifierTracker.Update(LCtrl, isDown: true);

        Assert.True(_engine.OnWheel(horizontal: false, delta: 60));   // half a notch up
        Assert.True(_engine.OnWheel(horizontal: false, delta: -60));  // half a notch down: up's half is dropped
        Assert.False(_engine.IsRunning(up.Id));
        Assert.False(_engine.IsRunning(down.Id));
        Assert.True(_engine.OnWheel(horizontal: false, delta: -60));  // the second half down: one notch
        Assert.True(_engine.IsRunning(down.Id));
        Assert.False(_engine.IsRunning(up.Id));
    }

    /// <summary>A Hold loop for an app stops when another app comes in front, and its hotkey's release is still blocked.</summary>
    [Fact]
    public void A_Hold_loop_for_an_app_stops_when_another_app_comes_in_front_and_its_release_stays_blocked()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23), ActivationMode.Hold);
        loop.AppExe = "Game.exe";
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        _engine.OnForegroundApp("Game.exe");

        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));
        _engine.OnForegroundApp("other.exe");
        Assert.False(_engine.IsRunning(loop.Id));
        Assert.True(Press(F23));   // auto-repeat: still the hotkey's
        Assert.True(Release(F23)); // its press was blocked: so is its release
        Assert.False(_engine.IsRunning(loop.Id));

        // In the other app the key is the app's.
        Assert.False(Press(F23));
        Assert.False(Release(F23));
    }
}
