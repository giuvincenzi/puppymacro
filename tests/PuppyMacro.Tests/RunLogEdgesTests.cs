using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// A run's log (RunLog) in the cases RunLogTests does not cover: the log an AI app reads when it may not see the screen
/// (WithoutWhatWasSeen), a run that is stopped, a run that plays once (no rounds), a run whose action is skipped. The keys
/// pressed are F24 only. The runs write in the debug archive (a temporary folder).
/// </summary>
[Collection(nameof(DebugStore))]
public sealed class RunLogEdgesTests : IDisposable
{
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);
    private const int F24 = 0x87;
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public RunLogEdgesTests()
    {
        DebugStore.Root = _folder.File("debug");
        using var black = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(black);
    }

    public void Dispose()
    {
        RunLog.WaitWritten(Written);
        _screen.Dispose();
        _folder.Dispose();
    }

    private string NewFolder()
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "run-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Without_what_was_seen_takes_out_only_the_lines_of_what_the_conditions_saw_once_per_block()
    {
        var log = new RunLog(NewFolder(), new MacroDefinition { Name = "Seen" }, "a test");
        log.Event("#1 If: true after 0.1 s -> Then (1 actions)");
        log.Seen("Text \"Name\": \"secret\" found at 10,20 in the line \"my secret password\"");
        log.Seen("  -> Text \"Name\" is true");
        log.Line("#1 Then 1 skipped: a PuppyMacro window is at that point");
        log.Event("#2 If: not true after 0.1 s -> Else (0 actions)");
        log.Seen("Pixel 5,5: saw #123456, wanted #000000 within 5%");
        log.End("finished");
        Assert.True(RunLog.WaitWritten(Written));

        string without = RunLog.WithoutWhatWasSeen(RunLog.Read(log.Folder)!);

        Assert.DoesNotContain("secret", without);
        Assert.DoesNotContain("#123456", without);
        Assert.Contains("#1 If: true", without);
        Assert.Contains("#1 Then 1 skipped", without);
        Assert.Contains("#2 If: not true", without);
        Assert.Contains("Ends: finished", without);
        Assert.Equal(2, without.Split('\n').Count(l => l.Contains("what the conditions saw is left out")));
    }

    private (MacroRunner Runner, string Log) Run(MacroDefinition macro, Action<MacroRunner>? meanwhile = null)
    {
        var log = new RunLog(NewFolder(), macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) });
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        meanwhile?.Invoke(runner);
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(RunLog.WaitWritten(Written));
        return (runner, RunLog.Read(log.Folder)!);
    }

    [Fact]
    public void A_stopped_run_says_why_in_its_log_and_its_run_file()
    {
        var macro = new MacroDefinition
        {
            Name = "Long",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 5, DelayMs = 60_000 } },
        };

        var (runner, log) = Run(macro, r =>
        {
            Thread.Sleep(500);
            r.Stop("stopped at the test's time limit of 0.5 s");
        });

        Assert.Equal("stopped at the test's time limit of 0.5 s", runner.EndReason);
        Assert.Contains("Ends: stopped at the test's time limit of 0.5 s", log);
        Assert.DoesNotContain("#1 Press", log);
        Assert.Contains("\"EndReason\": \"stopped at the test\\u0027s time limit of 0.5 s\"",
            File.ReadAllText(Path.Combine(runner.DebugLog!.Folder, RunLog.RunFile)));
    }

    [Fact]
    public void A_run_that_plays_once_has_no_rounds()
    {
        var macro = new MacroDefinition
        {
            Name = "Once",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = F24, HoldMs = 5 } },
        };

        var (_, log) = Run(macro);

        Assert.DoesNotContain("Round", log);
        Assert.Contains("#1 Press F24, held 5 ms", log);
        Assert.Contains("Ends: finished", log);
    }

    /// <summary>A click where its condition found nothing (a pixel gives no place) is skipped and says so; nothing is clicked.</summary>
    [Fact]
    public void A_click_where_nothing_was_found_is_skipped_and_the_log_says_so()
    {
        var pixel = new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = "#000000", Tolerance = 5 };
        var macro = new MacroDefinition
        {
            Name = "Skip",
            Actions =
            {
                new MacroAction
                {
                    Type = MacroActionType.If,
                    If = new MacroIf
                    {
                        Conditions = { pixel },
                        Wait = ConditionWait.Once,
                        Then = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, FoundId = pixel.Id } },
                    },
                },
            },
        };

        var (_, log) = Run(macro);

        Assert.Contains("#1 If: true", log);
        Assert.Contains("#1 Then 1 Click: skipped, its condition found nothing this time", log);
        Assert.Contains("Ends: finished", log);
    }
}
