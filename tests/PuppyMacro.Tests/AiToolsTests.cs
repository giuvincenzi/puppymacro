using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>A user that answers the AI tools' windows as each test says, and remembers what happened.</summary>
internal sealed class FakeAiHost : IAiHost
{
    public FakeAiHost(AppSettings settings, MacroLibrary macros)
    {
        Settings = settings;
        Macros = macros;
    }

    public AppSettings Settings { get; }
    public MacroLibrary Macros { get; }
    public HashSet<Guid> Running { get; } = new();
    public bool AnyRunning => Running.Count > 0;
    public bool OverlayModeActive { get; set; }
    public bool UserIsEditing { get; set; }

    /// <summary>What the user does in an editor: null cancels; otherwise what is saved from the proposal.</summary>
    public Func<object, object?> Edit { get; set; } = proposal => proposal;

    /// <summary>Set: the editor waits until the test completes it.</summary>
    public TaskCompletionSource<bool>? Hold { get; set; }

    public AiAnswer ConfirmAnswer { get; set; } = AiAnswer.Done;
    public List<(string Title, IReadOnlyList<string> Lines)> Confirmed { get; } = new();
    public List<(object Proposal, bool IsNew, string App)> Opened { get; } = new();
    public List<Guid> Deleted { get; } = new();
    public List<(Guid Id, bool Enabled)> Enabled { get; } = new();
    public List<AiSettingsValues> Applied { get; } = new();
    public bool StoppedAll { get; private set; }

    public bool IsRunning(Guid id) => Running.Contains(id);

    public Task<T> Ui<T>(Func<Task<T>> action) => action();

    private async Task<AiEdited<T>> Open<T>(T proposal, bool isNew, string app) where T : class
    {
        Opened.Add((proposal, isNew, app));
        if (Hold != null)
            await Hold.Task;
        return Edit(proposal) is T saved ? new AiEdited<T>(AiAnswer.Done, saved) : new AiEdited<T>(AiAnswer.Cancelled, null);
    }

    public Task<AiEdited<LoopDefinition>> EditLoop(LoopDefinition proposal, bool isNew, string app) => Open(proposal, isNew, app);
    public Task<AiEdited<MacroDefinition>> EditMacro(MacroDefinition proposal, bool isNew, string app) => Open(proposal, isNew, app);
    public Task<AiEdited<RemapDefinition>> EditRemap(RemapDefinition proposal, bool isNew, string app) => Open(proposal, isNew, app);

    public Task<AiAnswer> Confirm(string app, string title, IReadOnlyList<string> lines, string primary)
    {
        Confirmed.Add((title, lines));
        return Task.FromResult(ConfirmAnswer);
    }

    public void Delete(Guid id) => Deleted.Add(id);
    public void SetEnabled(Guid id, bool enabled) => Enabled.Add((id, enabled));
    public void ApplySettings(AiSettingsValues values) => Applied.Add(values);
    public Task<(AiAnswer Answer, int X, int Y)> PickPoint(string app, string reason) => Task.FromResult((AiAnswer.Done, 640, 360));
    public Task<(AiAnswer Answer, string? Exe)> PickApp(string app, string reason) => Task.FromResult<(AiAnswer, string?)>((AiAnswer.Done, "notepad.exe"));

    public Task<(AiAnswer Answer, HotkeyBinding? Keys)> CaptureKeys(string app, string reason) =>
        Task.FromResult<(AiAnswer, HotkeyBinding?)>((AiAnswer.Done, new HotkeyBinding { Vk = 0x58, Ctrl = true }));

    public void StopAll() => StoppedAll = true;
    public IReadOnlyList<string> RunningApps() => new[] { "notepad.exe" };
}

public class AiToolsTests : IDisposable
{
    private const string App = "Claude Desktop";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiTools _tools;

    public AiToolsTests()
    {
        AiTools.WaitStep = TimeSpan.FromMilliseconds(300);
        _settings.AllowAiAccess = true;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null) =>
        _tools.Call(tool, JsonSerializer.SerializeToElement(args ?? new { }), App);

    private static readonly HotkeyBinding CtrlX = new() { Vk = 0x58, Ctrl = true };

    [Fact]
    public async Task Every_tool_is_refused_while_AI_access_is_off()
    {
        _settings.AllowAiAccess = false;

        foreach (AiTool tool in _tools.All)
        {
            AiToolResult result = await Call(tool.Name);
            Assert.True(result.IsError, tool.Name);
            Assert.Contains("Allow AI access", result.Text);
        }
        Assert.Empty(_host.Opened);
        Assert.All(_log.Entries(), e => Assert.Equal(AiActivityResult.Refused, e.Result));
    }

