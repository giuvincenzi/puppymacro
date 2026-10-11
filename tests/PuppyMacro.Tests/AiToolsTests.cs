using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The app as the AI tools see it: its data, changed as the windows would, and a user who answers as each test says.</summary>
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

    /// <summary>Set: the user's windows (confirmation, ready) wait until the test completes it.</summary>
    public TaskCompletionSource<bool>? Hold { get; set; }

    public AiAnswer ConfirmAnswer { get; set; } = AiAnswer.Done;
    public AiAnswer ReadyAnswer { get; set; } = AiAnswer.Done;
    public List<(string Title, IReadOnlyList<string> Lines)> Confirmed { get; } = new();
    public List<(object Item, string App)> Saved { get; } = new();
    public List<AiAsk> ReadyShown { get; } = new();
    public AiAnswer PlaceAnswer { get; set; } = AiAnswer.Done;
    public List<Guid> Placed { get; } = new();
    public AiAnswer OpenAnswer { get; set; } = AiAnswer.Done;
    public List<(Guid Id, string Title, string Description)> Opened { get; } = new();
    public List<IReadOnlyList<string>> PointsAsked { get; } = new();
    public List<AiSettingsValues> Applied { get; } = new();
    public bool StoppedAll { get; private set; }

    public bool IsRunning(Guid id) => Running.Contains(id);

    public Task<T> Ui<T>(Func<Task<T>> action) => action();

    public void Save(object item, string app)
    {
        Saved.Add((item, app));
        switch (item)
        {
            case LoopDefinition loop:
                Settings.Loops.RemoveAll(l => l.Id == loop.Id);
                Settings.Loops.Add(loop);
                Running.Remove(loop.Id);
                break;
            case MacroDefinition macro:
                Macros.Macros.RemoveAll(m => m.Id == macro.Id);
                Macros.Macros.Add(macro);
                Running.Remove(macro.Id);
                break;
            case RemapDefinition remap:
                Settings.Remaps.RemoveAll(r => r.Id == remap.Id);
                Settings.Remaps.Add(remap);
                break;
        }
    }

    public void Delete(Guid id, string app)
    {
        Settings.Loops.RemoveAll(l => l.Id == id);
        Macros.Macros.RemoveAll(m => m.Id == id);
        Settings.Remaps.RemoveAll(r => r.Id == id);
    }

    public void SetEnabled(Guid id, bool enabled, string app)
    {
        foreach (LoopDefinition loop in Settings.Loops.Where(l => l.Id == id))
            loop.Enabled = enabled;
        foreach (RemapDefinition remap in Settings.Remaps.Where(r => r.Id == id))
            remap.Enabled = enabled;
    }

    public void ApplySettings(AiSettingsValues values) => Applied.Add(values);

    public async Task<AiAnswer> Confirm(string app, string title, IReadOnlyList<string> lines, string primary)
    {
        Confirmed.Add((title, lines));
        if (Hold != null)
            await Hold.Task;
        return ConfirmAnswer;
    }

    public async Task<AiAnswer> Ready(string app, AiAsk ask)
    {
        ReadyShown.Add(ask);
        if (Hold != null)
            await Hold.Task;
        return ReadyAnswer;
    }

    public Task<(AiAnswer Answer, List<(int X, int Y)> Points)> PickPoints(string app, string message, IReadOnlyList<string> labels)
    {
        PointsAsked.Add(labels);
        return Task.FromResult((AiAnswer.Done, Enumerable.Range(0, labels.Count).Select(i => (640 + i, 360)).ToList()));
    }

    public Task<(AiAnswer Answer, string? Exe)> PickApp(string app, string message) =>
        Task.FromResult<(AiAnswer, string?)>((AiAnswer.Done, "notepad.exe"));

    public Task<(AiAnswer Answer, HotkeyBinding? Keys)> CaptureKeys(string app, AiAsk ask) =>
        Task.FromResult<(AiAnswer, HotkeyBinding?)>((AiAnswer.Done, new HotkeyBinding { Vk = 0x58, Ctrl = true }));

    /// <summary>As the window: the button and Show in overlay on, placed at 100, 200.</summary>
    public Task<AiAnswer> PlaceFloatingButton(Guid id, string app)
    {
        Placed.Add(id);
        if (PlaceAnswer == AiAnswer.Done)
        {
            LoopDefinition loop = Settings.Loops.First(l => l.Id == id).Clone();
            loop.ShowInOverlay = true;
            loop.FloatingButton.Enabled = true;
            loop.FloatingButton.X = 100;
            loop.FloatingButton.Y = 200;
            Save(loop, app);
        }
        return Task.FromResult(PlaceAnswer);
    }

    public Task<AiAnswer> OpenItem(Guid id, string app, string title, string description)
    {
        Opened.Add((id, title, description));
        return Task.FromResult(OpenAnswer);
    }

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
        _tools.Call(tool, JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static readonly HotkeyBinding CtrlX = new() { Vk = 0x58, Ctrl = true };

    private static readonly object CtrlXToA = new { item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 } } };

    private static readonly object Asking = new
    {
        title = "Pick the Send button",
        label = "The Send button",
        description = "We are making a macro that sends a message.",
        steps = new[] { "Open the chat", "Press Enter", "Click the Send button" },
    };

    private static string RequestId(AiToolResult waiting) =>
        Regex.Match(waiting.Text, "request_id \"([0-9a-f]+)\"").Groups[1].Value;

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
        Assert.Empty(_host.Saved);
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
    public async Task A_new_remap_is_saved_at_once()
    {
        AiToolResult result = await Call("save_remap", new
        {
            item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 }, AppExe = "notepad.exe" },
        });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Saved the new remap", result.Text);
        Assert.Empty(_host.Confirmed);
        var (item, app) = Assert.Single(_host.Saved);
        var remap = Assert.IsType<RemapDefinition>(item);
        Assert.Equal(App, app);
        Assert.True(remap.Source!.SameAs(CtrlX));
        Assert.Equal("notepad.exe", remap.AppExe);
        AiActivity entry = _log.Entries().First();
        Assert.Equal(AiActivityResult.Saved, entry.Result);
        Assert.Contains("Ctrl + X → A in notepad.exe", entry.Request);
        Assert.Equal(remap.Id, entry.ItemId);
        Assert.Null(entry.Before); // nothing to restore for something new
    }

    [Fact]
    public async Task A_change_keeps_its_id_says_what_changed_and_keeps_the_version_before()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41), Note = "Old" };
        _settings.Remaps.Add(remap);
        string before = RemapJson.Serialize(remap);

        AiToolResult result = await Call("save_remap", new
        {
            id = remap.Id.ToString(),
            item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 }, Note = "New" },
        });

        Assert.StartsWith("Saved the changes to the remap", result.Text);
        Assert.Equal(remap.Id, Assert.IsType<RemapDefinition>(Assert.Single(_host.Saved).Item).Id);
        AiActivity entry = _log.Entries().First();
        Assert.Contains("Note: \"Old\" → \"New\"", entry.Request);
        Assert.Equal(before, entry.Before);
        Assert.Equal("remap", entry.ItemKind);
    }

    [Fact]
    public async Task A_proposal_the_editor_would_refuse_saves_nothing()
    {
        AiToolResult result = await Call("save_remap", new { item = new { Source = new { Vk = 0x41 }, Target = new { Vk = 0x41 } } });

        Assert.True(result.IsError);
        Assert.Contains("Send something different from what you press.", result.Text);
        Assert.Empty(_host.Saved);
        Assert.Equal(AiActivityResult.Error, _log.Entries().First().Result);
    }

    [Fact]
    public async Task A_remap_of_a_key_already_remapped_is_refused_as_in_the_editor()
    {
        _settings.Remaps.Add(new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x42) });

        AiToolResult result = await Call("save_remap", CtrlXToA);

        Assert.True(result.IsError);
        Assert.Contains("is already remapped for all apps", result.Text);
    }

    [Fact]
    public async Task With_ask_before_changing_the_user_sees_the_changes_first()
    {
        _settings.AiAskBeforeChanging = true;
        var loop = new LoopDefinition { Name = "Spam", Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = 100 } } };
        _settings.Loops.Add(loop);

        _host.ConfirmAnswer = AiAnswer.Cancelled;
        AiToolResult cancelled = await Call("save_loop", new
        {
            id = loop.Id.ToString(),
            item = new { Name = "Spam", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 250 } } },
        });

        Assert.Contains("cancelled", cancelled.Text);
        Assert.Empty(_host.Saved);
        var (title, lines) = Assert.Single(_host.Confirmed);
        Assert.Equal("Save these changes to the loop?", title);
        Assert.Contains("Action 1 (Key): Interval value 100 → 250", lines);
    }

    [Fact]
    public async Task A_running_item_that_changes_stops()
    {
        var loop = new LoopDefinition { Name = "Spam", Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = 100 } } };
        _settings.Loops.Add(loop);
        _host.Running.Add(loop.Id);

        AiToolResult result = await Call("save_loop", new
        {
            id = loop.Id.ToString(),
            item = new { Name = "Spam 2", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 100 } } },
        });

        Assert.Contains("It was running: it stopped.", result.Text);
    }

    [Fact]
    public async Task Deleting_asks_by_default_and_not_when_turned_off()
    {
        var first = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        var second = new RemapDefinition { Source = HotkeyBinding.FromKey(0x7C), Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.AddRange(new[] { first, second });

        AiToolResult asked = await Call("delete_item", new { id = first.Id.ToString() });
        _settings.AiAskBeforeDeleting = false;
        AiToolResult direct = await Call("delete_item", new { id = second.Id.ToString() });

        Assert.StartsWith("Deleted", asked.Text);
        Assert.StartsWith("Deleted", direct.Text);
        Assert.Equal("Delete this remap?", Assert.Single(_host.Confirmed).Title);
        Assert.Empty(_settings.Remaps);
        Assert.All(_log.Entries().Take(2), e => Assert.NotNull(e.Before)); // both can be restored
    }

    [Fact]
    public async Task A_cancelled_deletion_keeps_the_item()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);
        _host.ConfirmAnswer = AiAnswer.Cancelled;

        AiToolResult result = await Call("delete_item", new { id = remap.Id.ToString() });

        Assert.Contains("cancelled", result.Text);
        Assert.Single(_settings.Remaps);
    }

    [Fact]
    public async Task Set_enabled_is_direct_by_default()
    {
        var loop = new LoopDefinition { Name = "Spam" };
        _settings.Loops.Add(loop);

        AiToolResult result = await Call("set_enabled", new { id = loop.Id.ToString(), enabled = false });
        AiToolResult again = await Call("set_enabled", new { id = loop.Id.ToString(), enabled = false });

        Assert.StartsWith("Disabled loop \"Spam\"", result.Text);
        Assert.Contains("already disabled", again.Text);
        Assert.False(loop.Enabled);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task Settings_ask_by_default_with_every_change_listed()
    {
        AiToolResult result = await Call("update_settings", new { settings = new { Theme = "Light", StopAllHotkey = new { Vk = 0x7B } } });

        Assert.StartsWith("Settings changed", result.Text);
        IReadOnlyList<string> lines = Assert.Single(_host.Confirmed).Lines;
        Assert.Contains("Theme: Dark → Light", lines);
        Assert.Contains($"Stop all hotkey: {KeyNames.Format(_settings.StopAllHotkey)} → F12", lines);
        Assert.Equal(AppTheme.Light, Assert.Single(_host.Applied).Theme);
        AiActivity entry = _log.Entries().First();
        Assert.Equal("settings", entry.ItemKind);
        // Restore puts back only these two: the user's later changes of other settings stay.
        AiSettingsValues before = JsonSerializer.Deserialize<AiSettingsValues>(entry.Before!, CodeJson.Options)!;
        Assert.Equal(AppTheme.Dark, before.Theme);
        Assert.NotNull(before.StopAllHotkey);
        Assert.Null(before.SoundVolume);
        Assert.Null(before.OverlayModeHotkey);
    }

    [Fact]
    public async Task A_change_of_an_item_deleted_meanwhile_is_not_saved()
    {
        var loop = new LoopDefinition { Name = "Spam", Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = 100 } } };
        _settings.Loops.Add(loop);
        _settings.AiAskBeforeChanging = true;
        _host.Hold = new TaskCompletionSource<bool>();

        Task<AiToolResult> call = Call("save_loop", new
        {
            id = loop.Id.ToString(),
            item = new { Name = "Spam 2", Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 100 } } },
        });
        _settings.Loops.Clear(); // the user deletes it while the confirmation is open
        _host.Hold.SetResult(true);
        AiToolResult result = await call;

        Assert.True(result.IsError);
        Assert.Contains("deleted meanwhile", result.Text);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task Settings_are_direct_when_asking_is_off()
    {
        _settings.AiAskBeforeSettings = false;

        await Call("update_settings", new { settings = new { SoundVolume = 30 } });

        Assert.Empty(_host.Confirmed);
        Assert.Equal(30, Assert.Single(_host.Applied).SoundVolume);
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
    public async Task Unknown_settings_are_refused()
    {
        AiToolResult result = await Call("update_settings", new { settings = new { AllowAiAccess = false } });

        Assert.True(result.IsError);
        Assert.Empty(_host.Applied);
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
    public async Task A_pick_waits_until_the_user_is_ready_then_gives_the_points()
    {
        AiToolResult one = await Call("pick_point", Asking);
        AiToolResult three = await Call("pick_points", new
        {
            count = 3,
            labels = new[] { "First item", "Second item", "Third item" },
            title = "Pick the 3 items",
            description = "We are making a macro.",
            steps = new[] { "Click the 3 items" },
        });

        Assert.Equal("{\"X\":640,\"Y\":360}", one.Text);
        using JsonDocument points = JsonDocument.Parse(three.Text);
        Assert.Equal(3, points.RootElement.GetArrayLength());
        Assert.Equal(new[] { "The Send button" }, _host.PointsAsked[0]);
        Assert.Equal(new[] { "First item", "Second item", "Third item" }, _host.PointsAsked[1]);
        Assert.Equal("Pick the Send button", _host.ReadyShown[0].Title);
        Assert.Equal("We are making a macro that sends a message.", _host.ReadyShown[0].Description);
        Assert.Equal(new[] { "Open the chat", "Press Enter", "Click the Send button" }, _host.ReadyShown[0].Steps);
    }

    [Fact]
    public async Task Cancelled_when_not_ready_nothing_is_picked()
    {
        _host.ReadyAnswer = AiAnswer.Cancelled;

        AiToolResult result = await Call("pick_point", Asking);

        Assert.Contains("cancelled", result.Text);
        Assert.Empty(_host.PointsAsked);
    }

    [Fact]
    public async Task Interactions_need_a_title_a_description_and_steps()
    {
        string[] steps = { "Click it" };
        Assert.True((await Call("pick_point", new { label = "It", title = "Pick", description = "", steps })).IsError);
        Assert.True((await Call("pick_point", new { title = "Pick", description = "Why", steps })).IsError); // no label
        Assert.True((await Call("pick_points", new { count = 2, labels = new[] { "One", "" }, title = "Pick", description = "Why", steps })).IsError);
        Assert.True((await Call("pick_points", new { count = 3, labels = new[] { "One", "Two" }, title = "Pick", description = "Why", steps })).IsError);
        Assert.True((await Call("pick_app", new { title = "", description = "Why", steps })).IsError);
        Assert.True((await Call("pick_app", new { title = "Pick", description = "Why", steps = new[] { " " } })).IsError);
        Assert.True((await Call("pick_points", new { count = 1, labels = new[] { "One" }, title = "Pick", description = "Why", steps })).IsError);
        Assert.Empty(_host.ReadyShown);
    }

    [Fact]
    public async Task A_long_title_and_description_are_shown_whole()
    {
        string title = new('T', 300);
        string description = new('D', 2000);

        await Call("pick_point", new { label = "It", title, description, steps = new[] { "Click it" } });

        Assert.Equal(title, _host.ReadyShown[0].Title);
        Assert.Equal(description, _host.ReadyShown[0].Description);
    }

    [Fact]
    public async Task Placing_a_floating_button_asks_when_ready_then_saves_it_as_the_app()
    {
        var loop = new LoopDefinition { Name = "Spam" };
        _settings.Loops.Add(loop);

        AiToolResult result = await Call("place_floating_button", new
        {
            id = loop.Id.ToString(),
            title = "Place the Spam button",
            description = "It starts and stops Spam in overlay mode.",
            steps = new[] { "Drag the button", "Select Done" },
        });

        Assert.False(result.IsError, result.Text);
        Assert.Equal("Place the Spam button", Assert.Single(_host.ReadyShown).Title);
        Assert.Equal(loop.Id, Assert.Single(_host.Placed));
        Assert.Equal(App, Assert.Single(_host.Saved).App);
        AiActivity entry = _log.Entries().First();
        Assert.Equal(AiActivityResult.Saved, entry.Result);
        Assert.Equal(loop.Id, entry.ItemId);
        Assert.False(JsonSerializer.Deserialize<LoopDefinition>(entry.Before!, CodeJson.Options)!.FloatingButton.Enabled); // for Restore
    }

    [Fact]
    public async Task A_floating_button_is_only_for_Toggle_loops_and_macros()
    {
        var hold = new LoopDefinition { Name = "Hold", Mode = ActivationMode.Hold };
        _settings.Loops.Add(hold);
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);
        string[] steps = { "Drag it" };

        AiToolResult forHold = await Call("place_floating_button", new { id = hold.Id.ToString(), title = "Place", description = "Why", steps });
        AiToolResult forRemap = await Call("place_floating_button", new { id = remap.Id.ToString(), title = "Place", description = "Why", steps });

        Assert.Contains("Toggle", forHold.Text);
        Assert.True(forRemap.IsError);
        Assert.Empty(_host.ReadyShown);
        Assert.Empty(_host.Placed);
    }

    [Fact]
    public async Task Open_item_opens_the_editor_with_the_title_and_says_whether_the_user_saved()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);

        AiToolResult saved = await Call("open_item", new { id = remap.Id.ToString(), title = "Check the remap", description = "See its keys." });
        _host.OpenAnswer = AiAnswer.Cancelled;
        AiToolResult closed = await Call("open_item", new { id = remap.Id.ToString(), title = "Check the remap", description = "See its keys." });

        Assert.Equal((remap.Id, "Check the remap", "See its keys."), _host.Opened[0]);
        Assert.StartsWith("The user saved", saved.Text);
        Assert.Contains("without saving", closed.Text);
    }

    [Fact]
    public async Task Open_item_waits_for_running_loops_and_macros_to_stop()
    {
        var loop = new LoopDefinition { Name = "Spam" };
        _settings.Loops.Add(loop);
        _host.Running.Add(loop.Id);

        AiToolResult result = await Call("open_item", new { id = loop.Id.ToString(), title = "Check Spam", description = "Why" });

        Assert.True(result.IsError);
        Assert.Empty(_host.Opened);
    }

    [Fact]
    public async Task App_and_key_requests_give_what_the_user_picked_or_pressed()
    {
        AiToolResult app = await Call("pick_app", Asking);
        AiToolResult keys = await Call("capture_hotkey", Asking);

        Assert.Contains("notepad.exe", app.Text);
        Assert.StartsWith(KeyNames.Format(CtrlX), keys.Text);
    }

    [Fact]
    public async Task A_user_who_takes_long_gets_a_request_id_and_wait_for_user_gives_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();

        AiToolResult first = await Call("pick_point", Asking);
        string id = RequestId(first);
        AiToolResult other = await Call("pick_app", Asking);
        AiToolResult stillWaiting = await Call("wait_for_user", new { request_id = id });
        _host.Hold.SetResult(true);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.StartsWith("Waiting", first.Text);
        Assert.Equal(8, id.Length);
        Assert.True(other.IsError);
        Assert.Contains($"request_id \"{id}\"", other.Text);
        Assert.StartsWith("Waiting", stillWaiting.Text);
        Assert.Equal("{\"X\":640,\"Y\":360}", answer.Text);
        Assert.True((await Call("wait_for_user", new { request_id = id })).IsError); // given once
    }

    [Fact]
    public async Task An_answer_not_taken_yet_is_kept_when_another_request_starts()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("pick_point", Asking));
        _host.Hold.SetResult(true);
        _host.Hold = null;

        AiToolResult app = await Call("pick_app", Asking);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.False(app.IsError, app.Text);
        Assert.Equal("{\"X\":640,\"Y\":360}", answer.Text);
    }

    [Fact]
    public async Task Only_the_app_that_asked_gets_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("pick_point", Asking));
        _host.Hold.SetResult(true);

        AiToolResult other = await _tools.Call("wait_for_user", JsonSerializer.SerializeToElement(new { request_id = id }), "Codex",
            TestContext.Current.CancellationToken);
        AiToolResult mine = await Call("wait_for_user", new { request_id = id });

        Assert.True(other.IsError);
        Assert.Equal("{\"X\":640,\"Y\":360}", mine.Text);
    }

    [Fact]
    public async Task Nothing_runs_while_the_user_has_a_PuppyMacro_window_open()
    {
        _host.UserIsEditing = true;

        AiToolResult result = await Call("save_remap", CtrlXToA);

        Assert.True(result.IsError);
        Assert.Contains("PuppyMacro window open", result.Text);
        Assert.Empty(_host.Saved);
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
        Assert.Equal(_tools.All.Select(t => t.Name), AiTools.Definitions().Select(t => t.Name));
    }

    [Fact]
    public void While_PuppyMacro_is_closed_the_AI_is_told_how_to_keep_it_available()
    {
        string allOff = AiBridge.ClosedMessage(new AiBridge.ClosedSettings(false, false, false));
        string allOn = AiBridge.ClosedMessage(new AiBridge.ClosedSettings(false, true, true));

        Assert.Contains("Ask the user to open PuppyMacro", allOff);
        Assert.Contains("Start with Windows", allOff);
        Assert.Contains("Close to system tray", allOff);
        Assert.Contains("Start PuppyMacro when an AI app needs it", allOff);
        Assert.DoesNotContain("Start with Windows", allOn);
        Assert.DoesNotContain("Close to system tray", allOn);
        Assert.Contains("Start PuppyMacro when an AI app needs it", allOn);
    }
}
