using System.Collections.Generic;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>Loops playing on the target window. make e2e CATEGORY=Loops</summary>
[Trait("Category", "Loops")]
public class LoopTests
{
    [Fact]
    public void Each_row_presses_its_key_or_button_at_its_own_interval()
    {
        LoopDefinition loop = Loop("E2E rows", Vk.F13,
            Every(Vk.F15, 500),
            Every(Vk.F16, 1, IntervalUnit.Seconds),
            Every(Vk.LButton, 700));
        using var app = new AppSession(seed => seed.Settings.Loops.Add(loop));
        using var target = new TargetWindow();
        Point cursor = TargetWindow.PointAt(0.5, 0.5);
        Mouse.MoveTo(cursor); // a loop's click is where the cursor is

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.F16).Count >= 4, 8, "the loop did not run");
        target.Tap(Vk.F13);
        AssertStopped(target, Vk.F15, "the 500 ms row");
        AssertStopped(target, Vk.F16, "the 1 s row");
        AssertStopped(target, Vk.LButton, "the click row");

        foreach (double gap in Gaps(target.Downs(Vk.F15)))
            About(gap, 500, "500 ms row");
        foreach (double gap in Gaps(target.Downs(Vk.F16)))
            About(gap, 1000, "1 s row");
        List<TargetEvent> clicks = target.Downs(Vk.LButton);
        Assert.True(clicks.Count >= 3, $"{clicks.Count} clicks");
        foreach (double gap in Gaps(clicks))
            About(gap, 700, "700 ms click row");
        foreach (TargetEvent click in clicks)
            Near(cursor, click.Position, "loop click");
        Assert.Empty(target.Downs(Vk.F13));
    }

    [Fact]
    public void Hold_down_rows_hold_until_the_loop_stops_even_when_the_user_presses_that_key()
    {
        LoopDefinition loop = Loop("E2E hold down", Vk.F13,
            new LoopAction { Type = ActionType.Key, KeyVk = Vk.F15, HoldDown = true },
            new LoopAction { Type = ActionType.Key, KeyVk = Vk.MButton, HoldDown = true });
        using var app = new AppSession(seed => seed.Settings.Loops.Add(loop));
        using var target = new TargetWindow();
        Mouse.MoveTo(TargetWindow.PointAt(0.5, 0.5));

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.F15).Count == 1 && t.Downs(Vk.MButton).Count == 1, 5, "the loop did not hold its keys");

        // The user presses and releases the held key: the release must not let go of the loop's key.
        target.Tap(Vk.F15);
        TargetWindow.Quiet(1);
        Assert.Single(target.Downs(Vk.F15));
        Assert.Empty(target.Ups(Vk.F15));
        Assert.Empty(target.Ups(Vk.MButton));

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F15).Count == 1 && t.Ups(Vk.MButton).Count == 1, 5, "stopping the loop did not release its keys");
        Assert.True(target.Ups(Vk.F15)[0].Ms - target.Downs(Vk.F15)[0].Ms >= 1000, "F15 was not held for the whole time");
    }

    [Fact]
    public void Text_rows_paste_their_text_with_enter_before_or_after_and_keep_the_clipboard()
    {
        LoopDefinition after = Loop("E2E text after", Vk.F13,
            new LoopAction { Type = ActionType.Text, Text = "abc", EnterAfter = true, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds });
        LoopDefinition before = Loop("E2E text before", Vk.F14,
            new LoopAction { Type = ActionType.Text, Text = "xyz", EnterBefore = true, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds });
        using var app = new AppSession(seed => seed.Settings.Loops.AddRange(new[] { after, before }));
        using var target = new TargetWindow();
        target.ClipboardText = "E2E clipboard before";
        target.Text = "";

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.Return).Count >= 3, 6, "the text row did not paste");
        target.Tap(Vk.F13);
        AssertStopped(target, Vk.Return, "the text loop");
        int pastes = target.Downs(Vk.Return).Count;
        Assert.Equal(string.Concat(System.Linq.Enumerable.Repeat("abc\n", pastes)), target.Text.Replace("\r\n", "\n"));

        target.Clear();
        target.Text = "";
        target.Tap(Vk.F14);
        target.WaitFor(t => t.Downs(Vk.Return).Count >= 2, 6, "the text row did not paste");
        target.Tap(Vk.F14);
        AssertStopped(target, Vk.Return, "the text loop");
        pastes = target.Downs(Vk.Return).Count;
        Assert.Equal(string.Concat(System.Linq.Enumerable.Repeat("\nxyz", pastes)), target.Text.Replace("\r\n", "\n"));

        Assert.True(FlaUI.Core.Tools.Retry.WhileFalse(() => target.ClipboardText == "E2E clipboard before", AppSession.Timeout).Success,
            $"the clipboard was not restored: \"{target.ClipboardText}\"");
    }

    [Fact]
    public void Loops_start_and_stop_with_Toggle_Hold_the_card_and_Stop_all_and_a_disabled_one_never_starts()
    {
        LoopDefinition toggle = Loop("E2E toggle", Vk.F13, Every(Vk.F15, 300));
        LoopDefinition hold = Loop("E2E hold", Vk.F14, Every(Vk.F16, 300));
        hold.Mode = ActivationMode.Hold;
        LoopDefinition disabled = Loop("E2E disabled", Vk.F23, Every(Vk.F17, 300));
        disabled.Enabled = false;
        LoopDefinition played = Loop("E2E played", null, Every(Vk.F22, 300));
        using var app = new AppSession(seed => seed.Settings.Loops.AddRange(new[] { toggle, hold, disabled, played }));
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 3, 5, "the Toggle loop did not start");
        target.Tap(Vk.F13);
        AssertStopped(target, Vk.F15, "the Toggle loop");

        target.Press(Vk.F14);
        target.WaitFor(t => t.Downs(Vk.F16).Count >= 3, 5, "the Hold loop did not run while its hotkey was held");
        target.Release(Vk.F14);
        AssertStopped(target, Vk.F16, "the Hold loop");

        target.Tap(Vk.F23);
        target.WaitFor(t => t.Ups(Vk.F23).Count == 1, 5, "the disabled loop's hotkey did not reach the window");
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F17));

        // The card's Play and Stop, Stop all, the Stop all hotkey. A press sent while PuppyMacro is in
        // front after the click goes to it (F22 does nothing there); the target counts the next ones.
        PressCard(app, target, played.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F22).Count >= 2, 5, "Play did not start the loop");
        PressCard(app, target, played.Name, "Stop");
        AssertStopped(target, Vk.F22, "the loop stopped with its card");

        PressCard(app, target, played.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F22).Count >= 2, 5, "Play did not start the loop");
        InvokeInMainWindow(target, app.FindById(app.MainWindow, "StopAllButton"));
        AssertStopped(target, Vk.F22, "the loop stopped with Stop all");

        PressCard(app, target, played.Name, "Play");
        target.WaitFor(t => t.Downs(Vk.F22).Count >= 2, 5, "Play did not start the loop");
        target.Tap(Vk.F19);
        AssertStopped(target, Vk.F22, "the loop stopped with the Stop all hotkey");

        Assert.Empty(target.Downs(Vk.F13));
        Assert.Empty(target.Downs(Vk.F14));
        Assert.Empty(target.Downs(Vk.F19));
    }

    [Fact]
    public void A_loop_for_an_app_runs_only_there_and_wins_over_the_same_hotkey_for_all_apps()
    {
        string thisApp = System.IO.Path.GetFileName(System.Environment.ProcessPath)!; // the target window's process
        LoopDefinition elsewhere = Loop("E2E other app", Vk.F13, Every(Vk.F15, 300));
        elsewhere.AppExe = "notepad.exe";
        LoopDefinition here = Loop("E2E this app", Vk.F14, Every(Vk.F16, 300));
        here.AppExe = thisApp;
        LoopDefinition everywhere = Loop("E2E all apps", Vk.F14, Every(Vk.F17, 300));
        using var app = new AppSession(seed => seed.Settings.Loops.AddRange(new[] { elsewhere, here, everywhere }));
        using var target = new TargetWindow();

        // The hotkey of a loop for another app reaches the window, and that loop never starts.
        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F13).Count == 1, 5, "F13 (a loop's hotkey in notepad.exe only) did not reach the window");
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.F15));

        // F14 is the hotkey of a loop for this app and of one for all apps: this app's one starts.
        target.Tap(Vk.F14);
        target.WaitFor(t => t.Downs(Vk.F16).Count >= 3, 5, $"the loop for {thisApp} did not start");
        target.Tap(Vk.F14);
        AssertStopped(target, Vk.F16, $"the loop for {thisApp}");
        Assert.Empty(target.Downs(Vk.F17));
        Assert.Empty(target.Downs(Vk.F14));
    }
}
