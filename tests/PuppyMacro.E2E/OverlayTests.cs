using System;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>
/// Clicks in overlay mode over the target window: panel rows, the panel's Stop all, floating buttons.
/// The panel and the button are placed over the target window, so a click that is not taken by
/// PuppyMacro lands on it. make e2e CATEGORY=Overlay
/// </summary>
[Trait("Category", "Overlay")]
public class OverlayTests
{
    private const string PanelMacro = "E2E panel macro";
    private const string PanelLoop = "E2E panel loop";
    private const string ButtonMacro = "E2E button macro";

    private static void SeedOverlay(Seed seed, bool clickItemsInPanel)
    {
        // Positions in device-independent pixels, as the app saves them: inside the target window
        // at 100% to 200% display scale.
        seed.Settings.OverlayPanelX = 80;
        seed.Settings.OverlayPanelY = 80;
        seed.Settings.ClickItemsInPanel = clickItemsInPanel;
        seed.Settings.Loops.Add(Loop(PanelLoop, null, Every(Vk.F17, 300)));
        MacroDefinition panelMacro = Macro(PanelMacro, null, Press(Vk.F15, delayMs: 300));
        panelMacro.Repeat = RepeatMode.Loop;
        MacroDefinition buttonMacro = Macro(ButtonMacro, null, Press(Vk.F16));
        buttonMacro.FloatingButton = new FloatingButton { Enabled = true, X = 400, Y = 100 };
        seed.Macros.AddRange(new[] { panelMacro, buttonMacro });
    }

    /// <summary>
    /// The middle of a floating button's circle: its label. The window also holds the hotkey badge and room
    /// for the running halo, so its own middle is not the circle's.
    /// </summary>
    private static Point ButtonCenter(AppSession app, AutomationElement button) =>
        CenterOf(app, button, FloatingButton.DefaultLabel(ButtonMacro));

    /// <summary>Overlay mode on with its hotkey; returns the panel and the floating button.</summary>
    private static (AutomationElement Panel, AutomationElement Button) EnterOverlayMode(AppSession app, TargetWindow target)
    {
        target.Tap(Vk.F24);
        AutomationElement panel = app.TopWindow("PuppyMacro overlay panel") ?? throw new InvalidOperationException("no overlay panel");
        AutomationElement button = app.TopWindow($"PuppyMacro floating button: {ButtonMacro}") ?? throw new InvalidOperationException("no floating button");
        Assert.True(Retry.WhileFalse(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
        // The click targets are published every 300 ms.
        TargetWindow.Quiet(1);
        target.RequireForeground();
        return (panel, button);
    }

    private static Point CenterOf(AppSession app, AutomationElement root, string name) =>
        app.Find(root, name).BoundingRectangle.Center();

    [Fact]
    public void Panel_rows_the_panel_Stop_all_and_floating_buttons_run_and_stop_items_and_the_app_keeps_the_focus()
    {
        using var app = new AppSession(seed => SeedOverlay(seed, clickItemsInPanel: true));
        using var target = new TargetWindow();
        (AutomationElement panel, AutomationElement button) = EnterOverlayMode(app, target);

        target.Click(CenterOf(app, panel, PanelMacro));
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 3, 5, "clicking the macro's panel row did not start it");
        target.Click(CenterOf(app, panel, PanelLoop));
        target.WaitFor(t => t.Downs(Vk.F17).Count >= 3, 5, "clicking the loop's panel row did not start it");

        target.Click(CenterOf(app, panel, "Stop all"));
        AssertStopped(target, Vk.F15, "the macro after the panel's Stop all");
        AssertStopped(target, Vk.F17, "the loop after the panel's Stop all");

        target.Click(ButtonCenter(app, button));
        target.WaitFor(t => t.Ups(Vk.F16).Count == 1, 5, "clicking the floating button did not play its macro");
        TargetWindow.Quiet(1);
        Assert.Single(target.Downs(Vk.F16));

        // PuppyMacro took every click: none reached the window, which kept the focus.
        Assert.Empty(target.Downs(Vk.LButton));
        target.RequireForeground();

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }

    [Fact]
    public void With_Click_items_in_panel_off_a_click_on_a_row_reaches_the_window_and_floating_buttons_still_work()
    {
        using var app = new AppSession(seed => SeedOverlay(seed, clickItemsInPanel: false));
        using var target = new TargetWindow();
        (AutomationElement panel, AutomationElement button) = EnterOverlayMode(app, target);

        Point row = CenterOf(app, panel, PanelMacro);
        target.Click(row);
        target.WaitFor(t => t.Ups(Vk.LButton).Count == 1, 5, "the click on the row did not reach the window");
        Near(row, target.Downs(Vk.LButton)[0].Position, "click through the panel");
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F15));

        target.Click(ButtonCenter(app, button));
        target.WaitFor(t => t.Ups(Vk.F16).Count == 1, 5, "clicking the floating button did not play its macro");

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }
}
