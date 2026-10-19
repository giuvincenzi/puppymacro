using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// LoopEngine with several loops running together: Hold down rows that hold the same key, Stop all, and what the hook
/// decides about a key a loop holds. The only key sent and held is F24 (read back with GetAsyncKeyState, as
/// MacroRunnerTests does). InputSender reads ModifierTracker: same collection as its own tests, which runs alone.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class LoopEngineTogetherTests : IDisposable
{
    private const int F24 = 0x87;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private readonly LoopEngine _engine;

    public LoopEngineTogetherTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _engine.StopAll();
        _engine.Dispose();
        WaitFor(() => !IsDown(F24), Soon);
        ModifierTracker.Reset();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Polls on the test's own thread (never the thread pool).</summary>
    private static bool WaitFor(Func<bool> done, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > timeout)
                return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private static LoopDefinition HoldDownLoop(string name) => new()
    {
        Name = name,
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = F24, HoldDown = true } },
    };

    /// <summary>
    /// Two loops hold the same key down (a Hold down row of F24 each: "keep W held" in two loops of a game). Stopping one
    /// releases the key (ActionRunner's finally: InputSender.Up) although the other still runs and holds it: the key is
    /// up while the other loop shows as running, and the hook still blocks the user's own presses of it (IsHeldByLoop),
    /// the very case input.md's Hold down rule exists for ("otherwise the release would let go of the loop's key while the
    /// loop still shows as running").
    /// </summary>
    [Fact]
    public void Stopping_one_of_two_loops_that_hold_the_same_key_keeps_it_held_for_the_other()
    {
        LoopDefinition first = HoldDownLoop("First"), second = HoldDownLoop("Second");
        _engine.Publish(new EngineSnapshot { Loops = new[] { first, second } });

        _engine.StartLoop(first);
        _engine.StartLoop(second);
        Assert.True(WaitFor(() => IsDown(F24), Soon), "the loops did not hold F24");
        Thread.Sleep(100); // both rows' threads have pressed it

        _engine.StopLoop(first.Id);
        Thread.Sleep(200); // the stopped row's thread releases what it held

        Assert.True(_engine.IsRunning(second.Id));
        Assert.True(_engine.OnKey(F24, isDown: true, physicalModifierEvent: true)); // the hook still treats it as held
        _engine.OnKey(F24, isDown: false, physicalModifierEvent: true);
        Assert.True(IsDown(F24), "the second loop still runs, but the key it holds was released when the first one stopped");
    }

    /// <summary>A Hold down row holds its key while the loop runs and releases it at once when it stops (passing).</summary>
    [Fact]
    public void A_Hold_down_row_holds_its_key_until_the_loop_stops()
    {
        LoopDefinition loop = HoldDownLoop("Hold");
        _engine.Publish(new EngineSnapshot { Loops = new[] { loop } });

        _engine.StartLoop(loop);
        Assert.True(WaitFor(() => IsDown(F24), Soon));
        Thread.Sleep(200);
        Assert.True(IsDown(F24));

        _engine.StopLoop(loop.Id);
        Assert.True(WaitFor(() => !IsDown(F24), Soon), "the key stayed down after the loop stopped");
        Assert.False(_engine.IsRunning(loop.Id));
    }

    /// <summary>Stop all stops every loop, macro and the editor's Test together, and nothing stays held (passing).</summary>
    [Fact]
    public void Stop_all_stops_loops_macros_and_a_test_together_and_releases_what_they_hold()
    {
        LoopDefinition hold = HoldDownLoop("Hold");
        var macro = new MacroDefinition
        {
            Name = "Long",
            Repeat = RepeatMode.Loop,
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 10, DelayMs = 500 } },
        };
        _engine.Publish(new EngineSnapshot { Loops = new[] { hold }, Macros = new[] { macro } });
        bool testFinished = false;

        _engine.StartLoop(hold);
        _engine.StartMacro(macro);
        _engine.TestMacroAction(new MacroAction { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 10, Repeat = 100, RepeatPauseMs = 100 },
            () => testFinished = true);
        Assert.True(WaitFor(() => IsDown(F24), Soon));
        Assert.True(_engine.AnyRunning);

        _engine.StopAll();

        Assert.False(_engine.AnyRunning);
        Assert.True(WaitFor(() => !IsDown(F24), Soon), "a key stayed down after Stop all");
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        while (!testFinished && clock.Elapsed < Soon)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            frame = new DispatcherFrame();
            Thread.Sleep(10);
        }
        Assert.True(testFinished, "the editor's Test did not say it finished after Stop all");
    }
}
