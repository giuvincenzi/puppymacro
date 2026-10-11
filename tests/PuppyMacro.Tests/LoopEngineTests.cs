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
    private const int F24 = 0x87, F23 = 0x86, LCtrl = 0xA2, RCtrl = 0xA3, LShift = 0xA0;

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
    public void A_hotkey_for_an_app_works_only_while_that_app_is_in_front()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        loop.AppExe = "Game.exe";
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        // Another app in front: the key passes to it and nothing starts.
        _engine.OnForegroundApp("other.exe");
        Assert.False(_engine.OnKey(F23, isDown: true, physicalModifierEvent: true));
        Assert.False(_engine.OnKey(F23, isDown: false, physicalModifierEvent: true));
        Assert.False(_engine.IsRunning(loop.Id));

        // The app in front (any case): the hotkey works.
        _engine.OnForegroundApp("game.exe");
        Assert.True(_engine.OnKey(F23, isDown: true, physicalModifierEvent: true));
        Assert.True(_engine.OnKey(F23, isDown: false, physicalModifierEvent: true));
        Assert.True(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void The_hotkey_of_the_app_in_front_wins_over_the_same_hotkey_for_all_apps()
    {
        LoopDefinition forAll = TapLoop(HotkeyBinding.FromKey(F23));
        var forGame = new MacroDefinition
        {
            Name = "Game",
            Hotkey = HotkeyBinding.FromKey(F23),
            AppExe = "Game.exe",
            Repeat = RepeatMode.Loop,
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = F24, DelayMs = 10000 } },
        };
        _engine.Publish(new EngineSnapshot { Loops = new[] { forAll }, Macros = new[] { forGame } });

        _engine.OnForegroundApp("Game.exe");
        Assert.True(_engine.OnKey(F23, isDown: true, physicalModifierEvent: true));
        Assert.True(_engine.OnKey(F23, isDown: false, physicalModifierEvent: true));
        Assert.True(_engine.IsRunning(forGame.Id));
        Assert.False(_engine.IsRunning(forAll.Id));

        _engine.OnForegroundApp("other.exe"); // stops the game's macro
        Assert.True(_engine.OnKey(F23, isDown: true, physicalModifierEvent: true));
        Assert.True(_engine.OnKey(F23, isDown: false, physicalModifierEvent: true));
        Assert.True(_engine.IsRunning(forAll.Id));
        Assert.False(_engine.IsRunning(forGame.Id));
    }

    [Fact]
    public void When_another_app_comes_in_front_only_the_items_for_the_app_that_left_stop()
    {
        LoopDefinition forAll = TapLoop(HotkeyBinding.FromKey(F23));
        LoopDefinition forGame = TapLoop(HotkeyBinding.FromKey(F24));
        forGame.AppExe = "Game.exe";
        _engine.Publish(new EngineSnapshot { Loops = new[] { forAll, forGame } });
        _engine.OnForegroundApp("Game.exe");
        _engine.StartLoop(forAll);
        _engine.StartLoop(forGame);

        // The same app again (a window of its own, another case): nothing stops.
        _engine.OnForegroundApp("GAME.EXE");
        Assert.True(_engine.IsRunning(forGame.Id));

        _engine.OnForegroundApp("other.exe");
        Assert.False(_engine.IsRunning(forGame.Id));
        Assert.True(_engine.IsRunning(forAll.Id));
        Assert.Equal("other.exe", _engine.ForegroundApp);
    }

    [Fact]
    public void A_click_on_the_overlay_panel_Stop_all_stops_everything_and_never_reaches_the_app()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        _engine.StartLoop(loop);
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 200, 130, PanelTarget.StopAllId) });

        // A click elsewhere passes.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 50, 50, 0));
        Assert.True(_engine.IsRunning(loop.Id));

        // On Stop all: blocked, down and up, and the loop stops.
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 150, 115, 0));
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 150, 115, 0));
        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void A_right_click_on_an_overlay_item_asks_to_enable_or_disable_it_and_never_reaches_the_app()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        var requested = new System.Collections.Generic.List<Guid>();
        _engine.OverlayEnableToggleRequested += requested.Add;
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 200, 130, loop.Id) });

        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_RBUTTON, 150, 115, 0));
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_RBUTTON, 150, 115, 0));
        RunDispatcher();

        Assert.Equal(new[] { loop.Id }, requested);
        Assert.False(_engine.IsRunning(loop.Id)); // a right click never starts it
        // Elsewhere a right click passes.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_RBUTTON, 50, 50, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_RBUTTON, 50, 50, 0));
    }

    [Fact]
    public void The_overlay_panel_exit_button_leaves_overlay_mode_and_the_open_button_also_opens_the_window()
    {
        int toggles = 0, opens = 0;
        _engine.OverlayModeToggleRequested += () => toggles++;
        _engine.OverlayExitAndOpenRequested += () => opens++;
        _engine.SetPanelTargets(new[]
        {
            new PanelTarget(100, 100, 126, 126, PanelTarget.ExitOverlayId),
            new PanelTarget(132, 100, 158, 126, PanelTarget.OpenMainOverlayId),
        });

        // Exit: as the Overlay mode hotkey. Blocked, down and up.
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 110, 110, 0));
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 110, 110, 0));
        RunDispatcher();
        Assert.Equal((1, 0), (toggles, opens));

        // Open: its own request. Blocked too.
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 140, 110, 0));
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 140, 110, 0));
        RunDispatcher();
        Assert.Equal((1, 1), (toggles, opens));

        // A right click on them reaches the app and does nothing.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_RBUTTON, 140, 110, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_RBUTTON, 140, 110, 0));
        RunDispatcher();
        Assert.Equal((1, 1), (toggles, opens));
    }

    [Fact]
    public void A_right_click_on_the_overlay_panel_buttons_reaches_the_app()
    {
        var requested = new System.Collections.Generic.List<Guid>();
        _engine.OverlayEnableToggleRequested += requested.Add;
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 200, 130, PanelTarget.StopAllId) });

        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_RBUTTON, 150, 115, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_RBUTTON, 150, 115, 0));
        RunDispatcher();

        Assert.Empty(requested);
    }

    [Fact]
    public void A_left_click_on_a_disabled_overlay_item_does_nothing_and_never_reaches_the_app()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        loop.Enabled = false;
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 200, 130, loop.Id, Startable: false) });

        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 150, 115, 0));
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 150, 115, 0));

        Assert.False(_engine.IsRunning(loop.Id));
    }

    [Fact]
    public void With_clicks_passing_through_an_overlay_item_acts_and_the_app_gets_the_click_too()
    {
        LoopDefinition loop = TapLoop(HotkeyBinding.FromKey(F23));
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });
        var requested = new System.Collections.Generic.List<Guid>();
        _engine.OverlayEnableToggleRequested += requested.Add;
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 200, 130, loop.Id, PassThrough: true) });

        // Left: starts it, and neither the press nor the release is blocked.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 150, 115, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 150, 115, 0));
        Assert.True(_engine.IsRunning(loop.Id));

        // Right: asks to enable or disable it, not blocked either.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_RBUTTON, 150, 115, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_RBUTTON, 150, 115, 0));
        RunDispatcher();
        Assert.Equal(new[] { loop.Id }, requested);
    }

    [Fact]
    public void Dragging_the_overlay_panel_move_handle_moves_it_and_the_press_never_reaches_the_app()
    {
        var events = new System.Collections.Generic.List<(PanelDragPhase Phase, int Dx, int Dy)>();
        _engine.OverlayPanelDragged += (phase, dx, dy) => events.Add((phase, dx, dy));
        _engine.SetPanelTargets(new[] { new PanelTarget(100, 100, 126, 126, PanelTarget.MoveOverlayId) });

        // Pressed on the handle: blocked, the app keeps the focus.
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 110, 110, 0));
        // Moves are never blocked (the cursor must move); several in a row reach the UI as the latest offset.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.Move, 0, 130, 120, 0));
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.Move, 0, 150, 125, 0));
        RunDispatcher();
        // Released: blocked too, with the final offset.
        Assert.True(_engine.OnMouseDetail(InputHook.MouseKind.ButtonUp, KeyNames.VK_LBUTTON, 160, 130, 0));
        RunDispatcher();

        Assert.Equal(new[] { (PanelDragPhase.Started, 0, 0), (PanelDragPhase.Moved, 40, 15), (PanelDragPhase.Ended, 50, 20) }, events);

        // After the drag a click elsewhere passes again.
        Assert.False(_engine.OnMouseDetail(InputHook.MouseKind.ButtonDown, KeyNames.VK_LBUTTON, 50, 50, 0));
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
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_LBUTTON, Ctrl = true, CtrlSide = ModifierSide.Left }));

        Begin();
        Assert.True(_engine.OnKey(KeyNames.VK_ESCAPE, isDown: true, physicalModifierEvent: true));
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_ESCAPE, Ctrl = true, CtrlSide = ModifierSide.Left }));

        Begin();
        Assert.True(_engine.OnWheel(horizontal: false, delta: KeyNames.WheelDelta));
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = KeyNames.VK_WHEEL_UP, Ctrl = true, CtrlSide = ModifierSide.Left }));
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

    // ---- Remaps and modifier sides (remap targets: F24 only) ----

    private static RemapDefinition Remap(HotkeyBinding source) => new() { Source = source, Target = HotkeyBinding.FromKey(F24) };

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    [Fact]
    public void A_remap_from_a_combination_works_only_with_its_modifiers_on_its_side()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(new HotkeyBinding { Vk = F23, Ctrl = true, CtrlSide = ModifierSide.Left }) } });

        Assert.False(Press(F23));
        Assert.False(Release(F23));

        Assert.False(Press(RCtrl));
        Assert.False(Press(F23));      // Right Ctrl: not this remap
        Assert.False(Release(F23));
        Assert.False(Release(RCtrl));

        Assert.False(Press(LCtrl));    // a modifier that is not a source passes
        Assert.True(Press(F23));
        Assert.True(Press(F23));       // auto-repeat
        Assert.True(Release(F23));
        Assert.False(Release(LCtrl));
    }

    [Fact]
    public void A_remap_of_a_key_alone_does_not_take_the_key_with_a_modifier()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(F23)) } });

        Press(LCtrl);
        Assert.False(Press(F23));
        Assert.False(Release(F23));
        Release(LCtrl);

        Assert.True(Press(F23));
        Assert.True(Release(F23));
    }

    [Fact]
    public void A_remapped_modifier_is_blocked_and_no_longer_counts_as_a_modifier()
    {
        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(RCtrl)) } });

        Assert.True(Press(RCtrl));
        Assert.True(Press(RCtrl));     // auto-repeat
        Assert.False(ModifierTracker.Ctrl);
        Assert.True(Release(RCtrl));

        // The other side stays a modifier.
        Assert.False(Press(LCtrl));
        Assert.True(ModifierTracker.Ctrl);
        Assert.False(Release(LCtrl));

        // With another modifier held it is not this source (exact match): it is a modifier.
        Press(LShift);
        Assert.False(Press(RCtrl));
        Assert.True(ModifierTracker.Ctrl);
        Release(RCtrl);
        Release(LShift);
    }

    [Fact]
    public void The_fake_Ctrl_of_AltGr_is_blocked_only_while_Right_Alt_is_remapped()
    {
        Assert.False(_engine.OnKey(LCtrl, isDown: true, physicalModifierEvent: false, altGrCtrl: true));
        Assert.False(_engine.OnKey(LCtrl, isDown: false, physicalModifierEvent: false, altGrCtrl: true));

        _engine.Publish(new EngineSnapshot { Remaps = new[] { Remap(HotkeyBinding.FromKey(KeyNames.VK_RMENU)) } });
        Assert.True(_engine.OnKey(LCtrl, isDown: true, physicalModifierEvent: false, altGrCtrl: true));
        Assert.True(_engine.OnKey(LCtrl, isDown: false, physicalModifierEvent: false, altGrCtrl: true));
        // A Ctrl sent by another program is never blocked, and changes nothing.
        Assert.False(_engine.OnKey(LCtrl, isDown: true, physicalModifierEvent: false));
        Assert.False(ModifierTracker.Ctrl);
        _engine.OnKey(LCtrl, isDown: false, physicalModifierEvent: false);
    }

    [Fact]
    public void A_capture_takes_a_modifier_alone_only_when_asked_and_keeps_the_side_of_a_combination()
    {
        HotkeyBinding? captured = null;
        _engine.BeginCapture(b => captured = b, () => { }, allowPrimaryMouse: false);
        Press(LCtrl);
        Release(LCtrl);
        RunDispatcher();
        Assert.Null(captured);
        _engine.CancelCapture();

        _engine.BeginCapture(b => captured = b, () => { }, allowPrimaryMouse: false, modifierAlone: true);
        // Two modifiers together are not a modifier alone.
        Press(LCtrl);
        Press(LShift);
        Release(LShift);
        Release(LCtrl);
        RunDispatcher();
        Assert.Null(captured);

        Assert.False(Press(RCtrl));
        Assert.False(Release(RCtrl));
        RunDispatcher();
        Assert.True(captured!.SameAs(HotkeyBinding.FromKey(RCtrl)));

        captured = null;
        _engine.BeginCapture(b => captured = b, () => { }, allowPrimaryMouse: false, modifierAlone: true);
        Press(LCtrl);
        Assert.True(Press(F23));
        Release(F23);
        Release(LCtrl);
        RunDispatcher();
        Assert.True(captured!.SameAs(new HotkeyBinding { Vk = F23, Ctrl = true, CtrlSide = ModifierSide.Left }));
    }

    [Fact]
    public void A_hotkey_on_one_side_starts_only_with_that_side()
    {
        LoopDefinition loop = TapLoop(new HotkeyBinding { Vk = F23, Ctrl = true, CtrlSide = ModifierSide.Left });
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        Press(RCtrl);
        Assert.False(Press(F23));
        Release(F23);
        Release(RCtrl);
        Assert.False(_engine.IsRunning(loop.Id));

        Press(LCtrl);
        Assert.True(Press(F23));
        Release(F23);
        Release(LCtrl);
        Assert.True(_engine.IsRunning(loop.Id));
    }
}
