using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>Recording a macro on the target window and playing it back. make e2e CATEGORY=Recording</summary>
[Trait("Category", "Recording")]
public class RecordingTests
{
    [Fact]
    public void A_recording_of_keys_clicks_scrolls_and_moves_plays_back_the_same_on_the_window()
    {
        using var app = new AppSession(seed => { });
        app.GoTo("Macros");
        using var target = new TargetWindow();
        Point click = TargetWindow.PointAt(0.3, 0.7);

        StartRecording(app, target, recordMoves: true);
        target.MoveMouse(TargetWindow.PointAt(0.6, 0.5));
        // Recorded as a 1.5 s delay before F15: on playback, time for the target window to come back
        // to the front after Play.
        TargetWindow.Quiet(1.5);
        target.Tap(Vk.F15);
        target.Click(click);
        target.RequireForeground();
        Mouse.Scroll(-1);
        target.Tap(Vk.F16);
        target.Tap(Vk.F18); // the Record hotkey stops it and opens the new macro

        MacroDefinition macro = SaveRecordedMacro(app, "E2E recorded");
        List<MacroAction> actions = macro.Actions;
        int f15 = actions.FindIndex(a => a.Type == MacroActionType.PressKey && a.Vk == Vk.F15);
        int leftClick = actions.FindIndex(a => a.Type == MacroActionType.Click && a.Vk == Vk.LButton);
        int scroll = actions.FindIndex(a => a.Type == MacroActionType.Scroll && a.ScrollDirection == ScrollDirection.Down);
        int f16 = actions.FindIndex(a => a.Type == MacroActionType.PressKey && a.Vk == Vk.F16);
        string recorded = string.Join(", ", actions.Select(a => $"{a.Type} {a.Vk:X2}"));
        Assert.True(f15 >= 0 && leftClick > f15 && scroll > leftClick && f16 > scroll, $"recorded: {recorded}");
        Near(click, new Point(actions[leftClick].X, actions[leftClick].Y), "recorded click");
        Assert.Contains(actions, a => a.Type is MacroActionType.MoveTo or MacroActionType.MovePath);
        Assert.DoesNotContain(actions, a => a.Vk == Vk.F18); // the Record hotkey is not recorded

        // Played back on the window: the same keys, click and scroll, in the same order.
        target.Activate();
        app.GoTo("Macros");
        PressCard(app, target, "E2E recorded", "Play");
        target.WaitFor(t => t.Ups(Vk.F16).Count == 1, 15, "the recorded macro did not play");
        TargetEvent keyF15 = Assert.Single(target.Downs(Vk.F15));
        TargetEvent pressed = Assert.Single(target.Downs(Vk.LButton));
        TargetEvent wheel = Assert.Single(target.Of(TargetEventKind.Wheel));
        TargetEvent keyF16 = Assert.Single(target.Downs(Vk.F16));
        Near(click, pressed.Position, "played click");
        Assert.Equal(-120, wheel.Delta);
        Assert.True(keyF15.Ms < pressed.Ms && pressed.Ms < wheel.Ms && wheel.Ms < keyF16.Ms, $"order. Received: {target.Describe()}");
    }

    [Fact]
    public void Without_mouse_movement_only_keys_are_recorded_and_clicks_on_the_recording_bar_are_left_out()
    {
        using var app = new AppSession(seed => { });
        app.GoTo("Macros");
        using var target = new TargetWindow();

        StartRecording(app, target, recordMoves: false);
        target.MoveMouse(TargetWindow.PointAt(0.2, 0.6));
        target.MoveMouse(TargetWindow.PointAt(0.7, 0.8));
        target.Tap(Vk.F15);

        // Stop with a real click on the bar's Stop: PuppyMacro's own window, so the click is not recorded.
        Mouse.Click(BarButton(app).BoundingRectangle.Center());

        MacroDefinition macro = SaveRecordedMacro(app, "E2E keys only");
        MacroAction only = Assert.Single(macro.Actions);
        Assert.Equal(MacroActionType.PressKey, only.Type);
        Assert.Equal(Vk.F15, only.Vk);
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"RecordMouseMovement\": false"),
            AppSession.Timeout).Success, "the Record mouse movement choice was not kept");
    }

    [Fact]
    public void Cancel_in_the_Ready_to_record_window_or_during_the_countdown_records_nothing()
    {
        using var app = new AppSession(seed => { });
        app.GoTo("Macros");

        // Cancel in the window: the main window comes back.
        app.FindById(app.MainWindow, "RecordMacroButton").AsButton().Invoke();
        Window prompt = app.Dialog("Ready to record");
        app.FindButton(prompt, "Cancel").Invoke();
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");

        // Cancel on the bar during the countdown.
        app.FindById(app.MainWindow, "RecordMacroButton").AsButton().Invoke();
        prompt = app.Dialog("Ready to record");
        app.FindButton(prompt, "Start recording").Invoke();
        Mouse.Click(BarButton(app).BoundingRectangle.Center()); // Cancel
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");

        // After the countdown's time nothing started and no macro editor opened.
        TargetWindow.Quiet(4);
        Assert.Null(app.Automation.GetDesktop().FindFirstChild(cf => cf.ByName("New macro").And(cf.ByProcessId(app.ProcessId))));
        Assert.Empty(AppSession.SavedMacros());
    }

    /// <summary>
    /// Record macro, Record mouse movement as asked, Start recording; the target window is brought to the
    /// front during the countdown (clicks then are not recorded). Returns once recording runs.
    /// </summary>
    private static void StartRecording(AppSession app, TargetWindow target, bool recordMoves)
    {
        app.FindById(app.MainWindow, "RecordMacroButton").AsButton().Invoke();
        Window prompt = app.Dialog("Ready to record");
        var moves = app.FindById(prompt, "MouseMovementBox").AsCheckBox();
        if (moves.IsChecked != recordMoves)
            moves.Toggle();
        app.FindButton(prompt, "Start recording").Invoke();
        target.Activate();

        AutomationElement bar = app.TopWindow("Recording") ?? throw new InvalidOperationException("no recording bar");
        AutomationElement countdown = app.FindById(bar, "CountdownText");
        Assert.True(Retry.WhileFalse(() => Gone(countdown), TimeSpan.FromSeconds(8)).Success, "the countdown did not end");
        target.Clear();
    }

    /// <summary>
    /// The recording bar's one shown button: Cancel during the countdown, Stop while recording. Its text is
    /// in its template, so it has no name to find it by.
    /// </summary>
    private static AutomationElement BarButton(AppSession app)
    {
        AutomationElement bar = app.TopWindow("Recording") ?? throw new InvalidOperationException("no recording bar");
        return Retry.WhileNull(() => bar.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "no button on the recording bar").Result!;
    }

    /// <summary>Hidden, or no longer in the UI Automation tree (a collapsed element leaves it).</summary>
    private static bool Gone(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen || !element.IsAvailable;
        }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            return true;
        }
    }

    /// <summary>Names the macro in the New macro window, saves it and returns it as saved.</summary>
    private static MacroDefinition SaveRecordedMacro(AppSession app, string name)
    {
        Window editor = app.Dialog("New macro");
        app.FindById(editor, "NameBox").AsTextBox().Text = name;
        app.FindById(editor, "SaveButton").AsButton().Invoke();
        MacroDefinition? saved = null;
        Assert.True(Retry.WhileNull(() => saved = AppSession.SavedMacros().FirstOrDefault(m => m.Name == name), AppSession.Timeout, ignoreException: true).Success,
            $"\"{name}\" was not saved");
        return saved!;
    }
}