    [Fact]
    public async Task Overview_lists_the_items_with_their_ids_and_keys()
    {
        var loop = new LoopDefinition { Name = "Spam", Hotkey = HotkeyBinding.FromKey(0x70) };
        _settings.Loops.Add(loop);
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);
        _host.Running.Add(loop.Id);

        AiToolResult result = await Call("get_overview");

        Assert.False(result.IsError);
        Assert.Contains(loop.Id.ToString(), result.Text);
        Assert.Contains("\"Spam\"", result.Text);
        Assert.Contains("\"Running\": true", result.Text);
        Assert.Contains(remap.Id.ToString(), result.Text);
        Assert.Contains(KeyNames.Format(CtrlX), result.Text);
        Assert.Equal(AiActivityKind.Read, Assert.Single(_log.Entries()).Kind);
    }

    [Fact]
    public async Task Get_item_gives_the_saved_json_and_an_error_for_an_unknown_id()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41), Note = "Cut is A" };
        _settings.Remaps.Add(remap);

        AiToolResult found = await Call("get_item", new { id = remap.Id.ToString() });
        AiToolResult missing = await Call("get_item", new { id = Guid.NewGuid().ToString() });

        Assert.Equal(RemapJson.Serialize(remap), found.Text);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task The_schema_has_no_id_since_PuppyMacro_gives_it()
    {
        AiToolResult result = await Call("get_schema", new { kind = "loop" });

        using JsonDocument schema = JsonDocument.Parse(result.Text);
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("Id", out _));
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("Actions", out _));
    }

    [Fact]
    public async Task A_new_remap_opens_the_editor_and_says_it_was_saved()
    {
        AiToolResult result = await Call("propose_remap", new
        {
            item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 }, AppExe = "notepad.exe" },
        });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Saved as proposed", result.Text);
        var (proposal, isNew, app) = Assert.Single(_host.Opened);
        var remap = Assert.IsType<RemapDefinition>(proposal);
        Assert.True(isNew);
        Assert.Equal(App, app);
        Assert.True(remap.Source!.SameAs(CtrlX));
        Assert.Equal("notepad.exe", remap.AppExe);
        Assert.NotEqual(Guid.Empty, remap.Id);
        AiActivity entry = _log.Entries().First();
        Assert.Equal(AiActivityResult.Saved, entry.Result);
        Assert.Contains("Ctrl + X → A in notepad.exe", entry.Request);
    }

    [Fact]
    public async Task A_proposal_the_editor_would_refuse_opens_nothing()
    {
        AiToolResult result = await Call("propose_remap", new
        {
            item = new { Source = new { Vk = 0x41 }, Target = new { Vk = 0x41 } },
        });

        Assert.True(result.IsError);
        Assert.Contains("Send something different from what you press.", result.Text);
        Assert.Empty(_host.Opened);
        Assert.Equal(AiActivityResult.Error, _log.Entries().First().Result);
    }

    [Fact]
    public async Task A_remap_of_a_key_already_remapped_is_refused_as_in_the_editor()
    {
        _settings.Remaps.Add(new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x42) });

        AiToolResult result = await Call("propose_remap", new
        {
            item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 } },
        });

        Assert.True(result.IsError);
        Assert.Contains("is already remapped for all apps", result.Text);
    }

    [Fact]
    public async Task Changes_the_user_made_before_saving_are_in_the_answer()
    {
        _host.Edit = proposal =>
        {
            var saved = ((RemapDefinition)proposal).Clone();
            saved.Note = "Changed by the user";
            return saved;
        };

        AiToolResult result = await Call("propose_remap", new
        {
            item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 } },
        });

        Assert.StartsWith("Saved, with changes", result.Text);
        Assert.Contains("Changed by the user", result.Text);
        Assert.Equal(AiActivityResult.SavedWithChanges, _log.Entries().First().Result);
    }

    [Fact]
    public async Task A_cancelled_proposal_is_not_saved()
    {
        _host.Edit = _ => null;

        AiToolResult result = await Call("propose_loop", new
        {
            item = new { Name = "Spam F24", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 100 } } },
        });

        Assert.Contains("cancelled", result.Text);
        Assert.Equal(AiActivityResult.Cancelled, _log.Entries().First().Result);
    }

    [Fact]
    public async Task A_change_keeps_the_id_of_the_item_changed()
    {
        var loop = new LoopDefinition
        {
            Name = "Old", Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = 100 } },
        };
        _settings.Loops.Add(loop);

        await Call("propose_loop", new
        {
            id = loop.Id.ToString(),
            item = new { Name = "New", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 200 } } },
        });

        var (proposal, isNew, _) = Assert.Single(_host.Opened);
        Assert.False(isNew);
        Assert.Equal(loop.Id, ((LoopDefinition)proposal).Id);
        Assert.Equal("New", ((LoopDefinition)proposal).Name);
    }

    [Fact]
    public async Task Loops_and_macros_cannot_be_changed_while_something_runs()
    {
        _host.Running.Add(Guid.NewGuid());

        AiToolResult result = await Call("propose_loop", new
        {
            item = new { Name = "Spam", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 100 } } },
        });

        Assert.True(result.IsError);
        Assert.Contains("stop_all", result.Text);
        Assert.Empty(_host.Opened);
    }

    [Fact]
    public async Task Nothing_opens_while_the_user_has_a_PuppyMacro_window_open()
    {
        _host.UserIsEditing = true;

        AiToolResult result = await Call("pick_point", new { reason = "Click the button" });

        Assert.True(result.IsError);
        Assert.Contains("PuppyMacro window open", result.Text);
    }

    private static string RequestId(AiToolResult waiting) =>
        System.Text.RegularExpressions.Regex.Match(waiting.Text, "request_id \"([0-9a-f]+)\"").Groups[1].Value;

    private static readonly object CtrlXToA = new { item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 } } };

    [Fact]
    public async Task A_user_who_takes_long_gets_a_request_id_and_wait_for_user_gives_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();

        AiToolResult first = await Call("propose_remap", CtrlXToA);
        string id = RequestId(first);
        AiToolResult other = await Call("pick_point", new { reason = "Click" });
        AiToolResult stillWaiting = await Call("wait_for_user", new { request_id = id });
        _host.Hold.SetResult(true);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.StartsWith("Waiting", first.Text);
        Assert.Equal(8, id.Length);
        Assert.True(other.IsError);
        Assert.Contains($"request_id \"{id}\"", other.Text);
        Assert.StartsWith("Waiting", stillWaiting.Text);
        Assert.StartsWith("Saved as proposed", answer.Text);
        Assert.True((await Call("wait_for_user", new { request_id = id })).IsError); // given once
    }

    [Fact]
    public async Task An_answer_that_came_after_waiting_is_kept_for_wait_for_user()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("propose_remap", CtrlXToA));

        _host.Hold.SetResult(true);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.StartsWith("Saved as proposed", answer.Text);
    }

    [Fact]
    public async Task An_answer_not_taken_yet_is_kept_when_another_request_starts()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("propose_remap", CtrlXToA));
        _host.Hold.SetResult(true);
        _host.Hold = null;

        AiToolResult point = await Call("pick_point", new { reason = "Click" });
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.False(point.IsError, point.Text);
        Assert.StartsWith("Saved as proposed", answer.Text);
    }

    [Fact]
    public async Task Only_the_app_that_asked_gets_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("propose_remap", CtrlXToA));
        _host.Hold.SetResult(true);

        AiToolResult other = await _tools.Call("wait_for_user", JsonSerializer.SerializeToElement(new { request_id = id }), "Codex",
            TestContext.Current.CancellationToken);
        AiToolResult mine = await Call("wait_for_user", new { request_id = id });

        Assert.True(other.IsError);
        Assert.StartsWith("Saved as proposed", mine.Text);
    }

    [Fact]
    public async Task Two_global_hotkeys_cannot_become_the_same_key()
    {
        AiToolResult result = await Call("update_settings", new
        {
            settings = new { StopAllHotkey = new { Vk = 0x7B }, OverlayModeHotkey = new { Vk = 0x7B } },
        });

        Assert.True(result.IsError);
        Assert.Contains("would also be the Stop all hotkey", result.Text);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task Delete_asks_the_user_and_deletes_only_when_confirmed()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);

        _host.ConfirmAnswer = AiAnswer.Cancelled;
        AiToolResult cancelled = await Call("delete_item", new { id = remap.Id.ToString() });
        _host.ConfirmAnswer = AiAnswer.Done;
        AiToolResult deleted = await Call("delete_item", new { id = remap.Id.ToString() });

        Assert.Contains("cancelled", cancelled.Text);
        Assert.StartsWith("Deleted", deleted.Text);
        Assert.Equal(new[] { remap.Id }, _host.Deleted);
        Assert.Equal("Delete this remap?", _host.Confirmed[0].Title);
    }

    [Fact]
    public async Task Set_enabled_asks_the_user()
    {
        var loop = new LoopDefinition { Name = "Spam" };
        _settings.Loops.Add(loop);

        AiToolResult result = await Call("set_enabled", new { id = loop.Id.ToString(), enabled = false });

        Assert.StartsWith("Disabled loop \"Spam\"", result.Text);
        Assert.Equal((loop.Id, false), Assert.Single(_host.Enabled));
    }

    [Fact]
    public async Task Settings_changes_are_listed_in_the_confirmation_and_applied()
    {
        AiToolResult result = await Call("update_settings", new { settings = new { Theme = "Light", StopAllHotkey = new { Vk = 0x7B } } });

        Assert.StartsWith("Settings changed", result.Text);
        IReadOnlyList<string> lines = Assert.Single(_host.Confirmed).Lines;
        Assert.Contains("Theme: Dark → Light", lines);
        Assert.Contains($"Stop all hotkey: {KeyNames.Format(_settings.StopAllHotkey)} → F12", lines);
        AiSettingsValues applied = Assert.Single(_host.Applied);
        Assert.Equal(AppTheme.Light, applied.Theme);
    }

    [Fact]
    public async Task A_global_hotkey_that_clashes_is_refused_before_asking()
    {
        AiToolResult result = await Call("update_settings", new
        {
            settings = new { StopAllHotkey = new { Vk = _settings.OverlayModeHotkey!.Vk, Alt = true, Shift = true } },
        });

        Assert.True(result.IsError);
        Assert.Contains("Stop all hotkey", result.Text);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task Unknown_settings_are_refused()
    {
        AiToolResult result = await Call("update_settings", new { settings = new { AllowAiAccess = false } });

        Assert.True(result.IsError);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task Check_hotkey_follows_the_hotkey_rules_and_conflicts()
    {
        _settings.Loops.Add(new LoopDefinition { Name = "Spam", Hotkey = HotkeyBinding.FromKey(0x70) });

        AiToolResult esc = await Call("check_hotkey", new { hotkey = new { Vk = 0x1B } });
        AiToolResult taken = await Call("check_hotkey", new { hotkey = new { Vk = 0x70 } });
        AiToolResult free = await Call("check_hotkey", new { hotkey = new { Vk = 0x7C } });

        Assert.Contains("can't be used", esc.Text);
        Assert.Contains("can't be used", taken.Text);
        Assert.Contains("is free", free.Text);
    }

    [Fact]
    public async Task Interactions_give_what_the_user_picked_or_pressed()
    {
        AiToolResult point = await Call("pick_point", new { reason = "Click the Send button" });
        AiToolResult app = await Call("pick_app", new { reason = "Click the game" });
        AiToolResult keys = await Call("capture_hotkey", new { reason = "The key to remap" });

        Assert.Equal("{\"X\":640,\"Y\":360}", point.Text);
        Assert.Contains("notepad.exe", app.Text);
        Assert.StartsWith(KeyNames.Format(CtrlX), keys.Text);
        Assert.True((await Call("pick_point", new { reason = "" })).IsError);
    }

    [Fact]
    public async Task Stop_all_stops_everything_but_nothing_can_be_started()
    {
        AiToolResult result = await Call("stop_all");

        Assert.True(_host.StoppedAll);
        Assert.False(result.IsError);
        Assert.DoesNotContain(_tools.All, t => t.Name.StartsWith("start", StringComparison.Ordinal) || t.Name.StartsWith("test", StringComparison.Ordinal));
        Assert.Contains("Not available: starting, stopping or testing loops and macros", AiTools.Instructions);
    }

    [Fact]
    public async Task Help_lists_the_guide_and_gives_a_section_as_text()
    {
        AiToolResult index = await Call("get_help");
        AiToolResult remap = await Call("get_help", new { topic = "remap" });
        AiToolResult unknown = await Call("get_help", new { topic = "nothing" });

        Assert.Contains("- remap: Remap", index.Text);
        Assert.StartsWith("# Remap", remap.Text);
        Assert.Contains("Add remap", remap.Text);
        Assert.DoesNotContain("<", remap.Text);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public void Every_tool_schema_is_a_valid_object_schema()
    {
        foreach (AiTool tool in _tools.All)
        {
            using JsonDocument schema = JsonDocument.Parse(tool.InputSchema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(_tools.All.Count, _tools.All.Select(t => t.Name).Distinct().Count());
    }
}
