using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// The AI page and the MCP server: the test is the AI app. It starts PuppyMacro.exe --mcp (the bridge) as an AI app does,
/// calls the tools, and answers in PuppyMacro's windows as the user would. make e2e CATEGORY=AI
/// </summary>
[Trait("Category", "AI")]
public class AiTests
{
    private const string ClientName = "E2E test client";

    /// <summary>An AI app connected to the running development build through the bridge (what it wrote on stderr in the failure).</summary>
    private static async Task<McpClient> Connect()
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            return await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = AppSession.ExePath,
                    Arguments = new[] { "--mcp" },
                    Name = "PuppyMacro",
                    StandardErrorLines = errors.Enqueue,
                }),
                new McpClientOptions { ClientInfo = new Implementation { Name = ClientName, Version = "1.0" } },
                cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"The bridge did not connect. Its stderr: {string.Join(" | ", errors)}", ex);
        }
    }

    private static async Task<(string Text, bool IsError)> Call(McpClient client, string tool, object? args = null)
    {
        Dictionary<string, object?>? arguments = args == null ? null
            : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        CallToolResult result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (text, result.IsError == true);
    }

    /// <summary>
    /// A window owned by the main window while that stays visible (AI activity): UI Automation lists it under the main
    /// window, not among the top-level windows (where <see cref="AppSession.Dialog"/> looks).
    /// </summary>
    private static Window OwnedWindow(AppSession app, string title) =>
        Retry.WhileNull(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.Window)))?.AsWindow()
                ?? app.Automation.GetDesktop().FindFirstChild(cf => cf.ByName(title).And(cf.ByProcessId(app.ProcessId)))?.AsWindow(),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: $"window \"{title}\" not found").Result!;

    /// <summary>The window of an AI request; when it does not come, the failure says what PuppyMacro showed instead (an error box).</summary>
    private static Window RequestWindow(AppSession app)
    {
        Window? window = Retry.WhileNull(() => app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .FirstOrDefault(w => AppSession.HasTitle(w.Name, "AI request"))?.AsWindow(), AppSession.Timeout).Result;
        if (window != null)
            return window;
        string shown = string.Join(" || ", app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .Select(w => $"{w.Name}: {string.Join(" | ", w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).Take(8))}"));
        throw new TimeoutException($"window \"AI request\" not found. PuppyMacro's windows: {shown}");
    }

    private static AppSettings SavedSettings() =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSession.SettingsFile), Seed.JsonOptions)!;

    private static readonly object F23ToF22 = new
    {
        item = new { Source = new { Vk = Vk.F23 }, Target = new { Vk = Vk.F22 }, Note = "E2E remap" },
    };

    [Fact]
    public async Task With_AI_access_off_an_AI_app_is_refused_and_the_page_shows_it()
    {
        using var app = new AppSession();
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "get_overview");

        Assert.True(isError);
        Assert.Contains("Allow AI access", text);
        app.GoTo("AI");
        Assert.Equal(ToggleState.Off, app.FindById(app.MainWindow, "AiAccessSwitch").AsToggleButton().ToggleState);
        Assert.True(app.Has(app.MainWindow, $"Connected: {ClientName}"));
        Assert.True(app.Has(app.MainWindow, "Tried to connect while AI access was off"));
    }

    [Fact]
    public async Task Allow_AI_access_lets_an_AI_app_read_the_items()
    {
        using var app = new AppSession();
        app.GoTo("AI");
        app.FindById(app.MainWindow, "AiAccessSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => SavedSettings().AllowAiAccess, AppSession.Timeout).Success);
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "get_overview");

        Assert.False(isError, text);
        Assert.Contains("Sample: click every second", text);
        Assert.Contains("Sample: type and confirm", text);
        Assert.True(app.Has(app.MainWindow, "Read your loops, macros and remaps"));
    }

    [Fact]
    public async Task A_proposed_remap_opens_the_editor_and_is_saved_with_Save()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "propose_remap", F23ToF22);
        Window editor = app.Dialog("Add remap");
        app.FindById(editor, "ProposalBar"); // open only for a proposal (its text is not a UI Automation name)
        Assert.True(app.Has(editor, "F23"));
        app.FindById(editor, "SaveButton").AsButton().Invoke();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.StartsWith("Saved as proposed", text);
        RemapDefinition remap = Assert.Single(SavedSettings().Remaps);
        Assert.Equal(Vk.F23, remap.Source!.Vk);
        Assert.Equal(Vk.F22, remap.Target!.Vk);
        Assert.Equal("E2E remap", remap.Note);
    }

    [Fact]
    public async Task A_cancelled_proposal_changes_nothing()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "propose_remap", F23ToF22);
        app.FindById(app.Dialog("Add remap"), "CancelButton").AsButton().Invoke();
        var (text, _) = await call;

        Assert.Contains("cancelled", text);
        Assert.Empty(SavedSettings().Remaps);
    }

    [Fact]
    public async Task A_proposal_the_editor_would_refuse_opens_nothing()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "propose_remap", new { item = new { Source = new { Vk = Vk.F23 }, Target = new { Vk = Vk.F23 } } });

        Assert.True(isError);
        Assert.Contains("Send something different from what you press.", text);
        Assert.False(Retry.WhileNull(() => app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .FirstOrDefault(w => AppSession.HasTitle(w.Name, "Add remap")), TimeSpan.FromSeconds(2)).Success);
    }

    [Fact]
    public async Task Deleting_asks_the_user_first()
    {
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(Vk.F23), Target = HotkeyBinding.FromKey(Vk.F22) };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.Remaps.Add(remap);
        });
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "delete_item", new { id = remap.Id.ToString() });
        Window confirm = RequestWindow(app);
        Assert.True(app.Has(confirm, "Delete this remap?"));
        Assert.True(app.Has(confirm, $"Asked by {ClientName}"));
        app.FindButton(confirm, "Delete").Invoke();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.StartsWith("Deleted", text);
        Assert.Empty(SavedSettings().Remaps);
    }

    [Fact]
    public async Task A_key_asked_by_an_AI_app_is_pressed_by_the_user()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "capture_hotkey", new { reason = "The key to remap" });
        Window window = RequestWindow(app);
        Assert.True(app.Has(window, "The key to remap"));
        app.Find(window, "Press the key or combination (Esc cancels)");
        Keyboard.Press(VirtualKeyShort.F22);
        Keyboard.Release(VirtualKeyShort.F22);
        app.Find(window, "F22");
        app.FindButton(window, "OK").Invoke();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.StartsWith("F22", text);
    }

    [Fact]
    public async Task A_point_asked_by_an_AI_app_is_clicked_by_the_user()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "pick_point", new { reason = "Click the test point" });
        Assert.NotNull(app.TopWindow("Pick a point"));
        Assert.True(app.Has(app.TopWindow("Pick a point")!, $"{ClientName}: Click the test point"));
        Mouse.Click(new System.Drawing.Point(400, 300)); // on the picker, which covers the screen: nothing else gets it
        var (text, isError) = await call;

        Assert.False(isError, text);
        using JsonDocument point = JsonDocument.Parse(text);
        Assert.Equal(400, point.RootElement.GetProperty("X").GetInt32());
        Assert.Equal(300, point.RootElement.GetProperty("Y").GetInt32());
    }

    [Fact]
    public async Task View_all_shows_every_request()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using (McpClient client = await Connect())
        {
            await Call(client, "get_overview");
            await Call(client, "get_help", new { topic = "remap" });
        }

        app.GoTo("AI");
        app.FindById(app.MainWindow, "AiViewAllButton").AsButton().Invoke();
        Window activity = OwnedWindow(app, "AI activity");

        Assert.True(app.Has(activity, "Read your loops, macros and remaps"));
        Assert.True(app.Has(activity, "Read the guide: Remap"));
        activity.Close();
    }
}
