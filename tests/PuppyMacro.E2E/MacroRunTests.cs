using System.Linq;
using FlaUI.Core.AutomationElements;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>How macros run: repeat, speed, Toggle and Hold, the ways to stop them. make e2e CATEGORY=Macros</summary>
[Trait("Category", "Macros")]
public class MacroRunTests
{
    [Fact]
    public void Speed_scales_the_delays_from_a_quarter_to_four_times()
    {
        double[] speeds = MacroDefinition.SpeedSteps;
        MacroDefinition[] macros = speeds.Select(speed =>
        {
            // A first delay of 1 s whatever the speed: time for the target window to come back to the front.
            MacroDefinition macro = Macro($"E2E speed {speed}", null, Press(Vk.F15, delayMs: 1000 * speed), Press(Vk.F16, delayMs: 800));
            macro.Speed = speed;
            return macro;
        }).ToArray();
        using var app = new AppSession(seed => seed.Macros.AddRange(macros));
        app.GoTo("Macros");
        using var target = new TargetWindow();

        foreach (MacroDefinition macro in macros)
        {
            PressCard(app, target, macro.Name, "Play");
            target.WaitFor(t => t.Ups(Vk.F16).Count == 1, 10, $"{macro.Name} did not finish");
            target.RequireForeground();
            // From F15 down to F16 down: its 30 ms press and the 800 ms delay, both scaled.
            double gap = target.Downs(Vk.F16)[0].Ms - target.Downs(Vk.F15)[0].Ms;
            About(gap, 830 / macro.Speed, $"speed {macro.Speed}");
            WaitIdle(app, macro.Name);
        }
    }

    [Fact]
    public void Toggle_starts_and_stops_with_its_hotkey_and_Hold_runs_only_while_the_hotkey_is_held()
    {
        MacroDefinition toggle = Macro("E2E toggle", Vk.F13, Press(Vk.F15, delayMs: 300));
        toggle.Repeat = RepeatMode.Loop;
        MacroDefinition hold = Macro("E2E hold", Vk.F14, Press(Vk.F16, delayMs: 300));
        hold.Repeat = RepeatMode.Loop;
        hold.Mode = ActivationMode.Hold;
        // Released during its long delay: the second F17 and F22 never come.
        MacroDefinition holdOnce = Macro("E2E hold once", Vk.F23, Press(Vk.F17), Press(Vk.F17, delayMs: 2000), Press(Vk.F22));
        holdOnce.Mode = ActivationMode.Hold;
        using var app = new AppSession(seed => seed.Macros.AddRange(new[] { toggle, hold, holdOnce }));
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 3, 5, "the Toggle macro did not start");
        target.Tap(Vk.F13);
        AssertStopped(target, Vk.F15, "the Toggle macro");

        target.Press(Vk.F14);
        target.WaitFor(t => t.Downs(Vk.F16).Count >= 3, 5, "the Hold macro did not run while its hotkey was held");
        target.Release(Vk.F14);
        AssertStopped(target, Vk.F16, "the Hold macro");

        target.Press(Vk.F23);
        target.WaitFor(t => t.Ups(Vk.F17).Count == 1, 5, "the Hold macro did not start");
        TargetWindow.Quiet(0.3);
        target.Release(Vk.F23);
        TargetWindow.Quiet(2.5);
        Assert.Single(target.Downs(Vk.F17));
        Assert.Empty(target.Downs(Vk.F22));

        Assert.Empty(target.Downs(Vk.F13));
        Assert.Empty(target.Downs(Vk.F14));
        Assert.Empty(target.Downs(Vk.F23));
    }

    [Fact]
    public void A_macro_stops_with_its_card_Stop_all_and_the_Stop_all_hotkey_releasing_its_keys_and_a_disabled_one_never_starts()
    {
        MacroDefinition runner = Macro("E2E runner", null, Press(Vk.F15, delayMs: 250));
        runner.Repeat = RepeatMode.Loop;
        // Key down F17, then a long wait: stopping it releases F17.
        MacroDefinition holder = Macro("E2E holder", null,
            new MacroAction { Type = MacroActionType.KeyDown, Vk = Vk.F17, DelayMs = 1000 }, Press(Vk.F16, delayMs: 5000));
        MacroDefinition disabled = Macro("E2E disabled", Vk.F14, Press(Vk.F16));
        disabled.Enabled = false;
        using var app = new AppSession(seed => seed.Macros.AddRange(new[] { runner, holder, disabled }));
        app.GoTo("Macros");
        using var target = new TargetWindow();

        // The card's Play, then its Stop.
        PressCard(app, target, runner.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 2, 5, "Play did not start the macro");
        PressCard(app, target, runner.Name, "Stop");
        AssertStopped(target, Vk.F15, "the macro stopped with its card");

        // Stop all in the main window.
        PressCard(app, target, runner.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 2, 5, "Play did not start the macro");
        InvokeInMainWindow(target, app.FindById(app.MainWindow, "StopAllButton"));
        AssertStopped(target, Vk.F15, "the macro stopped with Stop all");

        // The Stop all hotkey.
        PressCard(app, target, runner.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 2, 5, "Play did not start the macro");
        target.Tap(Vk.F19);
        AssertStopped(target, Vk.F15, "the macro stopped with the Stop all hotkey");
        Assert.Empty(target.Downs(Vk.F19));

        // Stopped while it holds a key: the key is released.
        PressCard(app, target, holder.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F17).Count == 1, 5, "the holder macro did not press its key");
        target.Tap(Vk.F19);
        target.WaitFor(t => t.Ups(Vk.F17).Count == 1, 5, "the key held by the macro was not released");
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F16));

        // A disabled macro's hotkey does nothing: the key reaches the window as any other key.
        target.Clear();
        target.Tap(Vk.F14);
        target.WaitFor(t => t.Ups(Vk.F14).Count == 1, 5, "the disabled macro's hotkey did not reach the window");
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F16));
    }
}
