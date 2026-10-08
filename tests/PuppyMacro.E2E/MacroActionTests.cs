using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>Every kind of macro action, played on the target window. make e2e CATEGORY=Macros</summary>
[Trait("Category", "Macros")]
public class MacroActionTests
{
    [Fact]
    public void Key_actions_press_hold_repeat_and_make_combinations()
    {
        MacroDefinition macro = Macro("E2E keys", Vk.F13,
            Press(Vk.F15, holdMs: 300),
            new MacroAction { Type = MacroActionType.PressKey, Vk = Vk.F16, Repeat = 3, RepeatPauseMs = 400, DelayMs = 200 },
            new MacroAction { Type = MacroActionType.KeyDown, Vk = Vk.F17, DelayMs = 200 },
            new MacroAction { Type = MacroActionType.KeyUp, Vk = Vk.F17, DelayMs = 500 },
            // Ctrl+A: Ctrl down, A, Ctrl up.
            new MacroAction { Type = MacroActionType.KeyDown, Vk = Vk.LControl, DelayMs = 200 },
            Press(Vk.A),
            new MacroAction { Type = MacroActionType.KeyUp, Vk = Vk.LControl },
            Press(Vk.F22, delayMs: 200)); // the end
        using var app = new AppSession(seed => seed.Macros.Add(macro));
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 10, "the macro did not finish");

        // The hotkey started it and never reached the window.
        Assert.Empty(target.Downs(Vk.F13));

        TargetEvent f15Down = Assert.Single(target.Downs(Vk.F15));
        About(Assert.Single(target.Ups(Vk.F15)).Ms - f15Down.Ms, 300, "F15 held");

        List<TargetEvent> f16 = target.Downs(Vk.F16);
        Assert.Equal(3, f16.Count);
        Assert.Equal(3, target.Ups(Vk.F16).Count);
        foreach (double gap in Gaps(f16))
            About(gap, 430, "F16 repeated (30 ms press + 400 ms pause)");

        TargetEvent f17Down = Assert.Single(target.Downs(Vk.F17));
        About(Assert.Single(target.Ups(Vk.F17)).Ms - f17Down.Ms, 500, "Key down F17 to its key up");

