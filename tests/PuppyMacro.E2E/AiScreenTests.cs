using System;
using System.Collections.Generic;
using System.Drawing;
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
        return await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

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
        Assert.NotEmpty(look.Content.OfType<ImageContentBlock>());
        string snapshot = Regex.Match(Text(look), "\"SnapshotId\": \"([0-9a-f]+)\"").Groups[1].Value;
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
        Assert.Contains("The test ended: finished", Text(tested));
        Assert.Contains("#1 Press F22", Text(tested));
    }

    [Fact]
    public async Task Record_macro_gives_the_keys_and_the_click_with_an_image_around_it()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        using var target = new TargetWindow();
        await using McpClient client = await Connect();
        Point click = TargetWindow.PointAt(0.4, 0.5);

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
    }
}
