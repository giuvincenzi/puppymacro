using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Unlink and Save as component keep what a macro does: the macro played before and after the edit, on a fake screen,
/// presses the same keys, also when a condition of an Any found nothing this time (the place a {"Found"} argument or a
/// "Where … was found" takes is missing). The keys pressed are F24 only; a click whose place is missing is skipped, never
/// sent.
/// </summary>
[Collection(nameof(DebugStore))]
public class ComponentEditsPlaybackTests : IDisposable
{
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();
    private readonly ImageStore _images;

    public ComponentEditsPlaybackTests()
    {
        DebugStore.Root = _folder.File("debug");
        _images = new ImageStore(_folder.File("images"));
        using var black = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3, Scalar.All(0));
        _screen.Show(black);
    }

    public void Dispose()
    {
        _screen.Dispose();
        _folder.Dispose();
    }

    private string Run(MacroDefinition macro, IEnumerable<LibraryComponent> components)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "run-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var log = new RunLog(folder, macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), _images, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) },
            ComponentPrograms.Prepare(components));
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(RunLog.WaitWritten(Written));
        return RunLog.Read(log.Folder)!;
    }

    /// <summary>An image condition whose image is not in the store: never found, so it has no place this time.</summary>
    private static MacroCondition Missing() => new()
    {
        Kind = ConditionKind.Image, Name = "Missing", Similarity = 90,
        Images = new() { new ConditionImage { Id = ImageA, X = 10, Y = 10 } },
    };

    /// <summary>"Strip": presses F24 (held <paramref name="hold"/> ms), then clicks at Slot.</summary>
    private static LibraryComponent Strip(double hold) =>
        Comp("Strip", new[] { SlotParam() }, Press(0x87, hold), Bound(Click(), ("Point", "Slot")));

    /// <summary>An If true by its pixel (the fake screen is black at 5, 5) while its image is not found.</summary>
    private static MacroAction AnyIf(MacroCondition missing, params MacroAction[] then) =>
        If(new[] { Pixel(5, 5, "#000000"), missing }, ConditionMatch.Any, then);

    [Fact]
    public void A_row_and_its_unlinked_actions_press_the_same_keys_when_its_place_was_found()
    {
        // The passing side: the place is there (no Any), both press F24 once.
        var strip = Comp("Press only", new[] { HoldParam("Hold") }, Bound(Press(0x87, 10), ("HoldMs", "Hold")));
        var row = Call(strip, ("Hold", Number(31)));
        var unlinked = ComponentEdits.Unlink(row, strip, out string? problem)!;
        Assert.Null(problem);

        string linked = Run(Macro("Linked", If(Pixel(5, 5, "#000000"), row)), new[] { strip });
        string flat = Run(Macro("Unlinked", If(Pixel(5, 5, "#000000"), unlinked.ToArray())), new[] { strip });

        Assert.Contains("held 31 ms", linked);
        Assert.Contains("held 31 ms", flat);
    }

    [Fact]
    public void Unlink_keeps_what_the_row_plays_when_the_place_it_passes_was_not_found_this_time()
    {
        var missing = Missing();
        var strip = Strip(hold: 23);
        var row = Call(strip, ("Slot", Found(missing.Id)));
        var unlinked = ComponentEdits.Unlink(row, strip, out string? problem)!;
        Assert.Null(problem);
        // Both macros pass the checks: the user can save either.
        Assert.Empty(MacroProblems(Macro("Linked", AnyIf(missing, row)), strip));
        Assert.Empty(MacroProblems(Macro("Unlinked", AnyIf(missing, unlinked.Select(a => a.Clone()).ToArray())), strip));

        string linked = Run(Macro("Linked", AnyIf(missing, row)), new[] { strip });
        string flat = Run(Macro("Unlinked", AnyIf(missing, unlinked.ToArray())), new[] { strip });

        // The row plays as its rows do (the user's choice): F24 is pressed, only the click where nothing was found is skipped.
        Assert.Contains("held 23 ms", linked);
        Assert.Contains("skipped, its condition found nothing this time", linked);
        Assert.DoesNotContain("has no value this time", linked);
        // Its unlinked actions play the same.
        Assert.Contains("held 23 ms", flat);
        Assert.Contains("skipped, its condition found nothing this time", flat);
    }

    [Fact]
    public void Save_as_component_keeps_what_the_actions_play_when_the_place_they_use_was_not_found_this_time()
    {
        var missing = Missing();
        var selected = new[] { Press(0x87, 24), Click(found: missing.Id) };
        var (actions, parameters, arguments) = ComponentEdits.Lift(selected, null, new Dictionary<Guid, string> { [missing.Id] = "Missing" });
        var component = Comp("Lifted", parameters, actions.ToArray());
        var row = new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id, Arguments = arguments };
        Assert.Empty(ComponentProblems(component));
        Assert.Empty(MacroProblems(Macro("Lifted", AnyIf(missing, row)), component));

        string before = Run(Macro("Before", AnyIf(missing, selected.Select(a => a.Clone()).ToArray())), Array.Empty<LibraryComponent>());
        string after = Run(Macro("After", AnyIf(missing, row)), new[] { component });

        // Before: F24 is pressed, only the click is skipped.
        Assert.Contains("held 24 ms", before);
        // After Save as component the same F24 must be pressed.
        Assert.Contains("held 24 ms", after);
    }
}