        // Ctrl arrives as VK_CONTROL; A is pressed and released inside it.
        TargetEvent ctrlDown = Assert.Single(target.Downs(Vk.Control));
        TargetEvent ctrlUp = Assert.Single(target.Ups(Vk.Control));
        TargetEvent aDown = Assert.Single(target.Downs(Vk.A));
        TargetEvent aUp = Assert.Single(target.Ups(Vk.A));
        Assert.True(ctrlDown.Ms <= aDown.Ms && aUp.Ms <= ctrlUp.Ms, $"A is not inside Ctrl. Received: {target.Describe()}");
    }

    [Fact]
    public void Clicks_use_every_button_at_their_point_with_double_clicks_repeats_holds_and_drags()
    {
        var points = new Dictionary<string, Point>();
        using var app = new AppSession(seed =>
        {
            static Point P(double fx, double fy) => TargetWindow.PointAt(fx, fy);
            points["left"] = P(0.1, 0.2);
            points["right"] = P(0.3, 0.2);
            points["middle"] = P(0.5, 0.2);
            points["x1"] = P(0.7, 0.2);
            points["x2"] = P(0.9, 0.2);
            points["double"] = P(0.1, 0.6);
            points["repeat"] = P(0.3, 0.6);
            points["hold"] = P(0.5, 0.6);
            points["drag from"] = P(0.7, 0.6);
            points["drag to"] = P(0.9, 0.9);
            seed.Macros.Add(Macro("E2E clicks", Vk.F13,
                Click(Vk.LButton, points["left"]),
                Click(Vk.RButton, points["right"]),
                Click(Vk.MButton, points["middle"]),
                Click(Vk.XButton1, points["x1"]),
                Click(Vk.XButton2, points["x2"]),
                new MacroAction { Type = MacroActionType.Click, Vk = Vk.LButton, X = points["double"].X, Y = points["double"].Y, ClickCount = 2, DelayMs = 600 },
                new MacroAction { Type = MacroActionType.Click, Vk = Vk.RButton, X = points["repeat"].X, Y = points["repeat"].Y, Repeat = 3, RepeatPauseMs = 400, DelayMs = 300 },
                new MacroAction { Type = MacroActionType.Click, Vk = Vk.MButton, X = points["hold"].X, Y = points["hold"].Y, HoldMs = 400, DelayMs = 300 },
                new MacroAction { Type = MacroActionType.MouseDown, Vk = Vk.LButton, X = points["drag from"].X, Y = points["drag from"].Y, DelayMs = 600 },
                new MacroAction { Type = MacroActionType.MouseUp, Vk = Vk.LButton, X = points["drag to"].X, Y = points["drag to"].Y, DelayMs = 300 },
                Press(Vk.F22, delayMs: 200)));
        });
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 15, "the macro did not finish");

        List<TargetEvent> left = target.Downs(Vk.LButton);
        List<TargetEvent> right = target.Downs(Vk.RButton);
        List<TargetEvent> middle = target.Downs(Vk.MButton);
        Assert.True(left.Count == 4, $"left presses: {left.Count}. Received: {target.Describe()}");
        Assert.True(right.Count == 4, $"right presses: {right.Count}. Received: {target.Describe()}");
        Assert.True(middle.Count == 2, $"middle presses: {middle.Count}. Received: {target.Describe()}");
        Near(points["left"], left[0].Position, "left click");
        Near(points["right"], right[0].Position, "right click");
        Near(points["middle"], middle[0].Position, "middle click");
        Near(points["x1"], Assert.Single(target.Downs(Vk.XButton1)).Position, "back button click");
        Near(points["x2"], Assert.Single(target.Downs(Vk.XButton2)).Position, "forward button click");

        // Double click: two presses at the same point, 60 ms apart.
        Near(points["double"], left[1].Position, "double click");
        Near(points["double"], left[2].Position, "double click");
        Assert.True(left[2].Ms - left[1].Ms < 400, "the double click's presses are too far apart");

        // Repeat 3 with a 400 ms pause.
        foreach (TargetEvent press in right.Skip(1))
            Near(points["repeat"], press.Position, "repeated click");
        foreach (double gap in Gaps(right.Skip(1).ToList()))
            About(gap, 430, "repeated right click");

        // Held 400 ms.
        TargetEvent middleUp = target.Ups(Vk.MButton)[1];
        Near(points["hold"], middle[1].Position, "held click");
        About(middleUp.Ms - middle[1].Ms, 400, "middle button held");

        // Drag: pressed at one point, released at another.
        Near(points["drag from"], left[3].Position, "mouse down");
        Near(points["drag to"], target.Ups(Vk.LButton)[3].Position, "mouse up");
    }

    [Fact]
    public void Moves_glides_paths_and_scrolls_in_every_direction()
    {
        var p = new Dictionary<string, Point>();
        using var app = new AppSession(seed =>
        {
            p["move"] = TargetWindow.PointAt(0.2, 0.3);
            p["glide"] = TargetWindow.PointAt(0.8, 0.3);
            p["path 1"] = TargetWindow.PointAt(0.2, 0.8);
            p["path 2"] = TargetWindow.PointAt(0.5, 0.5);
            p["path 3"] = TargetWindow.PointAt(0.8, 0.8);
            seed.Macros.Add(Macro("E2E moves", Vk.F13,
                new MacroAction { Type = MacroActionType.MoveTo, X = p["move"].X, Y = p["move"].Y },
                Press(Vk.F15, delayMs: 100),
                new MacroAction { Type = MacroActionType.MoveTo, X = p["glide"].X, Y = p["glide"].Y, Smooth = true, SmoothSpeed = 1, DelayMs = 100 },
                Press(Vk.F16, delayMs: 100),
                new MacroAction
                {
                    Type = MacroActionType.MovePath,
                    DelayMs = 100,
                    Path =
                    {
                        new PathPoint { X = p["path 1"].X, Y = p["path 1"].Y, T = 0 },
                        new PathPoint { X = p["path 2"].X, Y = p["path 2"].Y, T = 300 },
                        new PathPoint { X = p["path 3"].X, Y = p["path 3"].Y, T = 600 },
                    },
                },
                Press(Vk.F17, delayMs: 100),
                new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = ScrollDirection.Up, ScrollSteps = 2, DelayMs = 100 },
                new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = ScrollDirection.Down, ScrollSteps = 3, DelayMs = 100 },
                new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = ScrollDirection.Left, ScrollSteps = 1, DelayMs = 100 },
                new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = ScrollDirection.Right, ScrollSteps = 2, DelayMs = 100 },
                Press(Vk.F22, delayMs: 200)));
        });
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 10, "the macro did not finish");

        double f15 = Assert.Single(target.Downs(Vk.F15)).Ms;
        double f16 = Assert.Single(target.Downs(Vk.F16)).Ms;
        double f17 = Assert.Single(target.Downs(Vk.F17)).Ms;
        List<TargetEvent> moves = target.Of(TargetEventKind.Move);

        // Move to: there at once.
        Near(p["move"], moves.Last(m => m.Ms < f15).Position, "move to");

        // Glide (speed 1: 1 second): many steps in between, ending at the point.
        List<TargetEvent> glide = moves.Where(m => m.Ms > f15 && m.Ms < f16).ToList();
        Assert.True(glide.Count >= 10, $"the glide moved in {glide.Count} steps");
        Near(p["glide"], glide.Last().Position, "glide end");
        Assert.Contains(glide, m => m.Position.X > p["move"].X + 20 && m.Position.X < p["glide"].X - 20);
        About(glide.Last().Ms - glide.First().Ms, 1000, "glide");

        // Path: from its first point through the middle one to the last one, in 600 ms.
        List<TargetEvent> path = moves.Where(m => m.Ms > f16 && m.Ms < f17).ToList();
        Near(p["path 1"], path.First().Position, "path start");
        Assert.Contains(path, m => Math.Abs(m.Position.X - p["path 2"].X) <= 3 && Math.Abs(m.Position.Y - p["path 2"].Y) <= 3);
        Near(p["path 3"], path.Last().Position, "path end");
        About(path.Last().Ms - path.First().Ms, 600, "path");

        // Scroll: up is +120 a notch, down -120; left is -120 on the horizontal wheel, right +120.
        Assert.Equal(new[] { 120, 120, -120, -120, -120 }, target.Of(TargetEventKind.Wheel).Select(w => w.Delta));
        Assert.Equal(new[] { -120, 120, 120 }, target.Of(TargetEventKind.HWheel).Select(w => w.Delta));
    }

    [Fact]
    public void Paste_text_with_enter_before_and_after_in_a_group_keeps_the_clipboard_and_waits_the_delays()
    {
        var group = new MacroGroup { Name = "Typing" };
        MacroDefinition macro = Macro("E2E paste", Vk.F13,
            new MacroAction { Type = MacroActionType.PasteText, Text = "Hello, world! àèìòù €", EnterAfter = true, GroupId = group.Id },
            new MacroAction { Type = MacroActionType.PasteText, Text = "first line\nsecond line", EnterBefore = true, GroupId = group.Id, DelayMs = 200 },
            Press(Vk.F16, delayMs: 200),
            Press(Vk.F15, delayMs: 1000));
        macro.Groups.Add(group);
        using var app = new AppSession(seed => seed.Macros.Add(macro));
        using var target = new TargetWindow();
        target.ClipboardText = "E2E clipboard before";
        target.Text = "";

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F15).Count == 1, 10, "the macro did not finish");

        Assert.Equal("Hello, world! àèìòù €\n\nfirst line\nsecond line", target.Text.Replace("\r\n", "\n"));
        Assert.Equal(2, target.Downs(Vk.Return).Count);
        About(target.Downs(Vk.F15)[0].Ms - target.Ups(Vk.F16)[0].Ms, 1000, "1 s delay");
        Assert.True(FlaUI.Core.Tools.Retry.WhileFalse(() => target.ClipboardText == "E2E clipboard before", AppSession.Timeout).Success,
            $"the clipboard was not restored: \"{target.ClipboardText}\"");
    }
}
