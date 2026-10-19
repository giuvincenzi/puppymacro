using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The AI tools when something changes while the user's window is open (a confirmation, the ready window): the user deletes
/// the item, or opens a PuppyMacro editor before pressing Start. And the texts an AI app gets naming the tools.
/// </summary>
// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
public class AiToolsMeanwhileTests : IDisposable
{
    private const string App = "Claude Desktop";
    private const string Editor = "the macro editor of \"Salvage\"";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiTools _tools;

    public AiToolsMeanwhileTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object args) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args), App, TestContext.Current.CancellationToken);

    private static readonly string[] Steps = { "Open the game", "Press Start" };

    private MacroDefinition SavedMacro()
    {
        var macro = new MacroDefinition { Name = "Bomb", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        _host.Macros.Macros.Add(macro);
        return macro;
    }

    /// <summary>
    /// set_enabled with Ask before changing on: while the confirmation is open the user deletes the loop from its card, then
    /// presses the confirmation's button. Nothing was enabled or disabled, so the AI app must be told (as save_loop and
    /// delete_item say "deleted meanwhile" / "already gone"), and the activity must not record the change as applied.
    /// </summary>
    [Fact]
    public async Task Set_enabled_confirmed_after_the_item_was_deleted_meanwhile_says_so_and_records_nothing_applied()
    {
        var loop = new LoopDefinition { Name = "Spam" };
        _settings.Loops.Add(loop);
        _settings.AiAskBeforeChanging = true;
        _host.Hold = new TaskCompletionSource<bool>();

        Task<AiToolResult> call = Call("set_enabled", new { id = loop.Id.ToString(), enabled = false });
        Assert.Single(_host.Confirmed);
        _settings.Loops.Clear(); // the user deletes it while the confirmation is open
        _host.Hold.SetResult(true);
        AiToolResult result = await call;

        Assert.True(result.IsError, $"Answered as done: \"{result.Text}\"");
        Assert.Contains("deleted meanwhile", result.Text);
        Assert.NotEqual(AiActivityResult.Applied, _log.Entries().First().Result);
    }

    /// <summary>
    /// The pick tools check again, once the user pressed Start in the ready window, that no PuppyMacro editor or dialog was
    /// opened meanwhile (BusyNow): the user may have gone back to PuppyMacro while the ready window waited.
    /// </summary>
    [Fact]
    public async Task A_pick_whose_ready_window_waited_while_the_user_opened_an_editor_picks_nothing()
    {
        _host.Hold = new TaskCompletionSource<bool>();

        Task<AiToolResult> call = Call("pick_points", new
        {
            confirmed_steps = new[] { "1. Click the button" }, count = 1, labels = new[] { "The button" },
            title = "Pick the button", description = "We are making a macro.", steps = Steps,
        });
        Assert.Single(_host.ReadyShown);
        _host.UserHasOpen = Editor;
        _host.Hold.SetResult(true);
        AiToolResult result = await call;

        Assert.True(result.IsError);
        Assert.Contains(Editor, result.Text);
        Assert.Empty(_host.PointsAsked);
    }

    /// <summary>
    /// The same for the screen tools: the user opens a macro editor while the ready window waits, then presses Start. A
    /// PuppyMacro editor suspends the hotkeys (Stop all among them), and PuppyMacro "changes nothing and opens no window for
    /// an AI app" while one is open, yet test_macro plays the macro (up to 600 s, with the Stop all hotkey not working),
    /// record_macro records, look_at_screen and check_condition look: they never check again after the ready window, as the
    /// pick tools do.
    /// </summary>
    [Theory]
    [InlineData("test_macro")]
    [InlineData("record_macro")]
    [InlineData("look_at_screen")]
    [InlineData("check_condition")]
    public async Task A_screen_request_whose_ready_window_waited_while_the_user_opened_an_editor_does_nothing(string tool)
    {
        MacroDefinition macro = SavedMacro();
        object args = tool switch
        {
            "test_macro" => new
            {
                id = macro.Id.ToString(), confirmed_steps = new[] { "1. Press A: drops the bomb" },
                title = "Test the bomb", description = "We test the macro.", steps = Steps,
            },
            "check_condition" => (object)new
            {
                condition = new { Kind = "Pixel", X = 5, Y = 5, Color = "#FFFFFF" },
                title = "Check the bomb", description = "We check the condition.", steps = Steps,
            },
            _ => new { title = "Show the game", description = "We make a macro.", steps = Steps },
        };
        _host.Hold = new TaskCompletionSource<bool>();

        Task<AiToolResult> call = Call(tool, args);
        Assert.Single(_host.ReadyShown);
        _host.UserHasOpen = Editor;
        _host.Hold.SetResult(true);
        AiToolResult result = await call;

        Assert.True(result.IsError, $"{tool} went on with an editor open: \"{result.Text}\"");
        Assert.Contains(Editor, result.Text);
        Assert.Empty(_host.Tested);
        Assert.Empty(_host.Checked);
    }

    /// <summary>
    /// get_last_run for a macro with no run: the answer names test_macro, and PuppyMacro's own texts name tools by their full
    /// names (puppymacro_…: an AI app with many servers tells them apart), as every other answer that names a tool does.
    /// </summary>
    [Fact]
    public async Task Get_last_run_without_a_run_names_test_macro_by_its_full_name()
    {
        MacroDefinition macro = SavedMacro();

        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });

        Assert.False(last.IsError);
        Assert.Contains("puppymacro_test_macro", last.Text);
    }

    /// <summary>
    /// An AI app that gives its name and an empty title (MCP's title is an optional display name): it is named by its name,
    /// as when it gives no title, not "Unknown app"; and Claude Desktop's chat still lights up its card on the AI page.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_AI_app_with_an_empty_title_is_named_by_its_name(string title)
    {
        var client = new Implementation { Name = "claude-ai", Title = title, Version = "1" };

        Assert.Equal("Claude (claude-ai)", AiServer.FriendlyName(client));
        Assert.Equal(AiAppKind.ClaudeDesktop, AiServer.KindOf(client));
    }

    /// <summary>A text that is only a tool's full name is not prefixed twice.</summary>
    [Fact]
    public void A_full_tool_name_is_never_prefixed_again()
    {
        Assert.Equal("call puppymacro_wait_for_user", AiTools.Prefixed("call puppymacro_wait_for_user"));
        Assert.Equal("call puppymacro_wait_for_user", AiTools.Prefixed("call wait_for_user"));
    }

    /// <summary>
    /// A request waiting for the user, then the AI app's call is cancelled (the AI app gave up on it): the answer is kept and
    /// wait_for_user gives it once the user answers.
    /// </summary>
    [Fact]
    public async Task A_cancelled_call_keeps_its_answer_for_wait_for_user()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        using var cancel = new System.Threading.CancellationTokenSource();

        Task<AiToolResult> call = _tools.Call(AiTools.Name("pick_app"),
            JsonSerializer.SerializeToElement(new { title = "Pick", description = "Why", steps = Steps }), App, cancel.Token);
        cancel.Cancel();
        AiToolResult waiting = await call;
        string id = System.Text.RegularExpressions.Regex.Match(waiting.Text, "request_id \"([0-9a-f]+)\"").Groups[1].Value;
        _host.Hold.SetResult(true);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.StartsWith("Waiting:", waiting.Text);
        Assert.False(answer.IsError, answer.Text);
        Assert.Contains("notepad.exe", answer.Text);
    }
}
