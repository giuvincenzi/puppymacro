using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// LoopEngine's edge cases: Hold and Toggle hotkeys with their auto-repeat and release, a release that arrives while a key
/// field waits or a recording runs, the Stop all hotkey on the wheel while an editor is open, the overlay's click that
/// leaves overlay mode, and what a recording keeps (the record hotkey's modifiers, auto-repeat). The keys driven here are
/// only decisions (OnKey, OnWheel, OnMouseDetail); the only key sent is F24 (a loop's press, a remap's target).
/// ModifierTracker is static: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class LoopEngineEdgesTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, F22 = 0x85, LCtrl = 0xA2;

    private readonly LoopEngine _engine;

    public LoopEngineEdgesTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _engine.CancelCapture();
        _engine.StopRecording();
        _engine.DisarmRecording();
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

    private static RemapDefinition Remap(HotkeyBinding source) => new() { Source = source, Target = HotkeyBinding.FromKey(F24) };

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    /// <summary>Runs what the engine queued on the dispatcher.</summary>
    private static void RunDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Starts a recording at once (as the Start recording button after its countdown); the events come back on stop.</summary>
    private Func<List<RawEvent>?> StartRecording()
    {
        List<RawEvent>? recorded = null;
        _engine.ArmRecording(() => false, () => { }, events => recorded = events);
        _engine.StartRecordingNow();
        Assert.True(_engine.IsRecording);
        return () =>
        {
            RunDispatcher();
            return recorded;
        };
    }

    // ================= Hold and Toggle hotkeys =================

    [Fact]
    public void A_Hold_loop_runs_while_its_hotkey_is_held_and_its_auto_repeat_changes_nothing()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23), ActivationMode.Hold);
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));
        Assert.True(Press(F23)); // auto-repeat: blocked, still running
        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));

        Assert.True(Release(F23));
        Assert.False(_engine.IsRunning(loop.Id));

        // Pressed again: it starts again.
        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));
        Assert.True(Release(F23));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void A_Toggle_hotkey_held_down_toggles_once_and_its_auto_repeat_never_toggles_it_back()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Assert.True(Press(F23));
        Assert.True(Press(F23));
        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.True(_engine.IsRunning(loop.Id));

        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void A_Hold_loop_whose_hotkey_has_a_modifier_stops_on_the_key_release_even_after_the_modifier()
    {
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = F23, Ctrl = true }, ActivationMode.Hold);
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Press(LCtrl);
        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));
        Release(LCtrl);                // the modifier first: still running
        Assert.True(Press(F23));       // the key's auto-repeat, now without Ctrl: still blocked, still running
        Assert.True(_engine.IsRunning(loop.Id));
        Assert.True(Release(F23));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    /// <summary>
    /// A Hold loop runs while its hotkey is held. If a key field starts waiting for a key meanwhile (a field that waits as
    /// soon as it is shown: a new action, an AI app's request for a key), the hotkey's release goes to the capture
    /// (LoopEngine.OnKey → HandleCapture, which only looks at _swallowUp), so the loop never stops: it keeps pressing keys
    /// with nothing held, and the key stays in _heldHotkeys, so its next press is taken for an auto-repeat and blocked.
    /// </summary>
    [Fact]
    public void A_Hold_loop_stops_when_its_hotkey_is_released_while_a_key_field_waits_for_a_key()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23), ActivationMode.Hold);
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Assert.True(Press(F23));
        Assert.True(_engine.IsRunning(loop.Id));

        _engine.BeginCapture(_ => { }, () => { }, allowPrimaryMouse: false);
        Release(F23);

        Assert.False(_engine.IsRunning(loop.Id), "the Hold loop kept running after its hotkey was released");
    }

    /// <summary>
    /// A remapped key held (its target down) when a key field starts waiting for a key: the release goes to the capture
    /// (HandleCapture only looks at _swallowUp), so it is not the remap's: the app gets the source's release it never saw
    /// pressed, _activeRemaps keeps the key and the target is never released, a stuck key in the app until the source is
    /// pressed and released again. (The decision is checked, not the key's state: the state of F24 can change under the
    /// test from another test's loop thread.)
    /// </summary>
    [Fact]
    public void A_remapped_key_released_while_a_key_field_waits_still_releases_its_target()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(F23)) } });
        Assert.True(Press(F23)); // the remap: blocked, F24 down

        _engine.BeginCapture(_ => { }, () => { }, allowPrimaryMouse: false);
        bool blocked = Release(F23);
        _engine.CancelCapture();

        Assert.True(blocked, "the release of the remapped key reached the app, and the remap did not release its target");
        Assert.False(Release(F23), "the remap was still held after its source was released");
    }

    /// <summary>
    /// The same while a recording runs: OnKey's recording branch records the release and returns before the remaps, so the
    /// target held since before the recording stays down.
    /// </summary>
    [Fact]
    public void A_remapped_key_released_while_recording_still_releases_its_target()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(F23)) } });
        Assert.True(Press(F23)); // the remap: blocked, F24 down

        StartRecording();
        bool blocked = Release(F23);
        _engine.StopRecording();

        Assert.True(blocked, "the release of the remapped key reached the app, and the remap did not release its target");
        Assert.False(Release(F23), "the remap was still held after its source was released");
    }

    // ================= The wheel =================

    [Fact]
    public void The_Stop_all_hotkey_on_the_wheel_stops_everything()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F22));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop }, StopAll = new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true } });
        _engine.StartLoop(loop);
        ModifierTracker.Update(LCtrl, isDown: true);

        Assert.True(_engine.OnWheel(horizontal: false, delta: -KeyNames.WheelDelta));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    /// <summary>
    /// With an editor open (hotkeys suspended) the Stop all hotkey still stops what runs (input.md, and OnKey does it), but
    /// OnWheel returns at once while hotkeys are suspended: a Stop all set to Ctrl + wheel never stops an editor's Test.
    /// </summary>
    [Fact]
    public void The_Stop_all_hotkey_on_the_wheel_still_stops_what_runs_while_an_editor_is_open()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F22));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop }, StopAll = new HotkeyBinding { Vk = KeyNames.VK_WHEEL_DOWN, Ctrl = true } });
        _engine.StartLoop(loop);
        _engine.HotkeysSuspended = true;
        ModifierTracker.Update(LCtrl, isDown: true);

        bool blocked = _engine.OnWheel(horizontal: false, delta: -KeyNames.WheelDelta);

        Assert.False(_engine.IsRunning(loop.Id), "Stop all on the wheel did nothing while hotkeys were suspended");
        Assert.True(blocked);
    }

    // ================= The overlay =================

    /// <summary>
    /// A left click on the overlay panel's exit button: its press is blocked and _swallowLeftUp is set for its release. The
    /// UI leaves overlay mode at once (SetPanelTargets(null) removes the mouse detail hook) before the button comes up, so
    /// the release never reaches OnMouseDetail and _swallowLeftUp stays set. The next time overlay mode is on, the release
    /// of the first click anywhere is blocked: the app got the press only, and its left button stays down (a drag).
    /// </summary>
    [Fact]
    public void After_leaving_overlay_mode_with_its_exit_button_the_next_click_in_overlay_mode_is_not_cut_in_half()
    {
        var targets = new[] { new PanelTarget(100, 100, 126, 126, PanelTarget.ExitOverlayId) };
        _engine.SetPanelTargets(targets);
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 110, 110, 0));
        // The UI leaves overlay mode before the button is released: the release goes to the key handler only.
        _engine.SetPanelTargets(null);
        _engine.OnKey(KeyNames.VK_LBUTTON, isDown: false, physicalModifierEvent: true);

        // Later, overlay mode again: a click in the game, away from the panel.
        _engine.SetPanelTargets(targets);
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 500, 500, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 500, 500, 0));
    }

    // ================= Recording =================

    [Fact]
    public void A_recording_keeps_the_keys_pressed_in_order_and_not_the_record_hotkey()
    {
        _engine.Publish(new EngineSnapshot { Record = HotkeyBinding.FromKey(F23) });
        var stop = StartRecording();

        Press(F22);
        Release(F22);
        Assert.True(Press(F23));   // the record hotkey stops it, blocked
        Assert.True(Release(F23));

        var events = stop();
        Assert.NotNull(events);
        Assert.Equal(new[] { (RawEventKind.KeyDown, F22), (RawEventKind.KeyUp, F22) }, events!.Select(e => (e.Kind, e.Vk)));
        Assert.False(_engine.IsRecording);
    }

    /// <summary>
    /// A record hotkey with a modifier (Ctrl + F23): stopping the recording records the Ctrl pressed for it (OnModifier
    /// records every physical modifier) but not its release, which comes after the stop. The macro ends with a Key down
    /// Left Ctrl and no Key up: played with Repeat, Ctrl stays down for the next rounds, so every key it presses is
    /// Ctrl + that key (an "a" becomes Ctrl+A).
    /// </summary>
    [Fact]
    public void Stopping_a_recording_with_a_record_hotkey_with_Ctrl_leaves_no_Ctrl_held_in_the_macro()
    {
        _engine.Publish(new EngineSnapshot { Record = new HotkeyBinding { Vk = F23, Ctrl = true } });
        var stop = StartRecording();

        Press(F22);
        Release(F22);
        Press(LCtrl);
        Assert.True(Press(F23)); // Ctrl + F23 stops the recording
        Release(F23);
        Release(LCtrl);

        var actions = MacroBuilder.Build(stop()!);

        Assert.DoesNotContain(actions, a => a.Type == MacroActionType.KeyDown && a.Vk == LCtrl);
        Assert.Equal(new[] { (MacroActionType.PressKey, F22) }, actions.Select(a => (a.Type, a.Vk)));
    }

    /// <summary>
    /// A key held while recording: Windows repeats its press (auto-repeat) and OnKey records every one, so a key held for a
    /// second becomes about thirty Key down steps and a short Press key, not one Press key held a second (MacroBuilder.Held:
    /// a press held up to a minute is one Press key).
    /// </summary>
    [Fact]
    public void A_key_held_while_recording_is_one_press_not_its_auto_repeats()
    {
        var stop = StartRecording();

        Press(F22);
        Press(F22); // auto-repeat
        Press(F22);
        Press(F22);
        Release(F22);
        _engine.StopRecording();

        var actions = MacroBuilder.Build(stop()!);

        Assert.Equal(new[] { (MacroActionType.PressKey, F22) }, actions.Select(a => (a.Type, a.Vk)));
    }
}
