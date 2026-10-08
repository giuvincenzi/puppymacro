using System;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

// Drives LoopEngine's input decisions directly (no hooks). The only key the loops send is F24, which
// no keyboard has. ModifierTracker is static: same collection as its own tests, so they never overlap.
[Collection(nameof(ModifierTracker))]
public sealed class LoopEngineTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, LCtrl = 0xA2;

    private readonly LoopEngine _engine;

    public LoopEngineTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _engine.StopAll();
        _engine.Dispose();
        ModifierTracker.Reset();
    }

    private static LoopDefinition HoldDownLoop(int vk) => new()
    {
        Name = "Hold",
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = vk, HoldDown = true } },
    };

    private static LoopDefinition TapLoop(HotkeyBinding hotkey, ActivationMode mode = ActivationMode.Toggle) => new()
    {
        Name = "Tap",
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = F24, IntervalValue = 10, IntervalUnit = IntervalUnit.Seconds } },
        Hotkey = hotkey,
        Mode = mode,
    };

    /// <summary>Runs what the engine queued on the dispatcher (capture results).</summary>
    private static void RunDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void A_physical_press_or_release_of_a_key_held_by_a_loop_is_blocked()
    {
        LoopDefinition loop = HoldDownLoop(F24);
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        _engine.StartLoop(loop);

        // Released before any press (it was down when the loop started): blocked.
        Assert.True(_engine.OnKey(F24, isDown: false, physicalModifierEvent: true));
        // Pressed and released again: both blocked, the loop's key stays down.
        Assert.True(_engine.OnKey(F24, isDown: true, physicalModifierEvent: true));
        Assert.True(_engine.OnKey(F24, isDown: false, physicalModifierEvent: true));
        Assert.True(_engine.IsRunning(loop.Id));
        // Other keys are not affected.
        Assert.False(_engine.OnKey(F23, isDown: true, physicalModifierEvent: true));
        Assert.False(_engine.OnKey(F23, isDown: false, physicalModifierEvent: true));

        _engine.StopAll();

        Assert.False(_engine.OnKey(F24, isDown: true, physicalModifierEvent: true));
        Assert.False(_engine.OnKey(F24, isDown: false, physicalModifierEvent: true));
    }

    [Fact]
    public void A_wheel_hotkey_toggles_once_per_notch_and_smaller_steps_add_up()
    {
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true });
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        ModifierTracker.Update(LCtrl, isDown: true);

        Assert.True(_engine.OnWheel(horizontal: false, delta: -KeyNames.WheelDelta));
        Assert.True(_engine.IsRunning(loop.Id));

        // Half a notch: blocked (no zoom or scroll in the app) but not a press yet.
        Assert.True(_engine.OnWheel(horizontal: false, delta: -60));
        Assert.True(_engine.IsRunning(loop.Id));
        Assert.True(_engine.OnWheel(horizontal: false, delta: -60));
        Assert.False(_engine.IsRunning(loop.Id));

        // The other direction is not this hotkey: it scrolls.
        Assert.False(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
    }

    [Fact]
    public void The_wheel_without_the_modifier_scrolls()
    {
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true });
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Assert.False(_engine.OnWheel(horizontal: false, delta: -KeyNames.WheelDelta));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void A_wheel_hotkey_never_starts_a_Hold_loop()
    {
        // HotkeyRules does not allow it; a hand-edited file must not start a loop that never stops.
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_UP, Ctrl = true }, ActivationMode.Hold);
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        ModifierTracker.Update(LCtrl, isDown: true);

        Assert.False(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void Hotkey_capture_takes_click_Esc_and_wheel_only_with_a_modifier()
    {
        HotkeyBinding? captured = null;
        bool cancelled = false;
        void Begin() => _engine.BeginCapture(b => captured = b, () => cancelled = true, allowPrimaryMouse: false, hotkey: true);

        Begin();
        // Alone: the click passes (the UI stays clickable) and the wheel scrolls.
        Assert.False(_engine.OnKey(KeyNames.VK_LBUTTON, isDown: true, physicalModifierEvent: true));
        Assert.False(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
        // Esc alone cancels.
        Assert.True(_engine.OnKey(KeyNames.VK_ESCAPE, isDown: true, physicalModifierEvent: true));
        _engine.OnKey(KeyNames.VK_ESCAPE, isDown: false, physicalModifierEvent: true);
        RunDispatcher();
        Assert.True(cancelled);
        Assert.Null(captured);

        ModifierTracker.Update(LCtrl, isDown: true);

        Begin();
        Assert.True(_engine.OnKey(KeyNames.VK_LBUTTON, isDown: true, physicalModifierEvent: true));
        Assert.True(_engine.OnKey(KeyNames.VK_LBUTTON, isDown: false, physicalModifierEvent: true));
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_LBUTTON, Ctrl = true }));

        Begin();
        Assert.True(_engine.OnKey(KeyNames.VK_ESCAPE, isDown: true, physicalModifierEvent: true));
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_ESCAPE, Ctrl = true }));

        Begin();
        Assert.True(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_UP, Ctrl = true }));
    }

    [Fact]
    public void Key_capture_for_actions_keeps_Esc_as_cancel_and_ignores_the_wheel()
    {
        bool cancelled = false;
        ModifierTracker.Update(LCtrl, isDown: true);
        _engine.BeginCapture(_ => { }, () => cancelled = true, allowPrimaryMouse: true);

        Assert.False(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
        Assert.True(_engine.OnKey(KeyNames.VK_ESCAPE, isDown: true, physicalModifierEvent: true));
        RunDispatcher();
        Assert.True(cancelled);
    }
}
