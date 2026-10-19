using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>
/// The AI tools that use the screen, called by the test as an AI app (through the bridge): look_at_screen and a pixel read
/// from its picture, check_condition, test_macro on the target window, record_macro of keys and a click there. Each one
/// starts with the ready window, answered with Start. Its keys are F22 and F18 (the Record hotkey). make e2e category=AI
/// </summary>
[Trait("Category", "AI")]
public class AiScreenTests
{
    private const string ClientName = "E2E test client";

    private static readonly object Ready = new
    {
        title = "E2E screen",
        description = "The E2E test checks the screen tools.",
        steps = new[] { "Press Start" },
    };

    private static async Task<McpClient> Connect() => await McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions { Command = AppSession.ExePath, Arguments = new[] { "--mcp" }, Name = "PuppyMacro" }),
        new McpClientOptions { ClientInfo = new Implementation { Name = ClientName, Version = "1.0" } },
        cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<CallToolResult> Call(McpClient client, string tool, object args)
    {
        Dictionary<string, object?> arguments =
            JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return await client.CallToolAsync("puppymacro_" + tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The answer: the first text block (a note after it, the first answer's rules and Library, is a block of its own).</summary>
    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";

    /// <summary>The answer's links (MCP resource links) by file name: the answer itself carries no file's content.</summary>
    private static Dictionary<string, ResourceLinkBlock> Links(CallToolResult result)
    {
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        return result.Content.OfType<ResourceLinkBlock>().ToDictionary(l => l.Name);
    }

    /// <summary>A linked file read as an AI app reads it (resources/read): a log's text.</summary>
    private static async Task<string> ReadText(McpClient client, ResourceLinkBlock link)
    {
        ReadResourceResult read = await client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken);
        return Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
    }

    /// <summary>The Done window after a request on the screen, closed with OK.</summary>
    private static void CloseDone(AppSession app)
    {
        AutomationElement done = app.TopWindow("Done") ?? throw new InvalidOperationException("no Done window");
        app.FindButton(done, "OK").Invoke();
    }

    private static object With(object extra) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(Ready))!
            .Concat(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(extra))!)
            .ToDictionary(p => p.Key, p => p.Value);

    /// <summary>The ready window of the request, answered with Start.</summary>
    private static void PressStart(AppSession app)
    {
        Window ready = Retry.WhileNull(() => app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
                .FirstOrDefault(w => AppSession.HasTitle(w.Name, "AI request"))?.AsWindow(), AppSession.Timeout,
            throwOnTimeout: true, timeoutMessage: "no ready window").Result!;
        Assert.True(app.Has(ready, "E2E screen"));
        app.FindButton(ready, "Start").Invoke();
    }

    [Fact]
    public async Task Look_at_screen_gives_a_picture_and_a_pixel_of_it()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<CallToolResult> call = Call(client, "look_at_screen", Ready);
        PressStart(app);
        CallToolResult look = await call;

        Assert.True(look.IsError != true, Text(look));
        CloseDone(app);
        // The picture is linked, and read as a PNG of the screen's own size.
        ResourceLinkBlock picture = Links(look)["monitor-1.png"];
        Assert.Equal("image/png", picture.MimeType);
        ReadResourceResult read = await client.ReadResourceAsync(picture.Uri, cancellationToken: TestContext.Current.CancellationToken);
        var png = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("image/png", png.MimeType);
        Assert.True(png.DecodedData.Length > 1000);
        Assert.Matches(@"^monitor 1, PNG, \d+ × \d+ px$", picture.Description);
        // resources/read is in the debug data too, as a tool call is.
        string debug = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuppyMacro Dev", "debug", "ai");
        Assert.True(Retry.WhileFalse(() => Directory.GetDirectories(debug, "*-resources_read-monitor-1-png", SearchOption.AllDirectories)
            .Any(folder => File.Exists(Path.Combine(folder, "response.json"))), AppSession.Timeout).Success, "no resources/read in the debug data");
        // read_resource, for AI apps that cannot open resource links: the same file, or a smaller copy.
        CallToolResult same = await Call(client, "read_resource", new { uri = picture.Uri });
        Assert.Equal(png.DecodedData.ToArray(), Assert.Single(same.Content.OfType<ImageContentBlock>()).DecodedData.ToArray());
        CallToolResult half = await Call(client, "read_resource", new { uri = picture.Uri, scale = 0.5 });
        Assert.Single(half.Content.OfType<ImageContentBlock>());
        Assert.Contains("at scale 0.5", Text(half));
        string snapshot = Regex.Match(Text(look), "\"SnapshotId\": \"([^\"]+)\"").Groups[1].Value;
        CallToolResult pixel = await Call(client, "pick_pixel", new { snapshot_id = snapshot, x = 10, y = 10 });
        Assert.Matches("\"Color\":\"#[0-9A-F]{6}\"", Text(pixel));
    }

    [Fact]
    public async Task Check_condition_says_what_a_pixel_shows_now()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        using var target = new TargetWindow();
        target.Activate();
        await using McpClient client = await Connect();
        Point inside = TargetWindow.PointAt(0.5, 0.5);

        Task<CallToolResult> call = Call(client, "check_condition",
            With(new { condition = new { Kind = "Pixel", X = inside.X, Y = inside.Y, Color = "#123456", Tolerance = 0 } }));
        PressStart(app);
        CallToolResult check = await call;

        Assert.True(check.IsError != true, Text(check));
        Assert.Contains("\"True\": false", Text(check)); // the target window is not that color
        Assert.Matches("\"Seen\": \"#[0-9A-F]{6}\"", Text(check));
    }

    [Fact]
    public async Task Test_macro_plays_on_the_app_in_front_and_gives_its_log()
    {
        MacroDefinition macro = Macro("E2E tested", null, Press(Vk.F22));
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Macros.Add(macro);
        });
        using var target = new TargetWindow();
        target.Activate();
        await using McpClient client = await Connect();

        Task<CallToolResult> call = Call(client, "test_macro",
            With(new { id = macro.Id.ToString(), max_seconds = 10, confirmed_steps = new[] { "1. Press F22" } }));
        PressStart(app);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 15, "the tested macro did not press F22 on the window in front");
        CallToolResult tested = await call;

        Assert.True(tested.IsError != true, Text(tested));
        Assert.StartsWith("The test ended: finished", Text(tested));
        CloseDone(app);
        Assert.Contains("#1 Press F22", await ReadText(client, Links(tested)["log.txt"]));
        Assert.Contains("0001.png", Links(tested).Keys);

        // Outside the debug data, nothing is read.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.ReadResourceAsync("puppymacro://debug/../../secret.txt", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Record_macro_gives_the_keys_and_the_click_with_an_image_around_it()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        using var target = new TargetWindow();
        await using McpClient client = await Connect();
        // A pattern to click: an image around a click on a flat area is left out (an Image condition can't find one color).
        Point at = TargetWindow.PointAt(0.4, 0.5);
        target.ShowPicture(ConditionTests.Pattern(80, 60, 7), at);
        Point click = new(at.X + 40, at.Y + 30);

        Task<CallToolResult> call = Call(client, "record_macro", With(new { mouse_movement = false }));
        PressStart(app);
        target.Activate();
        AutomationElement bar = app.TopWindow("Recording") ?? throw new InvalidOperationException("no recording bar");
        AutomationElement countdown = app.FindById(bar, "CountdownText");
        Assert.True(Retry.WhileFalse(() => countdown.IsOffscreen || !countdown.IsAvailable, TimeSpan.FromSeconds(8)).Success, "the countdown did not end");
        target.Tap(Vk.F22);
        target.Click(click);
        target.Tap(Vk.F18); // the Record hotkey stops it
        CallToolResult recorded = await call;

        string text = Text(recorded);
        Assert.True(recorded.IsError != true, text);
        Assert.Contains("\"Type\": \"PressKey\"", text);
        Assert.Contains("\"Type\": \"Click\"", text);
        Assert.Matches("\"Id\": \"[0-9a-f]{64}\"", text);
        Assert.DoesNotContain($"\"Vk\": {Vk.F18}", text); // the Record hotkey is not recorded
        CloseDone(app);
        string log = await ReadText(client, Links(recorded)["log.txt"]);
        Assert.Contains("Key down F22", log);
        Assert.Contains("Click Left", log);
    }
}
