using System;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
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

    private static void SeedOverlay(Seed seed, bool clickItemsInPanel, bool clicksPassThrough = false)
    {
        // Positions in device-independent pixels, as the app saves them: inside the target window
        // at 100% to 200% display scale.
        seed.Settings.OverlayPanelX = 80;
        seed.Settings.OverlayPanelY = 80;
        seed.Settings.ClickItemsInPanel = clickItemsInPanel;
        LoopDefinition panelLoop = Loop(PanelLoop, null, Every(Vk.F17, 300));
        panelLoop.OverlayClickPassesThrough = clicksPassThrough;
        seed.Settings.Loops.Add(panelLoop);
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

    [Fact]
    public void A_right_click_disables_and_enables_a_panel_row_and_a_floating_button_and_disabled_they_do_nothing()
    {
        using var app = new AppSession(seed => SeedOverlay(seed, clickItemsInPanel: true));
        using var target = new TargetWindow();
        (AutomationElement panel, AutomationElement button) = EnterOverlayMode(app, target);

        // Panel row: disabled, it shows so and a click does nothing.
        Point row = CenterOf(app, panel, PanelLoop);
        target.Click(row, MouseButton.Right);
        Assert.True(app.Has(panel, "Disabled"), "the right click did not disable the loop");
        TargetWindow.Quiet(1); // the click targets are published every 300 ms
        target.Click(row);
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F17));

        // Enabled again, a click starts it.
        target.Click(row, MouseButton.Right);
        Assert.True(Retry.WhileTrue(() => app.Has(panel, "Disabled"), AppSession.Timeout).Success, "the right click did not enable the loop");
        TargetWindow.Quiet(1);
        target.Click(row);
        target.WaitFor(t => t.Downs(Vk.F17).Count >= 2, 5, "clicking the enabled row did not start the loop");
        target.Click(CenterOf(app, panel, "Stop all"));
        AssertStopped(target, Vk.F17, "the loop after the panel's Stop all");

        // Floating button: the same.
        Point circle = ButtonCenter(app, button);
        target.Click(circle, MouseButton.Right);
        TargetWindow.Quiet(1);
        target.Click(circle);
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F16));
        target.Click(circle, MouseButton.Right);
        TargetWindow.Quiet(1);
        target.Click(circle);
        target.WaitFor(t => t.Ups(Vk.F16).Count == 1, 5, "clicking the enabled floating button did not play its macro");

        // PuppyMacro took every click.
        Assert.Empty(target.Downs(Vk.LButton));
        Assert.Empty(target.Downs(Vk.RButton));
        target.RequireForeground();

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }

    [Fact]
    public void With_clicks_passing_through_a_panel_row_acts_and_the_window_gets_the_click_too()
    {
        using var app = new AppSession(seed => SeedOverlay(seed, clickItemsInPanel: true, clicksPassThrough: true));
        using var target = new TargetWindow();
        (AutomationElement panel, _) = EnterOverlayMode(app, target);

        Point row = CenterOf(app, panel, PanelLoop);
        target.Click(row);
        target.WaitFor(t => t.Downs(Vk.F17).Count >= 2, 5, "clicking the row did not start the loop");
        target.WaitFor(t => t.Ups(Vk.LButton).Count == 1, 5, "the click on the row did not reach the window");
        Near(row, target.Downs(Vk.LButton)[0].Position, "click through the panel");
        target.Click(CenterOf(app, panel, "Stop all"));
        AssertStopped(target, Vk.F17, "the loop after the panel's Stop all");

        target.Click(row, MouseButton.Right);
        Assert.True(app.Has(panel, "Disabled"), "the right click did not disable the loop");
        target.WaitFor(t => t.Ups(Vk.RButton).Count == 1, 5, "the right click on the row did not reach the window");

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }

    [Fact]
    public void The_overlay_shows_disabled_items_unless_they_are_hidden_while_disabled_or_not_shown_in_the_overlay()
    {
        using var app = new AppSession(seed =>
        {
            SeedOverlay(seed, clickItemsInPanel: true);
            LoopDefinition disabled = Loop("E2E disabled loop", null, Every(Vk.F17, 300));
            disabled.Enabled = false;
            LoopDefinition hiddenWhileDisabled = Loop("E2E loop hidden while disabled", null, Every(Vk.F17, 300));
            hiddenWhileDisabled.Enabled = false;
            hiddenWhileDisabled.HideInOverlayWhenDisabled = true;
            LoopDefinition notShown = Loop("E2E loop not in the overlay", null, Every(Vk.F17, 300));
            notShown.ShowInOverlay = false;
            seed.Settings.Loops.AddRange(new[] { disabled, hiddenWhileDisabled, notShown });
            MacroDefinition buttonNotShown = Macro("E2E button not in the overlay", null, Press(Vk.F16));
            buttonNotShown.ShowInOverlay = false;
            buttonNotShown.FloatingButton = new FloatingButton { Enabled = true, X = 500, Y = 100 };
            seed.Macros.Add(buttonNotShown);
        });
        using var target = new TargetWindow();
        (AutomationElement panel, _) = EnterOverlayMode(app, target);

        Assert.True(app.Has(panel, "E2E disabled loop"));
        Assert.True(app.Has(panel, "Disabled"));
        Assert.False(app.Has(panel, "E2E loop hidden while disabled"));
        Assert.False(app.Has(panel, "E2E loop not in the overlay"));
        Assert.Null(app.TopWindow("PuppyMacro floating button: E2E button not in the overlay"));

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }

    [Fact]
    public void The_overlay_shows_only_the_items_for_all_apps_and_for_the_app_in_front()
    {
        string thisApp = System.IO.Path.GetFileName(Environment.ProcessPath)!; // the target window's process
        using var app = new AppSession(seed =>
        {
            SeedOverlay(seed, clickItemsInPanel: true);
            LoopDefinition here = Loop("E2E loop for this app", null, Every(Vk.F17, 300));
            here.AppExe = thisApp;
            LoopDefinition elsewhere = Loop("E2E loop for notepad", null, Every(Vk.F17, 300));
            elsewhere.AppExe = "notepad.exe";
            seed.Settings.Loops.AddRange(new[] { here, elsewhere });
            MacroDefinition elsewhereButton = Macro("E2E button for notepad", null, Press(Vk.F16));
            elsewhereButton.AppExe = "notepad.exe";
            elsewhereButton.FloatingButton = new FloatingButton { Enabled = true, X = 500, Y = 100 };
            seed.Macros.Add(elsewhereButton);
        });
        using var target = new TargetWindow();
        (AutomationElement panel, _) = EnterOverlayMode(app, target);

        Assert.True(app.Has(panel, PanelLoop));                  // all apps
        Assert.True(app.Has(panel, "E2E loop for this app"));
        Assert.False(app.Has(panel, "E2E loop for notepad"));
        AutomationElement? hidden = app.TopWindow("PuppyMacro floating button: E2E button for notepad");
        Assert.True(hidden == null || hidden.IsOffscreen, "the floating button for notepad.exe is shown");

        target.Tap(Vk.F24);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }
}
