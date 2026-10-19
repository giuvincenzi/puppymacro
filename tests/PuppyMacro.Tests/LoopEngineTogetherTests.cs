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
