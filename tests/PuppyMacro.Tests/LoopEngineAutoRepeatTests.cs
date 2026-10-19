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

    /// <summary>
    /// The record hotkey (F23) starts an armed recording. Held a little longer than a quick tap (past the keyboard's repeat
    /// delay, about half a second), its auto-repeat arrives while the recording runs: OnKey's recording branch matches
    /// the record hotkey again (it has no _heldHotkeys check, unlike the loops' and macros' hotkeys) and stops the
    /// recording it has just started, with nothing in it.
    /// </summary>
    [Fact]
    public void The_record_hotkeys_auto_repeat_does_not_stop_the_recording_it_just_started()
    {
        _engine.Publish(new EngineSnapshot { Record = HotkeyBinding.FromKey(F23) });
        ArmRecording();

        Assert.True(Press(F23)); // starts the recording
        Assert.True(_engine.IsRecording);
        Press(F23);              // the same key still held: its auto-repeat
        Press(F23);

        Assert.True(_engine.IsRecording, "the auto-repeat of the record hotkey stopped the recording it had just started");
        Assert.True(Release(F23));
    }

    /// <summary>
    /// The record hotkey held to stop a recording: its press is blocked (and its release, _swallowUp), but once the
    /// recording has stopped nothing takes the key any more, so its auto-repeat goes through the normal routing and
    /// reaches the app, which then never gets the release: F23 stays down in the app.
    /// </summary>
    [Fact]
    public void The_record_hotkeys_auto_repeat_after_stopping_never_reaches_the_app_without_its_release()
    {
        _engine.Publish(new EngineSnapshot { Record = HotkeyBinding.FromKey(F23) });
        ArmRecording();
        _engine.StartRecordingNow();
        Assert.True(_engine.IsRecording);

        Assert.True(Press(F23)); // stops the recording
        Assert.False(_engine.IsRecording);
        bool repeatBlocked = Press(F23); // still held: its auto-repeat
        bool releaseBlocked = Release(F23);

        Assert.Equal(releaseBlocked, repeatBlocked);
        Assert.True(repeatBlocked, "the auto-repeat of the record hotkey reached the app, whose release was then blocked");
    }

    /// <summary>
    /// A record hotkey with Ctrl (Ctrl + F23): the user holds Ctrl a moment before pressing F23, long enough for Ctrl to
    /// repeat (any key repeats, modifiers too). Each repeat is recorded as one more Key down Ctrl; on stop, the loop that
    /// removes the record hotkey's own modifiers removes only the last one (_recordedDown holds Ctrl once), so the macro
    /// keeps a Key down Left Ctrl with no Key up, as the bug the test Stopping_a_recording_with_a_record_hotkey_with_Ctrl
    /// checks for a quick press: played, Ctrl stays down.
    /// </summary>
    [Fact]
    public void Holding_the_Ctrl_of_the_record_hotkey_until_it_repeats_leaves_no_Ctrl_held_in_the_macro()
    {
        _engine.Publish(new EngineSnapshot { Record = new HotkeyBinding { Vk = F23, Ctrl = true } });
        var stop = ArmRecording();
        _engine.StartRecordingNow();

        Press(F22);
        Release(F22);
        Press(LCtrl);
        Press(LCtrl);  // Ctrl's auto-repeat
        Press(LCtrl);
        Assert.True(Press(F23)); // Ctrl + F23 stops the recording
        Release(F23);
        Release(LCtrl);

        var actions = MacroBuilder.Build(stop()!);

        Assert.DoesNotContain(actions, a => a.Type == MacroActionType.KeyDown && a.Vk == LCtrl);
        Assert.Equal(new[] { (MacroActionType.PressKey, F22) }, actions.Select(a => (a.Type, a.Vk)));
    }

    /// <summary>
    /// An AI app's record_macro takes every monitor's whole picture at each key press (RecordedInput, MainWindow.Ai's
    /// Record: one full-screen Mat per monitor per event, kept until the recording ends). LoopEngine.Record raises
    /// RecordedInput for every KeyDown, auto-repeats included: a key held in a game (W to walk) for 10 seconds is about
    /// 300 KeyDowns, so about 300 screen copies per monitor (some 8 MB each at 1920 x 1080, 33 MB at 4K) kept in memory, and
    /// 300 "Key down W" inputs in the answer, for one press.
    /// </summary>
    [Fact]
    public void A_held_keys_auto_repeat_is_one_press_for_an_AI_apps_recording()
    {
        int presses = 0;
        void OnInput(RawEventKind kind, int vk, int x, int y, double t)
        {
            if (kind == RawEventKind.KeyDown && vk == F22)
                System.Threading.Interlocked.Increment(ref presses);
        }
        _engine.RecordedInput += OnInput;
        try
        {
            ArmRecording();
            _engine.StartRecordingNow();

            for (int i = 0; i < 10; i++)
                Press(F22); // held: the first press, then its auto-repeat
            Release(F22);
            _engine.StopRecording();

            // The events come on thread-pool threads: wait on this thread until they are in.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (System.Threading.Volatile.Read(ref presses) < 1 && clock.Elapsed < TimeSpan.FromSeconds(5))
                System.Threading.Thread.Sleep(10);
            System.Threading.Thread.Sleep(300);

            Assert.Equal(1, System.Threading.Volatile.Read(ref presses));
        }
        finally
        {
            _engine.RecordedInput -= OnInput;
        }
    }

    // ================= An AI request's ready keys =================

    /// <summary>
    /// An AI request waits for the user to be ready: Enter answers it and is blocked, its release too (_swallowUp). Held a
    /// moment, Enter repeats: the request is answered already, so the repeat passes to the app in front (the game), whose
    /// Enter release is then blocked: Enter stays down in the game (input.md: "Enter and Esc ... answer it once and are
    /// blocked (their release too)").
    /// </summary>
    [Fact]
    public void The_auto_repeat_of_the_Enter_that_answered_an_AI_request_never_reaches_the_app_without_its_release()
    {
        int ready = 0;
        _engine.WatchReadyKeys(() => ready++, () => { });

        Assert.True(Press(Enter));
        bool repeatBlocked = Press(Enter); // still held: its auto-repeat
        bool releaseBlocked = Release(Enter);
        RunDispatcher();

        Assert.Equal(1, ready);
        Assert.True(releaseBlocked);
        Assert.True(repeatBlocked, "Enter's auto-repeat reached the app, which never got its release");
    }

    // ================= Key capture =================

    /// <summary>
    /// A key field takes F23 (Settings' fields for the global hotkeys wait with hotkeys working, no editor open): the press
    /// is captured and blocked, its release too (_swallowUp). Its auto-repeat comes after the capture ended and goes
    /// through the normal routing: it runs a loop whose hotkey is F23 (here Toggle: it starts) and blocks it, or, with
    /// no hotkey on it, reaches the window in front with no release after it.
    /// </summary>
    [Fact]
    public void The_auto_repeat_of_a_captured_key_is_blocked_and_runs_no_hotkey()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        HotkeyBinding? captured = null;
        _engine.BeginCapture(b => captured = b, () => { }, allowPrimaryMouse: false, hotkey: true);

        Assert.True(Press(F23)); // captured
        Press(F23);              // its auto-repeat
        Press(F23);
        Assert.True(Release(F23));
        RunDispatcher();

        Assert.Equal(F23, captured?.Vk);
        Assert.False(_engine.IsRunning(loop.Id), "the auto-repeat of the key a field captured started the loop with that hotkey");
    }

    [Fact]
    public void The_auto_repeat_of_a_key_with_no_hotkey_after_its_capture_never_reaches_the_app_without_its_release()
    {
        _engine.BeginCapture(_ => { }, () => { }, allowPrimaryMouse: false);

        Assert.True(Press(F22)); // captured
        bool repeatBlocked = Press(F22);
        bool releaseBlocked = Release(F22);

        Assert.True(releaseBlocked);
        Assert.True(repeatBlocked, "the captured key's auto-repeat reached the app, which never got its release");
    }

    // ================= A remapped modifier =================

    /// <summary>
    /// Right Ctrl is remapped (to F24): held, it is blocked and never counts as a modifier (input.md). A key field starts
    /// waiting meanwhile (a field that waits as soon as it is shown, an AI request for a key): OnModifier skips the remap's
    /// auto-repeat branch while _captureDone is set, so the repeats of Right Ctrl pass to the app and enter ModifierTracker
    /// as a held Right Ctrl. Its release is the remap's (blocked, ModifierTracker not told): Right Ctrl stays held for
    /// PuppyMacro (every hotkey without Ctrl stops matching; every loop key "releases" Right Ctrl first) and down in the
    /// app, until a desktop switch.
    /// </summary>
    [Fact]
    public void A_remapped_modifier_held_while_a_key_field_starts_waiting_never_sticks_as_held()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { new RemapDefinition { Source = HotkeyBinding.FromKey(RCtrl), Target = HotkeyBinding.FromKey(F24) } } });

        Assert.True(Press(RCtrl)); // the remap: blocked, F24 down
        _engine.BeginCapture(_ => { }, () => { }, allowPrimaryMouse: false, modifierAlone: true);
        bool repeatBlocked = Press(RCtrl); // its auto-repeat
        Assert.True(Release(RCtrl));       // the remap's release
        _engine.CancelCapture();

        Assert.False(ModifierTracker.Ctrl, "Right Ctrl stayed held for PuppyMacro after the remapped key was released");
        Assert.True(repeatBlocked, "the remapped Right Ctrl's auto-repeat reached the app, which never got its release");
    }

    /// <summary>
    /// The same while a recording runs: the repeats of the remapped Right Ctrl are recorded as Key down Right Ctrl and
    /// enter ModifierTracker; the release is the remap's, so neither the recording nor ModifierTracker ever sees it.
    /// </summary>
    [Fact]
    public void A_remapped_modifier_held_while_a_recording_starts_never_sticks_as_held()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { new RemapDefinition { Source = HotkeyBinding.FromKey(RCtrl), Target = HotkeyBinding.FromKey(F24) } } });
        var stop = ArmRecording();

        Assert.True(Press(RCtrl)); // the remap: blocked, F24 down
        _engine.StartRecordingNow();
        Press(RCtrl);              // its auto-repeat
        Assert.True(Release(RCtrl));
        _engine.StopRecording();
        var actions = MacroBuilder.Build(stop()!);

        Assert.False(ModifierTracker.Ctrl, "Right Ctrl stayed held for PuppyMacro after the remapped key was released");
        Assert.DoesNotContain(actions, a => a.Type == MacroActionType.KeyDown && a.Vk == RCtrl);
    }

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
