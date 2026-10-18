using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// What LoopEngine does with the macros it plays: the editor's Test of an action, the reasons a run's Debug log gives
/// when the engine stops it, and the log's events across rounds. The only key sent is F24. The runs write in the debug
/// archive (a temporary folder): same collection as the other tests that write there.
/// </summary>
[Collection(nameof(DebugStore))]
public sealed class EnginePlaybackTests : IDisposable
{
    private const int F24 = 0x87;
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly LoopEngine _engine = new(new EngineSnapshot(), Dispatcher.CurrentDispatcher);
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public EnginePlaybackTests() => DebugStore.Root = _folder.File("debug");

    public void Dispose()
    {
        _engine.StopAll();
        _engine.Dispose();
        RunLog.WaitWritten(Written);
        _screen.Dispose();
        _folder.Dispose();
    }

    /// <summary>Runs what the engine queued on this thread's dispatcher until <paramref name="done"/> or the timeout.</summary>
    private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > timeout)
                return false;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>F24 pressed 50 times, 100 ms apart: about 5.5 s.</summary>
    private static MacroAction LongPress() => new()
    {
        Type = MacroActionType.PressKey, Vk = F24, HoldMs = 10, Repeat = 50, RepeatPauseMs = 100,
    };

    private string NewFolder(string name)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The editor's Test plays an action on a runner the engine never keeps (LoopEngine.TestMacroAction): Stop all (its
    /// hotkey, the overlay's button, an AI app's stop_all) does not reach it, so a long action (or an If that waits as long
    /// as it takes) plays to its end while the editors stay off screen.
    /// </summary>
    [Fact]
    public void Stop_all_stops_the_editors_Test_of_an_action()
    {
        bool finished = false;
        _engine.TestMacroAction(LongPress(), () => finished = true);
        Thread.Sleep(300);

        _engine.StopAll();

        Assert.True(PumpUntil(() => finished, TimeSpan.FromSeconds(2)), "the Test went on after Stop all");
    }

    /// <summary>
    /// A macro for an app stops when another app comes in front (LoopEngine.OnForegroundApp), but StopMacro is called
    /// without a reason, so its Debug log (and get_last_run for an AI app) says the user stopped it.
    /// </summary>
    [Fact]
    public void A_macro_stopped_because_another_app_came_in_front_does_not_say_the_user_stopped_it()
    {
        var macro = new MacroDefinition { Name = "Game macro", AppExe = "Game.exe", Actions = { LongPress() } };
        _engine.Publish(new EngineSnapshot { Macros = new[] { macro } });
        _engine.OnForegroundApp("Game.exe");
        using var ended = new ManualResetEventSlim();
        var runner = _engine.StartMacroTest(macro, NewFolder("app"), "a test", _ => ended.Set());
        Assert.NotNull(runner);
        Thread.Sleep(500);

        _engine.OnForegroundApp("other.exe");

        Assert.True(ended.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the macro did not stop");
        Assert.NotNull(runner!.EndReason);
        Assert.DoesNotContain("by the user", runner.EndReason);
    }

    [Fact]
    public void Each_round_numbers_its_events_in_order_with_one_picture_for_each_action()
    {
        using var black = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(black);
        var macro = new MacroDefinition
        {
            Name = "Rounds",
            Repeat = RepeatMode.Times,
            RepeatCount = 3,
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 5 },
                new MacroAction { Type = MacroActionType.KeyDown, Vk = F24 },
                new MacroAction { Type = MacroActionType.KeyUp, Vk = F24 },
            },
        };
        var log = new RunLog(NewFolder("rounds"), macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) });
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(RunLog.WaitWritten(Written));

        var events = RunLog.Read(log.Folder)!.Split('\n')
            .Select(l => Regex.Match(l, @"^(\d{4}) \+\S+ (.*?)\r?$")).Where(m => m.Success)
            .Select(m => (Number: int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Text: m.Groups[2].Value))
            .ToList();
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Number));
        for (int round = 1; round <= 3; round++)
            Assert.Contains(events, e => e.Text == $"Round {round} of 3");
        var actions = events.Where(e => e.Text.StartsWith('#')).ToList();
        Assert.Equal(9, actions.Count);
        Assert.Equal(new[] { "#1", "#2", "#3", "#1", "#2", "#3", "#1", "#2", "#3" }, actions.Select(e => e.Text.Split(' ')[0]));
        foreach (var (number, text) in actions)
        {
            Assert.EndsWith($"→ {number:0000}.png", text);
            Assert.True(File.Exists(Path.Combine(log.Folder, $"{number:0000}.png")), $"no picture for event {number}");
        }
        Assert.Equal("finished", runner.EndReason);
    }
}
