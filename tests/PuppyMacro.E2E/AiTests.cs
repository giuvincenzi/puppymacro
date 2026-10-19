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
/// calls the tools, and answers in PuppyMacro's windows as the user would. make e2e category=AI
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
        // The tools' full names (puppymacro_…): the tests name them without the prefix.
        CallToolResult result = await client.CallToolAsync("puppymacro_" + tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        // The answer is the first text block; a note after it (the first answer's rules and Library) is a block of its own.
        string text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
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
        // The status bar names the connected app (its own text: the Apps row says "Connected: …").
        Assert.True(app.Has(app.MainWindow, ClientName));
        Assert.True(app.HasRow(app.FindById(app.MainWindow, "AiStatusList"), ClientName));
    }

    [Fact]
    public async Task The_first_answer_of_a_connection_gives_the_rules_and_the_Library_once_in_a_block_of_its_own()
    {
        using var app = new AppSession();
        app.GoTo("AI");
        app.FindById(app.MainWindow, "AiAccessSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => SavedSettings().AllowAiAccess, AppSession.Timeout).Success);
        await using McpClient client = await Connect();

        CallToolResult first = await client.CallToolAsync("puppymacro_get_settings", cancellationToken: TestContext.Current.CancellationToken);
        CallToolResult second = await client.CallToolAsync("puppymacro_get_settings", cancellationToken: TestContext.Current.CancellationToken);

        var blocks = first.Content.OfType<TextContentBlock>().Select(b => b.Text).ToList();
        Assert.Equal(2, blocks.Count);
        using (JsonDocument.Parse(blocks[0])) { } // the answer itself stays JSON
        Assert.StartsWith("PuppyMacro's rules for AI apps:", blocks[1]);
        Assert.Contains("\"Sample: Notepad\" (notepad.exe) 1", blocks[1]); // the development build's sample collections
        Assert.Single(second.Content.OfType<TextContentBlock>()); // once per connection
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
        Assert.True(app.HasRow(app.FindById(app.MainWindow, "AiRecentList"), ClientName, "Read your loops, macros and remaps"));
    }

    [Fact]
    public async Task A_remap_from_an_AI_app_is_saved_at_once_and_its_History_says_who()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "save_remap", F23ToF22);

        Assert.False(isError, text);
        Assert.StartsWith("Saved the new remap", text);
        RemapDefinition remap = Assert.Single(Retry.WhileNull(() => SavedSettings().Remaps is { Count: 1 } r ? r : null, AppSession.Timeout).Result!);
        Assert.Equal(Vk.F23, remap.Source!.Vk);
        Assert.Equal("E2E remap", remap.Note);

        // Its editor's History: created by the AI app.
        app.GoTo("Remap");
        app.EditItem("E2E remap");
        Window editor = app.Dialog("Edit remap");
        app.FindById(editor, "HistoryViewButton").Patterns.SelectionItem.Pattern.Select();
        Assert.True(app.Has(editor, ClientName));
        Assert.True(app.Has(editor, "Created (current)"));
        Assert.True(app.HasRow(app.FindById(editor, "VersionList"), ClientName, "Created (current)"));
        app.FindById(editor, "CancelButton").AsButton().Invoke();
    }

    [Fact]
    public async Task With_ask_before_changing_a_cancelled_change_saves_nothing()
    {
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.AiAskBeforeChanging = true;
        });
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "save_remap", F23ToF22);
        Window confirm = RequestWindow(app);
        Assert.True(app.Has(confirm, "Save this new remap?"));
        app.FindButton(confirm, "Cancel").Invoke();
        var (text, _) = await call;

        Assert.Contains("cancelled", text);
        Assert.Empty(SavedSettings().Remaps);
    }

    [Fact]
    public async Task A_remap_the_editor_would_refuse_is_not_saved()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "save_remap", new { item = new { Source = new { Vk = Vk.F23 }, Target = new { Vk = Vk.F23 } } });

        Assert.True(isError);
        Assert.Contains("Send something different from what you press.", text);
        Assert.Empty(SavedSettings().Remaps);
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
    public async Task Restore_brings_back_what_an_AI_app_deleted()
    {
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(Vk.F23), Target = HotkeyBinding.FromKey(Vk.F22), Note = "Keep me" };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.AiAskBeforeDeleting = false;
            seed.Settings.Remaps.Add(remap);
        });
        await using (McpClient client = await Connect())
        {
            var (text, isError) = await Call(client, "delete_item", new { id = remap.Id.ToString() });
            Assert.False(isError, text);
        }
        Assert.Empty(SavedSettings().Remaps);

        app.GoTo("AI");
        app.Find(app.MainWindow, $"Restore: Deleted remap F23 → F22").AsButton().Invoke();

        RemapDefinition back = Assert.Single(Retry.WhileNull(() => SavedSettings().Remaps is { Count: 1 } r ? r : null, AppSession.Timeout).Result!);
        Assert.Equal("Keep me", back.Note);
        Assert.Equal(remap.Id, back.Id);
    }

    [Fact]
    public async Task A_key_asked_by_an_AI_app_is_pressed_by_the_user()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "capture_hotkey", new
        {
            title = "The key to remap",
            description = "The E2E test checks the key window.",
            steps = new[] { "Press F22" },
        });
        Window window = RequestWindow(app);
        Assert.True(app.Has(window, "The key to remap"));
        Assert.True(app.Has(window, "The E2E test checks the key window."));
        Assert.True(app.Has(window, "1. Press F22"));
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
    public async Task Points_asked_by_an_AI_app_wait_until_the_user_is_ready()
    {
        using var app = new AppSession(seed => seed.Settings.AllowAiAccess = true);
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "pick_points", new
        {
            confirmed_steps = new[] { "1. Click the first test point", "2. Click the second test point" },
            count = 2,
            labels = new[] { "The first test point", "The second test point" },
            title = "Pick the 2 test points",
            description = "The E2E test checks the picker.",
            steps = new[] { "Press Enter", "Click the 2 test points" },
        });
        Window ready = RequestWindow(app);
        Assert.True(app.Has(ready, "Pick the 2 test points"));
        Assert.True(app.Has(ready, "The E2E test checks the picker."));
        Assert.True(app.Has(ready, "1."));
        Assert.True(app.Has(ready, "Click the 2 test points"));
        Assert.True(app.HasRow(app.FindById(ready, "StepsList"), "2. Click the 2 test points"));
        Assert.Null(Retry.WhileNull(() => app.Automation.GetDesktop().FindFirstChild(cf => cf.ByName("Pick a point").And(cf.ByProcessId(app.ProcessId))),
            TimeSpan.FromSeconds(1)).Result); // no picker before Start
        app.FindButton(ready, "Start").Invoke();

        Assert.NotNull(app.TopWindow("Pick a point"));
        Mouse.Click(new System.Drawing.Point(400, 300)); // on the picker, which covers the screen: nothing else gets it
        Mouse.Click(new System.Drawing.Point(420, 320));
        var (text, isError) = await call;

        Assert.False(isError, text);
        using JsonDocument points = JsonDocument.Parse(text);
        Assert.Equal(400, points.RootElement[0].GetProperty("X").GetInt32());
        Assert.Equal(320, points.RootElement[1].GetProperty("Y").GetInt32());
    }

    [Fact]
    public async Task A_floating_button_asked_by_an_AI_app_is_placed_by_the_user()
    {
        var loop = new LoopDefinition { Name = "E2E button", Actions = { new LoopAction { KeyVk = Vk.F24, IntervalValue = 500 } } };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.Loops.Add(loop);
        });
        await using McpClient client = await Connect();

        Task<(string Text, bool IsError)> call = Call(client, "place_floating_button", new
        {
            id = loop.Id.ToString(),
            title = "Place the E2E button",
            description = "The E2E test checks the placement.",
            steps = new[] { "Drag the button", "Press Enter" },
        });
        Window ready = RequestWindow(app);
        Assert.True(app.Has(ready, "Place the E2E button"));
        app.FindButton(ready, "Start").Invoke();
        AutomationElement? placement = app.TopWindow("Position the overlay");
        Assert.NotNull(placement);
        placement.Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);
        var (text, isError) = await call;
        var (saved, _) = await Call(client, "get_item", new { id = loop.Id.ToString() });

        Assert.False(isError, text);
        Assert.Contains("is on and placed", text);
        using JsonDocument item = JsonDocument.Parse(saved);
        JsonElement button = item.RootElement.GetProperty("FloatingButton");
        Assert.True(button.GetProperty("Enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Number, button.GetProperty("X").ValueKind);
    }

    [Fact]
    public async Task An_AI_app_changes_only_part_of_an_item_with_a_patch()
    {
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(Vk.F23), Target = HotkeyBinding.FromKey(Vk.F22), Note = "E2E remap" };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.Remaps.Add(remap);
        });
        await using McpClient client = await Connect();

        var (text, isError) = await Call(client, "patch_item", new
        {
            id = remap.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Note", value = "E2E patched" } },
        });
        var (saved, _) = await Call(client, "get_item", new { id = remap.Id.ToString() });

        Assert.False(isError, text);
        Assert.StartsWith("Saved the changes to the remap", text);
        using JsonDocument item = JsonDocument.Parse(saved);
        Assert.Equal("E2E patched", item.RootElement.GetProperty("Note").GetString());
        app.GoTo("Remap");
        Assert.True(app.Has(app.MainWindow, "E2E patched"));
    }

    [Fact]
    public async Task A_macro_opened_by_an_AI_app_stays_open_until_the_user_cancels()
    {
        var macro = new MacroDefinition
        {
            Name = "E2E opened",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = Vk.F22, DelayMs = 800 } },
        };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Macros.Add(macro);
        });
        await using McpClient client = await Connect();
        // As with a user: the AI app is in front, PuppyMacro behind (no active window of its own).
        using var target = new TargetWindow();
        target.Activate();
        target.RequireForeground();

        Task<(string Text, bool IsError)> call = Call(client, "open_item", new { id = macro.Id.ToString(), title = "Check the delays", description = "The E2E test opens it." });
        Window editor = app.Dialog("Edit macro");
        Assert.Equal("Check the delays", app.FindById(editor, "NoticeBar").Name);
        // While the editor is open the request waits for it.
        Assert.False(Retry.WhileFalse(() => call.IsCompleted, TimeSpan.FromSeconds(3)).Result, "open_item ended while its editor was open");
        app.FindById(editor, "CancelButton").AsButton().Invoke();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.Contains("without saving", text);
        Assert.Null(Retry.WhileNull(() => app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .FirstOrDefault(w => w.Name == "PuppyMacro" && app.Has(w, "Unexpected error:")), TimeSpan.FromSeconds(2)).Result);
    }

    [Fact]
    public async Task An_item_opened_by_an_AI_app_shows_why_in_its_editor()
    {
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(Vk.F23), Target = HotkeyBinding.FromKey(Vk.F22), Note = "E2E remap" };
        using var app = new AppSession(seed =>
        {
            seed.Settings.AllowAiAccess = true;
            seed.Settings.Remaps.Add(remap);
        });
        await using McpClient client = await Connect();
        string id = remap.Id.ToString();

        Task<(string Text, bool IsError)> call = Call(client, "open_item", new { id, title = "Check the E2E remap", description = "The E2E test opens it." });
        Window editor = app.Dialog("Edit remap");
        Assert.Equal("Check the E2E remap", app.FindById(editor, "NoticeBar").Name); // the InfoBar's texts have no UIA peer
        editor.Close();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.Contains("without saving", text);
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
        Assert.True(app.HasRow(app.FindById(activity, "ActivityList"), ClientName, "Read the guide: Remap"));
        activity.Close();
    }
    private const string SharedFile = """
        { "Format": 1, "App": "1.16.0", "Items": [
          { "Kind": "Loop", "Item": { "Name": "E2E shared loop",
            "Actions": [ { "Type": "Key", "KeyVk": 135, "IntervalValue": 1, "IntervalUnit": "Seconds" } ] } } ] }
        """;

    private static async Task<McpClient> ConnectWithAccess(AppSession app)
    {
        app.GoTo("AI");
        app.FindById(app.MainWindow, "AiAccessSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => SavedSettings().AllowAiAccess, AppSession.Timeout).Success);
        return await Connect();
    }

    [Fact]
    public async Task An_AI_app_imports_a_file_s_items_disabled_and_exports_them()
    {
        using var app = new AppSession();
        await using McpClient client = await ConnectWithAccess(app);

        var (imported, importError) = await Call(client, "import_items", new { content = SharedFile });
        var (exported, exportError) = await Call(client, "export_items");

        Assert.False(importError, imported);
        Assert.StartsWith("Imported 1 item, all disabled", imported);
        app.GoTo("Loops");
        Assert.True(app.Has(app.MainWindow, "E2E shared loop"));
        Assert.False(SavedSettings().Loops.Single(l => l.Name == "E2E shared loop").Enabled);
        Assert.False(exportError, exported);
        Assert.Contains("\"E2E shared loop\"", exported);
        Assert.Contains("\"Sample: click every second\"", exported);
    }

    [Fact]
    public async Task An_AI_app_opens_Import_with_a_file_and_the_user_imports_there()
    {
        using var app = new AppSession();
        await using McpClient client = await ConnectWithAccess(app);

        Task<(string Text, bool IsError)> call = Call(client, "open_import_window", new { content = SharedFile });
        app.FindButton(app.Dialog("Import"), "Import 1 item").Invoke();
        var (text, isError) = await call;

        Assert.False(isError, text);
        Assert.StartsWith("The user imported 1 item, all disabled:", text);
        Assert.True(app.Has(app.MainWindow, "E2E shared loop"));
    }
}
