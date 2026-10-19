using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Playing a macro (MacroRunner) without a log, the ends that MacroRunnerTests does not cover: a key held by Key down is
/// released however the run ends (on its own, by Stop the macro, inside a component), the rounds of Times, a stop before
/// the thread starts, an empty macro that repeats forever. The only key sent is F24; nothing moves the mouse. InputSender
/// reads ModifierTracker: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class MacroRunnerEdgesTests : IDisposable
{
    private const int F24 = 0x87;
    private static readonly TimeSpan Ends = TimeSpan.FromSeconds(10);

    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public MacroRunnerEdgesTests()
    {
        ModifierTracker.Reset();
        using var black = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(black);
    }

    public void Dispose()
    {
        _screen.Dispose();
        ModifierTracker.Reset();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    /// <summary>Up within 2 s: other test classes may tap F24 meanwhile (down for a few ms), never hold it.</summary>
    private static bool Released(int vk)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            if ((GetAsyncKeyState(vk) & 0x8000) == 0)
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static MacroAction Press(double delayMs = 0) => new() { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 5, DelayMs = delayMs };

    private static MacroAction KeyDown() => new() { Type = MacroActionType.KeyDown, Vk = F24 };

    private static MacroDefinition Macro(params MacroAction[] actions)
    {
        var macro = new MacroDefinition { Name = "Test" };
        macro.Actions.AddRange(actions);
        return macro;
    }

    /// <summary>Plays the macro until it ends; gives the runner and how long it took.</summary>
    private (MacroRunner Runner, TimeSpan Took) Play(MacroDefinition macro, ComponentPrograms? programs = null, bool stopFirst = false)
    {
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen,
            programs: programs);
        using var ended = new ManualResetEventSlim();
        TimeSpan took = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        runner.Exited += _ =>
        {
            took = clock.Elapsed;
            ended.Set();
        };
        if (stopFirst)
            runner.Stop("stopped before it started");
        runner.Start();
        bool done = ended.Wait(Ends, TestContext.Current.CancellationToken);
        if (!done)
        {
            runner.Stop("the test ended");
            ended.Wait(Ends);
        }
        Assert.True(done, "the macro did not end");
        return (runner, took);
    }

    [Fact]
    public void A_key_held_by_Key_down_is_released_when_the_macro_ends_on_its_own()
    {
        var (runner, _) = Play(Macro(KeyDown(), Press(delayMs: 50)));

        Assert.Equal("finished", runner.EndReason);
        Assert.True(Released(F24), "F24 stayed down after the macro finished");
    }

    [Fact]
    public void A_key_held_by_Key_down_is_released_when_an_If_stops_the_macro()
    {
        var notTrue = new MacroAction
        {
            Type = MacroActionType.If,
            If = new MacroIf
            {
                Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF", Tolerance = 5 } },
                Wait = ConditionWait.Once,
                OnFail = ConditionFail.Stop,
            },
        };
        var (runner, _) = Play(Macro(KeyDown(), notTrue, Press()));

        Assert.Equal("an If was not true, and it stops the macro (Stop the macro)", runner.EndReason);
        Assert.True(Released(F24), "F24 stayed down after Stop the macro");
    }

    [Fact]
    public void A_key_held_by_Key_down_inside_a_component_is_released_at_the_end_of_the_macro()
    {
        var component = new LibraryComponent { Name = "Hold F24", Concept = "hold_f24", Actions = { KeyDown() } };
        var (runner, _) = Play(Macro(new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id }, Press(delayMs: 50)),
            ComponentPrograms.Prepare(new[] { component }));

        Assert.Equal("finished", runner.EndReason);
        Assert.True(Released(F24), "F24 held by the component stayed down");
    }

    [Fact]
    public void Times_plays_every_round()
    {
        var macro = Macro(Press(delayMs: 300));
        macro.Repeat = RepeatMode.Times;
        macro.RepeatCount = 3;

        var (runner, took) = Play(macro);

        Assert.Equal("finished", runner.EndReason);
        Assert.InRange(took.TotalMilliseconds, 850, 5000); // 3 rounds of 300 ms
    }

    [Fact]
    public void Times_below_one_plays_once()
    {
        var macro = Macro(Press(delayMs: 300));
        macro.Repeat = RepeatMode.Times;
        macro.RepeatCount = 0;

        var (runner, took) = Play(macro);

        Assert.Equal("finished", runner.EndReason);
        Assert.InRange(took.TotalMilliseconds, 280, 850);
    }

    [Fact]
    public void A_macro_stopped_before_its_thread_starts_plays_nothing_and_keeps_the_reason()
    {
        var (runner, took) = Play(Macro(KeyDown(), Press(delayMs: 5000)), stopFirst: true);

        Assert.Equal("stopped before it started", runner.EndReason);
        Assert.True(took < TimeSpan.FromSeconds(3));
        Assert.True(Released(F24));
    }

    /// <summary>An empty macro that repeats forever waits between rounds (no spinning) and still ends at the stop.</summary>
    [Fact]
    public void An_empty_macro_that_repeats_forever_ends_at_the_stop()
    {
        var macro = Macro();
        macro.Repeat = RepeatMode.Loop;
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen);
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Thread.Sleep(400);
        Assert.False(ended.IsSet, "an endless empty macro ended by itself");

        runner.Stop("stop");

        Assert.True(ended.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal("stop", runner.EndReason);
    }

    /// <summary>The If "Stop the macro" inside a component ends the whole macro: the rows after the call do not play.</summary>
    [Fact]
    public void Stop_the_macro_inside_a_component_ends_the_whole_macro()
    {
        var stops = new MacroAction
        {
            Type = MacroActionType.If,
            If = new MacroIf
            {
                Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#FFFFFF", Tolerance = 5 } },
                Wait = ConditionWait.Once,
                OnFail = ConditionFail.Stop,
            },
        };
        var component = new LibraryComponent { Name = "Check", Concept = "check", Actions = { stops } };
        var macro = Macro(new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id }, Press(delayMs: 3000));

        var (runner, took) = Play(macro, ComponentPrograms.Prepare(new[] { component }));

        Assert.Equal("an If was not true, and it stops the macro (Stop the macro)", runner.EndReason);
        Assert.True(took < TimeSpan.FromMilliseconds(2500), $"the row after the call played: {took.TotalMilliseconds:0} ms");
    }
}
