using System;
using System.Collections.Generic;
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
/// The debug archive (DebugStore) and a run's log (RunLog): names, numbered events with the pictures named by their
/// number, the cleaner (old operations go, recent and in-progress ones stay, never half a folder), resource paths that
/// never leave the archive, a macro's last run, and what a run writes (MacroRunner).
/// </summary>
[Collection(nameof(DebugStore))]
public class RunLogTests : IDisposable
{
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();

    public RunLogTests() => DebugStore.Root = _folder.File("debug");

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    private string NewFolder(string name)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static List<(Rect, Mat)> Monitor(int value) =>
        new() { (new Rect(0, 0, 10, 10), new Mat(10, 10, MatType.CV_8UC3, Scalar.All(value))) };

    [Theory]
    [InlineData("D4 Strip Affixes", "d4-strip-affixes")]
    [InlineData("Claude Desktop", "claude-desktop")]
    [InlineData("  è!!  ", "item")]
    [InlineData("puppymacro_test_macro", "puppymacro_test_macro")]
    public void Names_have_no_spaces_and_are_lowercase(string text, string slug) => Assert.Equal(slug, DebugStore.Slug(text));

    [Fact]
    public void Each_event_has_its_number_time_and_pictures_named_by_that_number()
    {
        var macro = new MacroDefinition { Name = "Farm" };
        var log = new RunLog(NewFolder("run"), macro, "a run started by the user");
        log.Event("#1 Press A", Monitor(10));
        log.Line("  Pixel 5,5: saw #000000");
        var two = Monitor(20);
        two.Add((new Rect(10, 0, 10, 10), new Mat(10, 10, MatType.CV_8UC3, Scalar.All(30))));
        log.Event("#2 If: not true", two);
        log.End("finished");
        Assert.True(RunLog.WaitWritten(Written));

        string text = RunLog.Read(log.Folder)!;
        Assert.Contains($"Macro \"Farm\" ({macro.Id:D}) starts: a run started by the user", text);
        Assert.Matches(@"0001 \+00:00\.\d{3} #1 Press A → 0001\.png", text);
        Assert.Contains("#2 If: not true → 0002.png, 0002-m2.png", text);
        Assert.Contains("0003", text);
        Assert.Contains("Ends: finished", text);
        Assert.True(File.Exists(Path.Combine(log.Folder, "0002-m2.png")));
        Assert.Contains("\"EndReason\": \"finished\"", File.ReadAllText(Path.Combine(log.Folder, RunLog.RunFile)));
        Assert.Equal(log.Folder, DebugStore.LastRunOf(macro.Id));
    }

    [Fact]
    public void Old_operations_go_recent_and_in_progress_ones_stay()
    {
        string old = NewFolder("old");
        File.WriteAllText(Path.Combine(old, "log.txt"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(old, "log.txt"), DateTime.UtcNow.AddDays(-8));
        string recent = NewFolder("recent");
        File.WriteAllText(Path.Combine(recent, "log.txt"), "recent");
        string running = NewFolder("running");
        File.WriteAllText(Path.Combine(running, "log.txt"), "running");
        File.SetLastWriteTimeUtc(Path.Combine(running, "log.txt"), DateTime.UtcNow.AddDays(-8));
        DebugStore.Begin(running);
        try
        {
            DebugStore.DeleteOlderThan(DebugStore.Life, DateTime.UtcNow);
        }
        finally
        {
            DebugStore.End(running);
        }

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(running));

        DebugStore.DeleteOlderThan(TimeSpan.Zero, DateTime.UtcNow.AddSeconds(1));
        Assert.False(Directory.Exists(recent));
        Assert.Equal(0, DebugStore.Size());
    }

    [Fact]
    public void A_session_with_no_call_left_goes_with_its_last_call()
    {
        string session = Path.Combine(DebugStore.Root, "ai", "claude-desktop", "20261018-071446-a3f2");
        Directory.CreateDirectory(Path.Combine(session, "001-puppymacro_get_help"));
        File.WriteAllText(Path.Combine(session, "session.json"), "{}");
        File.WriteAllText(Path.Combine(session, "001-puppymacro_get_help", "request.json"), "{}");
        foreach (string file in Directory.GetFiles(session, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));

        DebugStore.DeleteOlderThan(DebugStore.Life, DateTime.UtcNow);

        Assert.False(Directory.Exists(session));
    }

    [Theory]
    [InlineData("puppymacro://debug/user/macro/run/log.txt", true)]
    [InlineData("puppymacro://debug/user/macro/run/../../../../secret.txt", false)]
    [InlineData("puppymacro://debug/user/macro/run/missing.txt", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("puppymacro://debug/", false)]
    public void A_resource_is_a_file_inside_the_archive_or_nothing(string uri, bool found)
    {
        string folder = NewFolder("run");
        File.WriteAllText(Path.Combine(folder, "log.txt"), "log");
        File.WriteAllText(_folder.File("secret.txt"), "secret");

        Assert.Equal(found, DebugStore.PathOf(uri) != null);
        Assert.Equal("puppymacro://debug/user/macro/run/log.txt", DebugStore.UriOf(Path.Combine(folder, "log.txt")));
    }

    /// <summary>Runs a macro to its end with a log and gives the log.</summary>
    private string RunWithLog(MacroDefinition macro)
    {
        var log = new RunLog(NewFolder("run-" + Guid.NewGuid().ToString("N")[..6]), macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) });
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(RunLog.WaitWritten(Written));
        return RunLog.Read(log.Folder)!;
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
    public void A_run_says_what_each_If_saw_where_it_went_and_why_it_ended_with_the_screen_before_each_event()
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

        Assert.Contains("#1 If: true", log);
        Assert.Contains("Pixel 5,5: saw #000000, wanted #000000 within 5%", log);
        Assert.Matches(@"#1 Then 1 Press F24, held 10 ms → \d{4}\.png", log);
        Assert.Contains("#2 If: not true", log);
        Assert.Contains("Pixel 5,5: saw #000000, wanted #FFFFFF within 5%", log);
        Assert.Contains("the macro stops (Stop the macro)", log);
        // Stop the macro: the action after the If does not play.
        Assert.DoesNotContain("#3 Press", log);
        Assert.Contains("Ends: an If was not true, and it stops the macro (Stop the macro)", log);
    }

    [Fact]
    public void A_linked_component_plays_its_actions_numbered_under_its_row()
    {
        var press = new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87, HoldMs = 10 };
        var component = new LibraryComponent { Name = "Press F24", Concept = "press_f24", Actions = { press } };
        var library = new ComponentLibrary(_folder.File(ComponentLibrary.FileName));
        library.Collections.Add(new LibraryCollection { Name = "Keys", Components = { component } });
        var macro = library.Resolve(new MacroDefinition
        {
            Name = "Linked",
            Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id } },
        })!;

        string log = RunWithLog(macro);

        Assert.Contains("#1 Component \"Press F24\": 1 action(s)", log);
        Assert.Contains("#1 (Press F24) 1 Press F24, held 10 ms", log);
        Assert.Contains("Ends: finished", log);
    }

    [Fact]
    public void A_run_without_a_log_writes_nothing()
    {
        var macro = new MacroDefinition { Name = "Quiet", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x87, HoldMs = 10 } } };
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), null, RecognitionDevice.Cpu, _screen);
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Null(DebugStore.LastRunOf(macro.Id));
        Assert.Equal("finished", runner.EndReason);
    }
}
