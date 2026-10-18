using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The AI tools that use the screen (FakeAiHost as the app and the user): look_at_screen and the pictures it keeps for
/// take_image and pick_pixel, check_condition, record_macro and its frames, test_macro and get_last_run, and the two
/// permissions of the AI page.
/// </summary>
// RunLog.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(RunLog))]
public class AiScreenToolsTests : IDisposable
{
    private const string App = "Claude Desktop";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiTools _tools;

    private static readonly object Asking = new
    {
        title = "Show the game",
        description = "We are making a macro that waits for the bomb.",
        steps = new[] { "Open the game", "Press Enter" },
    };

    public AiScreenToolsTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        RunLog.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null) =>
        _tools.Call(tool, JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static object With(object extra) =>
        JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(JsonSerializer.Serialize(Asking))!
            .Concat(JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(JsonSerializer.Serialize(extra))!)
            .ToDictionary(p => p.Key, p => p.Value);

    private static string SnapshotId(AiToolResult result) => Regex.Match(result.Text, "\"SnapshotId\": \"([0-9a-f]+)\"").Groups[1].Value;

    [Fact]
    public async Task Look_at_screen_shows_each_monitor_after_the_ready_window_and_keeps_the_picture()
    {
        AiToolResult look = await Call("look_at_screen", Asking);

        Assert.False(look.IsError, look.Text);
        Assert.Equal("Show the game", Assert.Single(_host.ReadyShown).Title);
        Assert.Single(look.Images);
        Assert.Contains("\"Width\": 800", look.Text);
        string snapshot = SnapshotId(look);
        Assert.NotEmpty(snapshot);

        // From that picture, without the user: a pixel, and an image of the pattern.
        AiToolResult pixel = await Call("pick_pixel", new { snapshot_id = snapshot, x = 5, y = 5 });
        Assert.Contains("\"Color\":\"#FFFFFF\"", pixel.Text);
        AiToolResult image = await Call("take_image", new { snapshot_id = snapshot, area = new { X = 100, Y = 100, Width = 40, Height = 40 } });
        Assert.False(image.IsError, image.Text);
        string id = Regex.Match(image.Text, "\"Id\":\"([0-9a-f]{64})\"").Groups[1].Value;
        Assert.True(_host.Macros.Images.Has(id));
        Assert.Single(_host.ReadyShown); // the user was asked only once

        // A flat area: an Image condition can't find it.
        AiToolResult flat = await Call("take_image", new { snapshot_id = snapshot, area = new { X = 300, Y = 300, Width = 40, Height = 40 } });
        Assert.True(flat.IsError);
        Assert.Contains("Pixel color", flat.Text);
    }

    [Fact]
    public async Task Look_at_screen_with_an_area_gives_that_part_at_full_size()
    {
        AiToolResult look = await Call("look_at_screen", With(new { area = new { X = 100, Y = 100, Width = 40, Height = 40 } }));

        Assert.False(look.IsError, look.Text);
        Assert.Contains("\"ImageWidth\": 40", look.Text);
        Assert.Contains("\"Scale\": 1", look.Text);
    }

    [Fact]
    public async Task Without_Let_AI_apps_see_the_screen_nothing_of_the_screen_is_given()
    {
        _settings.AiSeeScreen = false;

        Assert.Contains("Let AI apps see the screen", (await Call("look_at_screen", Asking)).Text);
        Assert.True((await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF" } }))).IsError);
        AiToolResult recorded = await Call("record_macro", Asking);
        Assert.False(recorded.IsError);
        Assert.Contains("no pictures", recorded.Text);
        Assert.Empty(recorded.Images);
        Assert.DoesNotContain(_host.ReadyShown, a => a.Title != "Show the game");
    }

    [Fact]
    public async Task Check_condition_says_what_PuppyMacro_sees_with_Not_applied()
    {
        AiToolResult there = await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF", Tolerance = 5 } }));
        Assert.False(there.IsError, there.Text);
        Assert.Contains("\"True\": true", there.Text);
        Assert.Equal("#FFFFFF", Assert.Single(_host.Checked).Color);

        AiToolResult not = await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF", Not = true } }));
        Assert.Contains("\"True\": false", not.Text);
        Assert.Contains("\"There\": true", not.Text);
    }

    [Fact]
    public async Task Check_condition_refuses_a_condition_the_editor_would_refuse()
    {
        AiToolResult noColor = await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5 } }));
        Assert.True(noColor.IsError);
        Assert.Contains("problems", noColor.Text);

        AiToolResult unknownImage = await Call("check_condition", With(new
        {
            condition = new { Kind = "Image", Name = "Bomb", Similarity = 90, Images = new[] { new { Id = new string('a', 64), X = 1, Y = 1 } } },
        }));
        Assert.True(unknownImage.IsError);
        Assert.Empty(_host.ReadyShown);
    }

    [Fact]
    public async Task Record_macro_gives_the_actions_and_an_image_around_each_click()
    {
        AiToolResult recorded = await Call("record_macro", Asking);

        Assert.False(recorded.IsError, recorded.Text);
        Assert.Contains("\"Type\": \"Click\"", recorded.Text);
        string imageId = Regex.Match(recorded.Text, "\"Id\": \"([0-9a-f]{64})\"").Groups[1].Value;
        Assert.True(_host.Macros.Images.Has(imageId));
        Assert.Contains($"\"Width\": {AiTools.ClickImageSide}", recorded.Text);

        string recording = Regex.Match(recorded.Text, "\"RecordingId\": \"([0-9a-f]+)\"").Groups[1].Value;
        AiToolResult frame = await Call("get_recording_frame", new { recording_id = recording, click = 1 });
        Assert.False(frame.IsError, frame.Text);
        Assert.Single(frame.Images);
        Assert.True((await Call("get_recording_frame", new { recording_id = recording, click = 2 })).IsError);
    }

    [Fact]
    public async Task Record_macro_cancelled_in_its_countdown_gives_nothing()
    {
        _host.RecordAnswer = AiAnswer.Cancelled;

        AiToolResult recorded = await Call("record_macro", Asking);

        Assert.Contains("cancelled", recorded.Text);
    }

    private MacroDefinition SavedMacro()
    {
        var macro = new MacroDefinition { Name = "Bomb", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        _host.Macros.Macros.Add(macro);
        return macro;
    }

    private static object TestArgs(Guid id, double? seconds = null) => With(new
    {
        id = id.ToString(),
        max_seconds = seconds ?? AiTools.DefaultTestSeconds,
        confirmed_steps = new[] { "1. Press A: drops the bomb" },
    });

    [Fact]
    public async Task Test_macro_plays_it_after_the_ready_window_and_gives_its_log()
    {
        MacroDefinition macro = SavedMacro();

        AiToolResult tested = await Call("test_macro", TestArgs(macro.Id, 5));

        Assert.False(tested.IsError, tested.Text);
        Assert.Equal((macro.Id, 5.0), Assert.Single(_host.Tested));
        Assert.True(tested.Text.Contains("The test ended: finished", StringComparison.Ordinal), tested.Text);
        Assert.Contains("#1 Press A", tested.Text);

        // The same log, later.
        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });
        Assert.Contains("#1 Press A", last.Text);
    }

    [Fact]
    public async Task Test_macro_waits_for_a_free_PuppyMacro_and_its_permission()
    {
        MacroDefinition macro = SavedMacro();
        _host.Running.Add(Guid.NewGuid());
        Assert.Contains("stop_all", (await Call("test_macro", TestArgs(macro.Id))).Text);
        _host.Running.Clear();

        _settings.AiTestMacros = false;
        Assert.Contains("Let AI apps test macros", (await Call("test_macro", TestArgs(macro.Id))).Text);
        Assert.Empty(_host.Tested);
    }

    [Fact]
    public async Task Get_last_run_says_when_there_is_no_log()
    {
        MacroDefinition macro = SavedMacro();

        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });

        Assert.False(last.IsError);
        Assert.Contains("Debug", last.Text);
    }

    [Fact]
    public void Pictures_for_the_AI_app_are_shrunk_to_the_largest_side()
    {
        using var wide = new OpenCvSharp.Mat(1440, 5120, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(10));

        var (jpeg, scale, width, height) = AiTools.ForAi(wide);

        Assert.Equal(AiTools.MaxImageSide, width);
        Assert.InRange(height, 440, 442);
        Assert.InRange(scale, 0.30, 0.31);
        Assert.Equal(0xFF, jpeg[0]);
    }

    [Fact]
    public async Task The_macro_schema_names_the_languages_Windows_can_read()
    {
        AiToolResult schema = await Call("get_schema", new { kind = "macro" });

        Assert.Contains("Windows can read", schema.Text);
        Assert.DoesNotContain("(one of the languages Windows can read on this PC)", schema.Text);
        foreach (string language in TextReader.Languages)
            Assert.Contains(language, schema.Text);
    }

    [Fact]
    public void The_permissions_are_on_by_default_and_in_older_files()
    {
        Assert.True(AppSettings.CreateDefault().AiSeeScreen);
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{\"AllowAiAccess\": true}")!;
        Assert.True(old.AiSeeScreen);
        Assert.True(old.AiTestMacros);
    }
}
