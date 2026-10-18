using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// A macro's Debug log (RunLog): written in order by its own thread, one folder per macro with only the last run, the
/// newest picture of an If, deleted all at once; and what a run writes in it (MacroRunner).
/// </summary>
public class RunLogTests : IDisposable
{
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void Lines_are_written_in_order_and_a_new_run_keeps_only_its_own()
    {
        var id = Guid.NewGuid();
        var first = new RunLog(id, "Farm", "started by the user", _folder.Path);
        for (int i = 0; i < 100; i++)
            first.Line($"line {i}");
        Assert.True(first.WaitWritten(Written));
        string log = RunLog.Read(id, _folder.Path)!;
        Assert.Contains("Macro \"Farm\" starts (started by the user)", log);
        Assert.True(log.IndexOf("line 9", StringComparison.Ordinal) < log.IndexOf("line 10", StringComparison.Ordinal));
        Assert.Contains("line 99", log);

        var second = new RunLog(id, "Farm", "a test by an AI app", _folder.Path);
        second.Line("only this");
        Assert.True(second.WaitWritten(Written));
        log = RunLog.Read(id, _folder.Path)!;
        Assert.DoesNotContain("line 99", log);
        Assert.Contains("only this", log);
    }

    [Fact]
    public void An_If_false_every_round_keeps_its_newest_picture()
    {
        var id = Guid.NewGuid();
        var log = new RunLog(id, "Farm", "test", _folder.Path);
        for (int i = 0; i < 5; i++)
            log.Picture("if-2.png", new Mat(10, 10, MatType.CV_8UC3, Scalar.All(i * 50)));
        Assert.True(log.WaitWritten(Written));

        using var saved = Cv2.ImRead(Path.Combine(log.Folder, "if-2.png"));
        Assert.Equal(200, saved.At<Vec3b>(0, 0).Item0);
        Assert.Single(Directory.GetFiles(log.Folder, "*.png"));
    }

    [Fact]
    public void Every_log_is_deleted_at_once()
    {
        var log = new RunLog(Guid.NewGuid(), "Farm", "test", _folder.Path);
        log.Line("something");
        Assert.True(log.WaitWritten(Written));

        RunLog.DeleteAll(_folder.Path);

        Assert.False(Directory.Exists(_folder.Path));
        Assert.Null(RunLog.Read(Guid.NewGuid(), _folder.Path));
    }

    /// <summary>Runs a macro to its end with a Debug log and gives the log.</summary>
    private string RunWithLog(MacroDefinition macro)
    {
        var log = new RunLog(macro.Id, macro.Name, "test", _folder.Path);
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen, log);
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(log.WaitWritten(Written));
        return RunLog.Read(macro.Id, _folder.Path)!;
    }

    private static MacroAction If(string color, ConditionFail onFail, params MacroAction[] then) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf
        {
            Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 5, Color = color, Tolerance = 5 } },
            OnFail = onFail,
            Then = new List<MacroAction>(then),
        },
    };

    [Fact]
    public void A_run_says_what_each_If_saw_where_it_went_and_why_it_ended()
    {
        using var black = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(black);
        var macro = new MacroDefinition
        {
            Name = "Bomb",
            Actions =
            {
                If("#000000", ConditionFail.Stop, new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87, HoldMs = 10 }),
                If("#FFFFFF", ConditionFail.Stop),
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87, HoldMs = 10 },
            },
        };

        string log = RunWithLog(macro);

        Assert.Contains("#1 If: 1 condition(s), checks once", log);
        Assert.Contains("Pixel 5,5: saw #000000, wanted #000000 within 5%", log);
        Assert.Contains("#1 If: true", log);
        Assert.Contains("#1 Then 1 Press F24", log);
        Assert.Contains("Pixel 5,5: saw #000000, wanted #FFFFFF within 5%", log);
        Assert.Contains("#2 If: not true", log);
        Assert.Contains("the macro stops (Stop the macro)", log);
        Assert.Contains("screenshot of 0,0 640x400: if-2.png", log);
        Assert.True(File.Exists(Path.Combine(RunLog.FolderFor(macro.Id, _folder.Path), "if-2.png")));
        // Stop the macro: the action after the If does not play.
        Assert.DoesNotContain("#3 Press", log);
        Assert.Contains("Macro ends: an If was not true, and it stops the macro (Stop the macro)", log);
    }

    [Fact]
    public void A_run_without_Debug_writes_nothing()
    {
        var macro = new MacroDefinition { Name = "Quiet", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87, HoldMs = 10 } } };
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen);
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Null(RunLog.Read(macro.Id, _folder.Path));
        Assert.Equal("finished", runner.EndReason);
    }
}
