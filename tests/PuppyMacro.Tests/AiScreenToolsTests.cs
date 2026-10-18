using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The AI tools that use the screen (FakeAiHost as the app and the user): what they capture stays in the call's debug
/// folder and the answer links its files (never their content); look_at_screen and the picture take_image and pick_pixel
/// read again, check_condition, record_macro, test_macro and get_last_run, the Done window, the two permissions, Debug
/// kept for the user, and the MCP side (full names, titles, annotations, the rate limit).
/// </summary>
// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
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
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static object With(object extra) =>
        JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(JsonSerializer.Serialize(Asking))!
            .Concat(JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(JsonSerializer.Serialize(extra))!)
            .ToDictionary(p => p.Key, p => p.Value);

    private static string SnapshotId(AiToolResult result) => Regex.Match(result.Text, "\"SnapshotId\": \"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task Look_at_screen_links_each_monitor_s_picture_and_take_image_and_pick_pixel_read_it()
    {
        AiToolResult look = await Call("look_at_screen", Asking);

        Assert.False(look.IsError, look.Text);
        Assert.Equal("Show the game", Assert.Single(_host.ReadyShown).Title);
        string picture = Assert.Single(look.Files, f => f.EndsWith(".png", StringComparison.Ordinal));
        using (var saved = OpenCvSharp.Cv2.ImRead(picture))
            Assert.Equal(800, saved.Width); // the screen's own size, not shrunk
        Assert.Equal(new[] { App }, _host.FinishedFor);
        string snapshot = SnapshotId(look);

        AiToolResult pixel = await Call("pick_pixel", new { snapshot_id = snapshot, x = 5, y = 5 });
        Assert.Contains("\"Color\":\"#FFFFFF\"", pixel.Text);
        AiToolResult image = await Call("take_image", new { snapshot_id = snapshot, area = new { X = 100, Y = 100, Width = 40, Height = 40 } });
        Assert.False(image.IsError, image.Text);
        Assert.True(_host.Macros.Images.Has(Regex.Match(image.Text, "\"Id\":\"([0-9a-f]{64})\"").Groups[1].Value));
        Assert.Single(_host.ReadyShown); // the user was asked only once

        AiToolResult flat = await Call("take_image", new { snapshot_id = snapshot, area = new { X = 300, Y = 300, Width = 40, Height = 40 } });
        Assert.True(flat.IsError);
        Assert.Contains("Pixel color", flat.Text);
    }

    [Fact]
    public async Task Without_Let_AI_apps_see_the_screen_nothing_of_the_screen_is_given()
    {
        _settings.AiSeeScreen = false;

        Assert.Contains("Let AI apps see the screen", (await Call("look_at_screen", Asking)).Text);
        Assert.True((await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF" } }))).IsError);
        AiToolResult recorded = await Call("record_macro", Asking);
        Assert.False(recorded.IsError, recorded.Text);
        Assert.DoesNotContain(recorded.Files, f => f.EndsWith(".png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_condition_says_what_PuppyMacro_sees_with_Not_applied()
    {
        AiToolResult there = await Call("check_condition", With(new { condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF", Tolerance = 5 } }));
        Assert.False(there.IsError, there.Text);
        Assert.Contains("\"True\": true", there.Text);

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
        Assert.Empty(_host.ReadyShown);
    }

    [Fact]
    public async Task Record_macro_gives_the_actions_and_links_the_recording_s_log_and_the_screen_at_each_input()
    {
        AiToolResult recorded = await Call("record_macro", Asking);

        Assert.False(recorded.IsError, recorded.Text);
        Assert.Contains("\"Type\": \"Click\"", recorded.Text);
        Assert.True(_host.Macros.Images.Has(Regex.Match(recorded.Text, "\"Id\": \"([0-9a-f]{64})\"").Groups[1].Value));
        Assert.Equal(RunLog.LogFile, Path.GetFileName(recorded.Files[0]));
        string log = File.ReadAllText(recorded.Files[0]);
        Assert.Contains("Key down A", log);
        Assert.Contains("Click Left at 120,120 at +0.600 s of the recording → 0002.png", log);
        Assert.Contains(recorded.Files, f => Path.GetFileName(f) == "0002.png");
        Assert.Contains(recorded.Files, f => Path.GetFileName(f) == "actions.json");
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
    public async Task Test_macro_says_how_it_ended_and_links_its_log_and_pictures()
    {
        MacroDefinition macro = SavedMacro();

        AiToolResult tested = await Call("test_macro", TestArgs(macro.Id, 5));

        Assert.False(tested.IsError, tested.Text);
        Assert.Equal(5.0, Assert.Single(_host.Tested).MaxSeconds);
        Assert.StartsWith("The test ended: finished.", tested.Text);
        Assert.DoesNotContain("#1 Press A", tested.Text); // the log is linked, not in the answer
        Assert.Equal(RunLog.LogFile, Path.GetFileName(tested.Files[0]));
        Assert.Contains("#1 Press A", File.ReadAllText(tested.Files[0]));
        Assert.Contains(tested.Files, f => Path.GetFileName(f) == "0001.png");

        // The same run, later.
        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });
        Assert.StartsWith("Its last run ended: finished.", last.Text);
        Assert.Equal(tested.Files, last.Files);
    }

    [Fact]
    public async Task Test_macro_waits_for_a_free_PuppyMacro_and_its_permission()
    {
        MacroDefinition macro = SavedMacro();
        _host.Running.Add(Guid.NewGuid());
        Assert.Contains("puppymacro_stop_all", (await Call("test_macro", TestArgs(macro.Id))).Text);
        _host.Running.Clear();

        _settings.AiTestMacros = false;
        Assert.Contains("Let AI apps test macros", (await Call("test_macro", TestArgs(macro.Id))).Text);
        Assert.Empty(_host.Tested);
    }

    [Fact]
    public async Task Get_last_run_says_when_there_is_no_run()
    {
        MacroDefinition macro = SavedMacro();

        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });

        Assert.False(last.IsError);
        Assert.Contains("Debug", last.Text);
        Assert.Empty(last.Files);
    }

    [Fact]
    public void Every_tool_has_its_full_name_a_title_and_honest_annotations()
    {
        Assert.All(_tools.All, t =>
        {
            Assert.StartsWith(AiTools.Prefix, t.Name);
            Assert.Matches("^[A-Za-z0-9_.-]{1,128}$", t.Name);
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Name);
            // Every tool named in a description is named in full.
            Assert.DoesNotMatch(@"(?<![A-Za-z0-9_])(get_item|wait_for_user|get_schema|get_overview|save_macro|patch_item)\b", t.Description);
        });
        Assert.DoesNotContain(_tools.All, t => t.Name is "puppymacro_pick_point" or "puppymacro_get_status" or "puppymacro_get_recording_frame");
        AiTool test = _tools.All.Single(t => t.Name == "puppymacro_test_macro");
        Assert.True(test.Destructive);
        Assert.False(test.ReadOnly);
        Assert.True(_tools.All.Single(t => t.Name == "puppymacro_record_macro").ReadOnly);
        Assert.True(_tools.All.Single(t => t.Name == "puppymacro_get_item").Idempotent);
        Assert.False(_tools.All.Single(t => t.Name == "puppymacro_pick_points").Idempotent);
        Assert.Contains("puppymacro_wait_for_user", AiTools.InstructionsText);
    }

    [Fact]
    public void The_rate_limit_lets_a_burst_through_then_asks_to_wait_and_refills()
    {
        double now = 0;
        var limit = new AiRateLimit(() => now);

        for (int i = 0; i < AiRateLimit.Burst; i++)
            Assert.Null(limit.Wait());
        Assert.NotNull(limit.Wait());

        now += 1;
        for (int i = 0; i < AiRateLimit.PerSecond; i++)
            Assert.Null(limit.Wait());
        Assert.NotNull(limit.Wait());
    }

    [Fact]
    public void An_AI_app_s_save_keeps_the_macro_s_Debug_as_the_user_set_it()
    {
        MacroDefinition mine = SavedMacro();
        mine.Debug = true;

        var changed = new MacroDefinition { Id = mine.Id, Name = "Bomb", Debug = false };
        AiTools.KeepUsersDebug(changed, _host.Macros);
        var added = new MacroDefinition { Name = "New", Debug = true };
        AiTools.KeepUsersDebug(added, _host.Macros);

        Assert.True(changed.Debug);
        Assert.False(added.Debug);
    }

    [Fact]
    public async Task The_overview_says_whether_overlay_mode_is_on_and_a_request_waits()
    {
        _host.OverlayModeActive = true;

        AiToolResult overview = await Call("get_overview");

        Assert.Contains("\"OverlayModeOn\": true", overview.Text);
        Assert.Contains("\"WaitingForUser\"", overview.Text);
    }

    [Fact]
    public void The_permissions_are_on_by_default_and_in_older_files()
    {
        Assert.True(AppSettings.CreateDefault().AiSeeScreen);
        AppSettings old = JsonSerializer.Deserialize<AppSettings>("{\"AllowAiAccess\": true}")!;
        Assert.True(old.AiSeeScreen);
        Assert.True(old.AiTestMacros);
    }

    [Fact]
    public async Task The_macro_schema_names_the_languages_Windows_can_read()
    {
        AiToolResult schema = await Call("get_schema", new { kind = "macro" });

        Assert.Contains("Windows can read", schema.Text);
        Assert.DoesNotContain("(one of the languages Windows can read on this PC)", schema.Text);
    }
}
