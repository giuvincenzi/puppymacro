using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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
/// Components played (ComponentPrograms, MacroRunner): a call's arguments in the caller's frame (values, defaults, {"From"},
/// {"Found"}, one that has no value this time), the bound fields filled when an action starts, a Component condition as
/// the condition its component returns; and real runs on a fake screen, written in the Debug log: two calls with their own
/// values, a value passed down three levels, numbering under each row, a Component condition (with Not), a place found by
/// an image search passed to a component, frames that never mix their places, a call skipped, the depth guard. The keys
/// pressed are F24 only.
/// </summary>
[Collection(nameof(DebugStore))]
public class ComponentRuntimeTests : IDisposable
{
    private static readonly TimeSpan Written = TimeSpan.FromSeconds(10);
    private readonly TempFolder _folder = new();
    private readonly ConditionCheckerTests.FakeScreen _screen = new();
    private readonly ImageStore _images;

    public ComponentRuntimeTests()
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

    private string Run(MacroDefinition macro, IEnumerable<LibraryComponent> components) =>
        Run(macro, ComponentPrograms.Prepare(components));

    private string Run(MacroDefinition macro, ComponentPrograms programs)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "run-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var log = new RunLog(folder, macro, "a test");
        var runner = new MacroRunner(macro, new ClipboardPaster(Dispatcher.CurrentDispatcher), _images, RecognitionDevice.Cpu, _screen, log,
            () => new[] { new Rect(0, 0, ConditionCheckerTests.FakeScreen.Width, ConditionCheckerTests.FakeScreen.Height) }, programs);
        using var ended = new ManualResetEventSlim();
        runner.Exited += _ => ended.Set();
        runner.Start();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the macro did not end");
        Assert.True(RunLog.WaitWritten(Written));
        return RunLog.Read(log.Folder)!;
    }

    private static readonly Dictionary<Guid, Point> Nothing = new();

    /// <summary>"Press": presses F24, held Hold (30 ms when left out).</summary>
    private static LibraryComponent PressFor() =>
        Comp("Press", new[] { HoldParam(@default: Number(30)) }, Bound(Press(0x87, 30), ("HoldMs", "Hold")));

    // ================= Arguments in a frame =================

    [Fact]
    public void A_call_s_values_come_from_the_row_the_defaults_and_the_caller_s_frame()
    {
        var component = Comp("Strip", new[] { SlotParam(), HoldParam(@default: Number(30)) }, Press());
        var prepared = ComponentPrograms.Prepare(new[] { component }).Find(component.Id)!;
        Guid condition = Guid.NewGuid();
        var found = new Dictionary<Guid, Point> { [condition] = new Point(400, 300) };
        var caller = new FrameArguments(new Dictionary<string, JsonNode> { ["Where"] = Point(7, 8), ["Long"] = Number(99) });

        var values = ComponentPrograms.Evaluate(prepared, new() { ["Slot"] = Point(1, 2) }, FrameArguments.None, Nothing, out var missing)!;
        Assert.Null(missing);
        Assert.True(JsonNode.DeepEquals(Point(1, 2), values["Slot"]));
        Assert.True(JsonNode.DeepEquals(Number(30), values["Hold"])); // the default

        values = ComponentPrograms.Evaluate(prepared, new() { ["Slot"] = From("Where"), ["Hold"] = From("Long") }, caller, Nothing, out _)!;
        Assert.True(JsonNode.DeepEquals(Point(7, 8), values["Slot"]));
        Assert.True(JsonNode.DeepEquals(Number(99), values["Hold"]));

        values = ComponentPrograms.Evaluate(prepared, new() { ["Slot"] = Found(condition, 5, -10) }, FrameArguments.None, found, out _)!;
        Assert.True(JsonNode.DeepEquals(Point(405, 290), values["Slot"]));

        // A condition that found nothing this time, or a value that isn't there: no frame, the parameter named.
        Assert.Null(ComponentPrograms.Evaluate(prepared, new() { ["Slot"] = Found(Guid.NewGuid()) }, FrameArguments.None, found, out missing));
        Assert.Equal("Slot", missing);
        Assert.Null(ComponentPrograms.Evaluate(prepared, new() { ["Slot"] = From("Nowhere") }, caller, Nothing, out missing));
        Assert.Equal("Slot", missing);
        Assert.Null(ComponentPrograms.Evaluate(prepared, null, FrameArguments.None, Nothing, out missing));
        Assert.Equal("Slot", missing);
    }

    [Fact]
    public void A_frame_s_values_are_copies()
    {
        var component = Comp("Strip", new[] { SlotParam() }, Press());
        var prepared = ComponentPrograms.Prepare(new[] { component }).Find(component.Id)!;
        var arguments = new Dictionary<string, JsonNode?> { ["Slot"] = Point(1, 2) };
        var values = ComponentPrograms.Evaluate(prepared, arguments, FrameArguments.None, Nothing, out _)!;
        ((JsonObject)values["Slot"]!)["X"] = 99;
        Assert.Equal(1, (int)arguments["Slot"]!["X"]!);
    }

    [Fact]
    public void A_component_tested_alone_plays_with_its_examples()
    {
        var examples = FrameArguments.Examples(new[] { SlotParam(), HoldParam() });
        Assert.True(JsonNode.DeepEquals(Point(3912, 1056), examples["Slot"]));
        Assert.True(JsonNode.DeepEquals(Number(30), examples["Hold"]));
        Assert.Equal(2, examples.Count);
    }

    // ================= Bound fields =================

    [Fact]
    public void An_action_s_bound_fields_take_the_frame_s_values_on_a_copy()
    {
        var click = Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold"), ("DelayMs", "Wait"));
        var frame = new FrameArguments(new Dictionary<string, JsonNode> { ["Slot"] = Point(3986, 1060), ["Hold"] = Number(80), ["Wait"] = Number(5) });

        var bound = ComponentPrograms.Bound(click, frame)!;
        Assert.NotSame(click, bound);
        Assert.Equal((3986, 1060, 80d, 5d), (bound.X, bound.Y, bound.HoldMs, bound.DelayMs));
        Assert.Equal((3912, 1056, 30d, 0d), (click.X, click.Y, click.HoldMs, click.DelayMs)); // the prepared action never changes

        // Nothing bound, or a frame with no values: the action itself.
        var plain = Click();
        Assert.Same(plain, ComponentPrograms.Bound(plain, frame)!);
        Assert.Same(click, ComponentPrograms.Bound(click, FrameArguments.None)!);
        // A value the frame doesn't have leaves the field's example.
        var partly = ComponentPrograms.Bound(click, new FrameArguments(new Dictionary<string, JsonNode> { ["Hold"] = Number(70) }))!;
        Assert.Equal((3912, 1056, 70d), (partly.X, partly.Y, partly.HoldMs));
    }

    [Fact]
    public void An_If_s_wait_is_bound_and_its_branches_are_not_copied()
    {
        var block = Bound(If(Pixel(), Press()), ("WaitSeconds", "Wait"));
        block.If!.Wait = ConditionWait.UpTo;
        block.If.WaitSeconds = 2;
        var bound = ComponentPrograms.Bound(block, new FrameArguments(new Dictionary<string, JsonNode> { ["Wait"] = Number(7.5) }))!;
        Assert.Equal(7.5, bound.If!.WaitSeconds);
        Assert.Equal(2, block.If.WaitSeconds);
        Assert.Same(block.If.Then, bound.If.Then); // each action of Then is bound when it plays
        Assert.Same(block.If.Else, bound.If.Else);
    }

    [Fact]
    public void Every_kind_of_bound_field_takes_its_value()
    {
        var frame = new FrameArguments(new Dictionary<string, JsonNode>
        {
            ["Key"] = Key(0x53, ctrl: true), ["Text"] = Text("typed"), ["Steps"] = Number(4), ["Color"] = Text("#112233"),
            ["Tolerance"] = Number(9), ["Images"] = Images(ImageB), ["Similarity"] = Number(75), ["Area"] = Area(1, 2, 300, 200),
            ["Slot"] = Point(5, 6), ["Repeat"] = Number(3), ["Pause"] = Number(80),
        });
        var press = ComponentPrograms.Bound(Bound(Press(), ("Key", "Key"), ("Repeat", "Repeat"), ("RepeatPauseMs", "Pause")), frame)!;
        Assert.Equal((0x53, true, 3, 80d), (press.Vk, press.Ctrl, press.Repeat, press.RepeatPauseMs));
        Assert.Equal("typed", ComponentPrograms.Bound(Bound(new MacroAction { Type = MacroActionType.PasteText, Text = "x" }, ("Text", "Text")), frame)!.Text);
        Assert.Equal(4, ComponentPrograms.Bound(Bound(new MacroAction { Type = MacroActionType.Scroll }, ("ScrollSteps", "Steps")), frame)!.ScrollSteps);

        var pixel = ComponentPrograms.Bound(Bound(Pixel(), ("Point", "Slot"), ("Color", "Color"), ("Tolerance", "Tolerance")), frame)!;
        Assert.Equal((5, 6, "#112233", 9), (pixel.X, pixel.Y, pixel.Color, pixel.Tolerance));
        var image = ImageFinder();
        image.Area = SearchArea.Drawn;
        image = ComponentPrograms.Bound(Bound(image, ("Images", "Images"), ("Similarity", "Similarity"), ("Area", "Area")), frame)!;
        Assert.Equal((ImageB, 75, 1, 2, 300, 200), (image.Images!.Single().Id, image.Similarity, image.AreaX, image.AreaY, image.AreaWidth, image.AreaHeight));
    }

    // ================= Component conditions =================

    private static LibraryComponent Rare(string color = "#000000")
    {
        var pixel = Bound(Pixel(3912, 1056, color), ("Point", "Slot"));
        var component = Comp("Rare item in slot", new[] { SlotParam() }, If(pixel));
        component.Returns = new ComponentReturns { Found = pixel.Id };
        return component;
    }

    [Fact]
    public void A_Component_condition_is_checked_as_the_condition_its_component_returns()
    {
        var rare = Rare();
        var programs = ComponentPrograms.Prepare(new[] { rare });
        var call = CallCondition(rare, "Rare here", ("Slot", Point(7, 8)));

        var effective = programs.Effective(call, FrameArguments.None, Nothing)!;
        Assert.Equal(ConditionKind.Pixel, effective.Kind);
        Assert.Equal((7, 8), (effective.X, effective.Y));
        Assert.Equal((call.Id, "Rare here", false), (effective.Id, effective.Name, effective.Not));
        Assert.Null(effective.Bind);
        Assert.Equal(3912, programs.Find(rare.Id)!.Returned!.X); // the prepared one stays

        call.Not = true;
        Assert.True(programs.Effective(call, FrameArguments.None, Nothing)!.Not);
        // A returned condition with Not, called with Not: true when it is there.
        var gone = Rare();
        programs.Find(rare.Id)!.Returned!.Not = true;
        Assert.False(programs.Effective(call, FrameArguments.None, Nothing)!.Not);

        // Missing: the component, or a value its call needs.
        Assert.Null(programs.Effective(CallCondition(gone, "Gone", ("Slot", Point(1, 1))), FrameArguments.None, Nothing));
        Assert.Null(programs.Effective(CallCondition(rare, "No slot"), FrameArguments.None, Nothing));
        // A plain condition is only bound.
        var plain = Bound(Pixel(), ("Point", "Slot"));
        Assert.Equal(9, programs.Effective(plain, new FrameArguments(new Dictionary<string, JsonNode> { ["Slot"] = Point(9, 9) }), Nothing)!.X);
    }

    [Fact]
    public void A_component_can_return_another_s_Component_condition()
    {
        var rare = Rare();
        var inner = CallCondition(rare, "Inner", ("Slot", From("Where")));
        var wrapper = Comp("Rare there", new[] { SlotParam("Where") }, If(inner));
        wrapper.Returns = new ComponentReturns { Found = inner.Id };
        var programs = ComponentPrograms.Prepare(new[] { rare, wrapper });

        var effective = programs.Effective(CallCondition(wrapper, "Outer", ("Where", Point(11, 12))), FrameArguments.None, Nothing)!;
        Assert.Equal(ConditionKind.Pixel, effective.Kind);
        Assert.Equal((11, 12, "Outer"), (effective.X, effective.Y, effective.Name));
    }

    // ================= Runs =================

    [Fact]
    public void Each_call_plays_its_component_with_its_own_values_numbered_under_its_row()
    {
        var press = PressFor();
        var macro = Macro("Calls", Call(press, ("Hold", Number(25))), Call(press, ("Hold", Number(40))), Call(press));

        string log = Run(macro, new[] { press });

        Assert.Contains("#1 Component \"Press\": 1 action(s), with Hold = 25 ms", log);
        Assert.Contains("#1 (Press) 1 Press F24, held 25 ms", log);
        Assert.Contains("#2 (Press) 1 Press F24, held 40 ms", log);
        Assert.Contains("#3 (Press) 1 Press F24, held 30 ms", log); // the default
        Assert.Contains("Ends: finished", log);
    }

    [Fact]
    public void A_value_passes_down_three_levels_of_components()
    {
        var press = PressFor();
        var middle = Comp("Middle", new[] { HoldParam("Time") }, Call(press, ("Hold", From("Time"))));
        var top = Comp("Top", new[] { HoldParam("Long") }, Press(0x87, 15), Call(middle, ("Time", From("Long"))));
        var macro = Macro("Deep", Call(top, ("Long", Number(45))));

        string log = Run(macro, new[] { press, middle, top });

        Assert.Contains("#1 (Top) 1 Press F24, held 15 ms", log);
        Assert.Contains("#1 (Top) 2 (Middle) 1 (Press) 1 Press F24, held 45 ms", log);
    }

    [Fact]
    public void A_Component_condition_decides_an_If_with_the_call_s_values()
    {
        var rare = Rare("#000000"); // the fake screen is black
        var macro = Macro("Conditions",
            If(CallCondition(rare, "Rare at 7,7", ("Slot", Point(7, 7))), Press(0x87, 11)),
            If(CallCondition(rare, "Rare outside", ("Slot", Point(5000, 5000))), Press(0x87, 12)),
            If(new[] { NotOf(CallCondition(rare, "Not rare", ("Slot", Point(8, 8)))) }, ConditionMatch.All, Press(0x87, 13)));

        string log = Run(macro, new[] { rare });

        Assert.Contains("Pixel 7,7: saw #000000", log);
        Assert.Contains("#1 Then 1 Press F24, held 11 ms", log);
        Assert.DoesNotContain("held 12 ms", log); // its pixel is off every screen
        Assert.DoesNotContain("held 13 ms", log); // Not: the pixel is the rare color
    }

    private static MacroCondition NotOf(MacroCondition condition)
    {
        condition.Not = true;
        return condition;
    }

    /// <summary>The fake screen with two pieces of a picture: A at (100, 100), B at (400, 250), each 40 by 40.</summary>
    private (ConditionImage A, ConditionImage B) TwoImages()
    {
        var bytes = new byte[ConditionCheckerTests.FakeScreen.Width * ConditionCheckerTests.FakeScreen.Height * 3];
        new Random(7).NextBytes(bytes);
        using var picture = new Mat(ConditionCheckerTests.FakeScreen.Height, ConditionCheckerTests.FakeScreen.Width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, picture.Data, bytes.Length);
        Cv2.GaussianBlur(picture, picture, new Size(5, 5), 0);
        _screen.Show(picture);
        ConditionImage Take(Rect piece)
        {
            using var part = new Mat(picture, piece);
            Cv2.ImEncode(".png", part, out byte[] png);
            return new ConditionImage { Id = _images.Add(png), X = piece.X, Y = piece.Y };
        }
        return (Take(new Rect(100, 100, 40, 40)), Take(new Rect(400, 250, 40, 40)));
    }

    private static MacroCondition FindImage(string name, ConditionImage image) =>
        new() { Kind = ConditionKind.Image, Name = name, Images = new() { image }, Similarity = 90 };

    /// <summary>"Look at": checks the pixel at Slot (any color passes with 100% tolerance), so the log says where it looked.</summary>
    private static LibraryComponent LookAt()
    {
        var pixel = Bound(Pixel(1, 1, "#000000"), ("Point", "Slot"));
        pixel.Tolerance = 100;
        return Comp("Look at", new[] { SlotParam() }, If(pixel, Press(0x87, 10)));
    }

    [Fact]
    public void A_place_an_image_search_found_is_passed_to_a_component()
    {
        var (a, _) = TwoImages();
        var finder = FindImage("Button", a);
        var look = LookAt();
        var macro = Macro("Found", If(finder, Call(look, ("Slot", Found(finder.Id, 5, -5)))));

        string log = Run(macro, new[] { look });

        Assert.Contains("#1 Then 1 Component \"Look at\": 1 action(s), with Slot = 125, 115", log);
        Assert.Contains("Pixel 125,115", log);
    }

    [Fact]
    public void Each_frame_keeps_its_own_places()
    {
        var (a, b) = TwoImages();
        var outer = FindImage("Button", a);
        // A component whose condition has the very same id as the macro's, finding another image elsewhere.
        var inner = FindImage("Other", b);
        inner.Id = outer.Id;
        var finds = Comp("Finds elsewhere", If(inner, Press(0x87, 10)));
        var look = LookAt();
        var macro = Macro("Frames", If(outer, Call(finds), Call(look, ("Slot", Found(outer.Id)))));

        string log = Run(macro, new[] { finds, look });

        Assert.Contains("#1 Then 1 (Finds elsewhere) 1 If: true", log);
        // After the inner call, the macro's place is still its own: the center of A, not of B.
        Assert.Contains("with Slot = 120, 120", log);
        Assert.DoesNotContain("with Slot = 420, 270", log);
    }

    [Fact]
    public void A_call_whose_place_was_not_found_this_time_is_skipped()
    {
        var look = LookAt();
        var missing = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "Missing", Similarity = 90,
            Images = new() { new ConditionImage { Id = ImageA, X = 10, Y = 10 } }, // not in the store: never found
        };
        var any = If(new[] { Pixel(5, 5, "#000000"), missing }, ConditionMatch.Any, Call(look, ("Slot", Found(missing.Id))), Press(0x87, 17));

        string log = Run(Macro("Any", any), new[] { look });

        Assert.Contains("Component \"Look at\": skipped, Slot has no value this time", log);
        Assert.Contains("held 17 ms", log); // the macro goes on
    }

    [Fact]
    public void A_condition_that_can_t_be_checked_this_time_is_never_true()
    {
        var rare = Rare();
        var noSlot = CallCondition(rare, "No slot"); // its call has no Slot: it can't be checked
        var all = If(new[] { Pixel(5, 5, "#000000"), noSlot }, ConditionMatch.All, Press(0x87, 18));
        var any = If(new[] { Pixel(5, 5, "#000000"), CallCondition(rare, "No slot either") }, ConditionMatch.Any, Press(0x87, 19));

        string log = Run(Macro("Unchecked", all, any), new[] { rare });

        Assert.Contains("can't be checked this time", log);
        Assert.DoesNotContain("held 18 ms", log); // All: not true
        Assert.Contains("held 19 ms", log); // Any: the pixel decides
    }

    [Fact]
    public void A_missing_component_and_calls_too_deep_are_skipped_at_play()
    {
        // Checks refuse both before playing; the engine still never fails on them.
        var chain = new List<LibraryComponent> { Comp("L1", Press(0x87, 21)) };
        for (int level = 2; level <= 12; level++)
            chain.Add(Comp($"L{level}", Call(chain[^1])));
        string log = Run(Macro("Too deep", Call(chain[^1]), Call(Guid.NewGuid()), Press(0x87, 22)), chain);

        Assert.Contains("skipped, more than 10 components inside each other", log);
        Assert.DoesNotContain("held 21 ms", log);
        Assert.Contains("#2 Component: not in the Library, skipped", log);
        Assert.Contains("held 22 ms", log);
    }
}
