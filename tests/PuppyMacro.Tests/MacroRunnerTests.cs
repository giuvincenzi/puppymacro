using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Playing a macro (MacroRunner) without a log: stops (mid delay, mid If wait, inside a component) keep the first reason,
/// the speed scales the delays but not an If's wait, extreme delays, "Stop the macro" in a Loop macro, keys held by a Key
/// down released whatever ends the run, and what a long press does to the input of everything else. The only key sent is
/// F24; nothing moves the mouse. InputSender reads ModifierTracker: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class MacroRunnerTests : IDisposable
{
    private const int F24 = 0x87;
    private static readonly TimeSpan Ends = TimeSpan.FromSeconds(10);

    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public MacroRunnerTests()
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

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static MacroAction Press(double holdMs = 10, double delayMs = 0, int repeat = 1, double pauseMs = 50) => new()
    {
        Type = MacroActionType.PressKey, Vk = F24, HoldMs = holdMs, DelayMs = delayMs, Repeat = repeat, RepeatPauseMs = pauseMs,
    };

    private static MacroAction KeyDown() => new() { Type = MacroActionType.KeyDown, Vk = F24 };

    /// <summary>An If on a pixel of the fake screen (black): "#000000" is true, "#FFFFFF" never is.</summary>
    private static MacroAction If(string color, ConditionWait wait, double seconds, ConditionFail onFail, params MacroAction[] then) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf
        {
            Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = color, Tolerance = 5 } },
            Wait = wait,
            WaitSeconds = seconds,
            OnFail = onFail,
            Then = new List<MacroAction>(then),
        },
    };

    private static MacroDefinition Macro(params MacroAction[] actions)
    {
        var macro = new MacroDefinition { Name = "Test" };
        macro.Actions.AddRange(actions);
        return macro;
    }

    private sealed class Run : IDisposable
    {
        private readonly ManualResetEventSlim _ended = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public MacroRunner Runner { get; }
        public TimeSpan Elapsed { get; private set; }

        public Run(MacroRunner runner)
        {
            Runner = runner;
            runner.Exited += _ =>
            {
                Elapsed = _clock.Elapsed;
                _ended.Set();
            };
            runner.Start();
        }

        public bool Wait(TimeSpan timeout) => _ended.Wait(timeout, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Runner.Stop("the test ended");
            _ended.Wait(Ends);
            _ended.Dispose();
        }
    }

    private Run Start(MacroDefinition macro, IScreen? screen = null, ComponentPrograms? programs = null) =>
        new(new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, screen ?? _screen,
            programs: programs));

    // ================= Stops =================

    [Fact]
    public void A_stop_in_the_middle_of_a_delay_ends_at_once_and_keeps_the_first_reason()
    {
        using var run = Start(Macro(Press(delayMs: 60_000)));
        Thread.Sleep(300);
        run.Runner.Stop("first reason");
        run.Runner.Stop("second reason");
        run.Runner.Stop();

        Assert.True(run.Wait(TimeSpan.FromSeconds(3)), "a delay did not end at the stop");
        Assert.Equal("first reason", run.Runner.EndReason);
    }

    [Fact]
    public void A_stop_while_an_If_waits_ends_at_once_and_keeps_the_first_reason()
    {
        using var run = Start(Macro(If("#FFFFFF", ConditionWait.Forever, 0, ConditionFail.Else, Press())));
        Thread.Sleep(1500);
        run.Runner.Stop("first reason");
        run.Runner.Stop("second reason");

        Assert.True(run.Wait(TimeSpan.FromSeconds(3)), "an If's wait did not end at the stop");
        Assert.Equal("first reason", run.Runner.EndReason);
    }

    [Fact]
    public void A_stop_inside_a_component_ends_the_macro_and_keeps_the_first_reason()
    {
        var component = new LibraryComponent { Name = "Slow", Concept = "slow", Actions = { Press(delayMs: 60_000) } };
        var macro = Macro(new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id }, Press());
        using var run = Start(macro, programs: ComponentPrograms.Prepare(new[] { component }));
        Thread.Sleep(300);
        run.Runner.Stop("first reason");
        run.Runner.Stop("second reason");

        Assert.True(run.Wait(TimeSpan.FromSeconds(3)), "a component's delay did not end at the stop");
        Assert.Equal("first reason", run.Runner.EndReason);
    }

    // ================= Speed and delays =================

    [Fact]
    public void The_speed_scales_the_delays()
    {
        var fast = Macro(Press(delayMs: 4000));
        fast.Speed = 4;
        using (var run = Start(fast))
        {
            Assert.True(run.Wait(Ends));
            Assert.InRange(run.Elapsed.TotalMilliseconds, 800, 3000); // 1 s at x4, not 4 s
        }

        var slow = Macro(Press(delayMs: 400));
        slow.Speed = 0.25;
        using (var run = Start(slow))
        {
            Assert.True(run.Wait(Ends));
            Assert.True(run.Elapsed.TotalMilliseconds >= 1400, $"x0.25 took {run.Elapsed.TotalMilliseconds} ms"); // 1.6 s
        }
    }

    [Fact]
    public void A_speed_out_of_range_is_clamped_never_a_division_by_zero()
    {
        var zero = Macro(Press(delayMs: 200));
        zero.Speed = 0;
        using var run = Start(zero);
        Assert.True(run.Wait(Ends));
        Assert.Equal("finished", run.Runner.EndReason);
        Assert.True(run.Elapsed.TotalMilliseconds >= 700, $"speed 0 took {run.Elapsed.TotalMilliseconds} ms"); // x0.25: 800 ms
    }

    [Fact]
    public void The_speed_does_not_shorten_an_Ifs_wait()
    {
        var macro = Macro(If("#FFFFFF", ConditionWait.UpTo, 2, ConditionFail.Else, Press()));
        macro.Speed = 4;
        using var run = Start(macro);

        Assert.True(run.Wait(Ends));
        Assert.True(run.Elapsed.TotalMilliseconds >= 1800, $"the If waited {run.Elapsed.TotalMilliseconds} ms, not 2 s");
    }

    [Fact]
    public void A_negative_delay_plays_at_once_and_a_huge_one_still_stops()
    {
        using (var run = Start(Macro(Press(delayMs: -5000), Press(holdMs: -10))))
        {
            Assert.True(run.Wait(TimeSpan.FromSeconds(3)), "a negative delay or hold did not play at once");
            Assert.Equal("finished", run.Runner.EndReason);
        }

        using (var run = Start(Macro(Press(delayMs: 1e15))))
        {
            Thread.Sleep(300);
            Assert.False(run.Wait(TimeSpan.Zero), "a huge delay ended by itself");
            run.Runner.Stop("stop");
            Assert.True(run.Wait(TimeSpan.FromSeconds(3)), "a huge delay did not end at the stop");
        }
    }

    // ================= Ends =================

    [Fact]
    public void An_If_that_stops_the_macro_ends_a_macro_that_repeats_forever()
    {
        var macro = Macro(Press(), If("#FFFFFF", ConditionWait.Once, 0, ConditionFail.Stop));
        macro.Repeat = RepeatMode.Loop;
        using var run = Start(macro);

        Assert.True(run.Wait(Ends), "a Loop macro went on after Stop the macro");
        Assert.Equal("an If was not true, and it stops the macro (Stop the macro)", run.Runner.EndReason);
    }

    [Fact]
    public void A_key_held_by_Key_down_is_released_when_the_macro_is_stopped()
    {
        using var run = Start(Macro(KeyDown(), Press(delayMs: 60_000)));
        Thread.Sleep(300);
        run.Runner.Stop("stop");
        Assert.True(run.Wait(TimeSpan.FromSeconds(3)));

        Assert.True(Released(F24), "F24 stayed down after the stop");
    }

    [Fact]
    public void A_key_held_by_Key_down_is_released_when_the_screen_fails()
    {
        using var run = Start(Macro(KeyDown(), If("#000000", ConditionWait.Once, 0, ConditionFail.Else, Press())), new ThrowingScreen());
        Assert.True(run.Wait(Ends));

        Assert.StartsWith("failed:", run.Runner.EndReason);
        Assert.True(Released(F24), "F24 stayed down after the macro failed");
    }

    /// <summary>
    /// Up within 2 s: other test classes may tap F24 meanwhile (down for a few ms), never hold it; a key left down stays
    /// down.
    /// </summary>
    private static bool Released(int vk)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (!IsDown(vk))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private sealed class ThrowingScreen : IScreen
    {
        public IScreenWatch? Start(Rect area) => throw new InvalidOperationException("the capture failed");
        public void Listen(AutoResetEvent waiter) { }
        public void Unlisten(AutoResetEvent waiter) { }
    }

    // ================= Other input meanwhile =================

    /// <summary>
    /// A macro's Press key held long (its Hold for: 3 s) holds InputSender's lock for the whole hold, as a loop's long
    /// press no longer does (InputSender.PressFor): every loop row, other macro and remap that sends input waits for it.
    /// </summary>
    [Fact]
    public async Task A_macros_long_press_does_not_hold_up_what_others_send_meanwhile()
    {
        using var run = Start(Macro(Press(holdMs: 3000)));
        Thread.Sleep(300); // the macro is in its hold

        using var timer = PreciseTimer.Create();
        using var never = new ManualResetEvent(false);
        var other = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            InputSender.Tap(F24, 1, timer, never); // what a loop row or a remap sends meanwhile
            return clock.Elapsed;
        }, TestContext.Current.CancellationToken);

        TimeSpan waited = await other.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(waited < TimeSpan.FromMilliseconds(1500),
            $"a tap of another item waited {waited.TotalMilliseconds:0} ms for the macro's press to end");
        Assert.True(run.Wait(Ends));
    }
}
