using System;
using System.Globalization;
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
/// The AI tools' edge cases (FakeAiHost as the app and the user): what the user confirms is what is saved, a change the
/// History's rounding hides is still a change, the answers stay in English whatever the user's language, Let AI apps see
/// the screen covers what the screen showed in a run's log, the tools' names written in full do not touch the user's data,
/// a request of another AI app, and get_item read back by save_* as the same item.
/// </summary>
// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
public class AiToolsEdgesTests : IDisposable
{
    private const string App = "Claude Desktop";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiTools _tools;

    private static readonly object Asking = new
    {
        title = "Pick the Send button",
        count = 1,
        labels = new[] { "The Send button" },
        confirmed_steps = new[] { "1. Click the Send button: sends the message" },
        description = "We are making a macro that sends a message.",
        steps = new[] { "Open the chat", "Click the Send button" },
    };

    public AiToolsEdgesTests()
    {
        AiTools.WaitStep = TimeSpan.FromMilliseconds(300);
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null, string app = App) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), app, TestContext.Current.CancellationToken);

    private MacroDefinition SavedMacro(bool debug = false)
    {
        var macro = new MacroDefinition
        {
            Name = "Send",
            Debug = debug,
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41, DelayMs = 100 } },
        };
        _host.Macros.Macros.Add(macro);
        return macro;
    }

    // ================= What the user confirms is what is saved =================

    [Fact]
    public async Task A_Debug_the_app_keeps_as_the_user_set_it_is_not_confirmed_or_announced_as_saved()
    {
        // The app's IAiHost.Save keeps the macro's Debug as the user set it (AiTools.KeepUsersDebug, MainWindow.Ai.cs), and
        // so does FakeAiHost: an AI app's Debug never reaches the macro.
        MacroDefinition macro = SavedMacro(debug: false);
        _settings.AiAskBeforeChanging = true;

        AiToolResult result = await Call("patch_item", new
        {
            id = macro.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Debug", value = true } },
        });

        Assert.False(_host.Macros.Find(macro.Id)!.Debug); // the user's choice stays
        // AiTools.SaveItem computes the diff, the confirmation, the answer and the activity's data before the host drops
        // Debug: the user is asked to approve "Debug: off -> on", the AI app is told it was saved, and nothing of it is.
        Assert.DoesNotContain(_host.Confirmed, c => c.Lines.Any(l => l.Contains("Debug", StringComparison.Ordinal)));
        Assert.DoesNotContain("Debug", result.Text);
    }

    [Fact]
    public async Task The_activity_keeps_the_macro_as_it_was_saved_not_as_the_AI_app_sent_it()
    {
        MacroDefinition macro = SavedMacro(debug: true);

        // A rewrite that leaves Debug out (false): the host keeps the user's true.
        AiToolResult result = await Call("save_macro", new
        {
            id = macro.Id.ToString(),
            item = new { Name = "Send", Actions = new object[] { new { Type = "PressKey", Vk = 0x42, DelayMs = 100 } } },
        });

        Assert.False(result.IsError, result.Text);
        Assert.True(_host.Macros.Find(macro.Id)!.Debug);
        AiActivity saved = Assert.Single(_log.Entries(), e => e.Result == AiActivityResult.Saved);
        Assert.Equal(MacroJson.Serialize(_host.Macros.Find(macro.Id)!), saved.Data);
    }

    // ================= A change the History's rounding hides =================

    [Fact]
    public async Task A_change_in_the_third_decimal_is_saved_not_answered_as_nothing_to_change()
    {
        var loop = new LoopDefinition
        {
            Name = "Slow click",
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 2.001, IntervalUnit = IntervalUnit.Seconds } },
        };
        _settings.Loops.Add(loop);

        AiToolResult result = await Call("patch_item", new
        {
            id = loop.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Actions/0/IntervalValue", value = 2.004 } },
        });

        // ItemHistory.Diff shows numbers as "0.##": 2.001 and 2.004 are both "2", so SaveItem finds no change and saves
        // nothing, although the loop would click 3 ms later every time.
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("Nothing to change", result.Text);
        Assert.Equal(2.004, Assert.Single(_settings.Loops).Actions[0].IntervalValue);
    }

    // ================= The answers in English =================

    [Theory]
    [InlineData("update_settings", """{"settings":{"Theme":"dark"}}""")]
    [InlineData("check_hotkey", """{"hotkey":{"Vk":65,"Ctrl":true,"CtrlSide":"left"}}""")]
    [InlineData("save_component", """{"collection":{"name":"Diablo","domain":"game"},"component":{"Name":"Press A","Purpose":"act","Concept":"press_a","Actions":[{"Type":"PressKey","Vk":65}]}}""")]
    public async Task A_wrong_value_of_a_list_of_names_is_said_in_English_whatever_the_user_s_language(string tool, string args)
    {
        using JsonDocument given = JsonDocument.Parse(args);
        AiToolResult english = await _tools.Call(AiTools.Name(tool), given.RootElement, App, TestContext.Current.CancellationToken);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it");
        AiToolResult italian;
        try
        {
            italian = await _tools.Call(AiTools.Name(tool), given.RootElement, App, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }

        Assert.True(english.IsError, english.Text);
        Assert.Contains("Must be one of", english.Text);
        // Arg<T> and save_component read the JSON with CodeJson.StrictOptions outside Loc.English: its enum converter
        // writes Strings.Code_MustBeOneOf in the user's language ("Deve essere uno di questi").
        Assert.Equal(english.Text, italian.Text);
    }

    // ================= Let AI apps see the screen =================

    [Fact]
    public async Task Without_Let_AI_apps_see_the_screen_a_run_s_log_does_not_give_the_text_the_screen_showed()
    {
        MacroDefinition macro = SavedMacro(debug: true);
        // A run the user started with Debug on, logged as MacroRunner logs an If whose Text condition was not found
        // (ConditionChecker.Describe: what it read on the screen).
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "20261019-101010-000-send");
        Directory.CreateDirectory(folder);
        var run = new RunLog(folder, macro, "a run started by the user");
        run.Event("#1 If: not true after 0.5 s -> the macro stops (Stop the macro)");
        run.Line("  Text \"Total\": \"Total\" not found; read \"IBAN IT60 X054 2811 1010 0000 0123 456\"");
        run.Line("  Pixel \"Ready\": saw #3BD16F, wanted #FFFFFF within 5%");
        run.End("the If stopped the macro");
        RunLog.WaitWritten(TimeSpan.FromSeconds(10));
        _settings.AiSeeScreen = false;

        AiToolResult last = await Call("get_last_run", new { id = macro.Id.ToString() });
        string? log = last.Files.FirstOrDefault(f => Path.GetFileName(f) == RunLog.LogFile);
        string read = log == null ? "" : (await Call("read_resource", new { uri = DebugStore.UriOf(log) })).Text;

        // SeeScreenOff: "pictures of the screen and what it shows are not given to AI apps", and check_condition (which
        // answers the text read and the color seen) is refused for it. The run's log gives both.
        Assert.DoesNotContain("IBAN", read);
        Assert.DoesNotContain("#3BD16F", read);
    }

    // ================= Tools' names written in full =================

    [Fact]
    public async Task An_error_that_names_a_component_names_it_as_it_is()
    {
        var component = new LibraryComponent
        {
            Name = "Get the item", Purpose = ComponentPurpose.Act, Concept = "get_item",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x45 } },
        };
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Diablo", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe", Components = { component },
        });
        string name = ComponentLibrary.StructuredName(_host.Macros.Library.Collections[0], component);

        AiToolResult result = await Call("delete_item", new { id = component.Id.ToString() });

        // AiTools.Call writes every tool's name in full in an error (Prefixed): "get_item", the concept, becomes
        // "puppymacro_get_item", and search_library finds no such name.
        Assert.True(result.IsError);
        Assert.True(result.Text.Contains($"\"{name}\"", StringComparison.Ordinal), result.Text);
    }

    // ================= A request of another AI app =================

    [Fact]
    public async Task Another_app_is_not_told_to_wait_for_a_request_it_can_not_wait_for()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        AiToolResult waiting = await Call("pick_points", Asking, app: "Codex");
        Assert.StartsWith("Waiting:", waiting.Text);
        string id = Regex.Match(waiting.Text, "request_id \"([0-9a-f]+)\"").Groups[1].Value;

        AiToolResult refused = await Call("pick_points", Asking);
        AiToolResult told = await Call("wait_for_user", new { request_id = id });
        _host.Hold.SetResult(true);

        // The refusal tells Claude Desktop to call wait_for_user with Codex's request_id, which only Codex can wait for.
        Assert.True(refused.IsError);
        Assert.False(told.IsError && refused.Text.Contains("wait_for_user", StringComparison.Ordinal),
            $"Told: {refused.Text}\nThen: {told.Text}");
    }

    // ================= get_item read back by save_* =================

    [Fact]
    public async Task A_macro_read_with_get_item_and_saved_as_it_is_has_nothing_to_change()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        macro.Actions.Add(new MacroAction
        {
            Type = MacroActionType.If,
            If = new MacroIf
            {
                Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, Name = "Ready", X = 5, Y = 5, Color = "#FFFFFF", Tolerance = 5 } },
                Then = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x0D } },
                Wait = ConditionWait.UpTo,
                WaitSeconds = 2.5,
            },
        });
        macro.Speed = 2;
        _host.Macros.Macros.Add(macro);

        AiToolResult read = await Call("get_item", new { id = macro.Id.ToString() });
        using JsonDocument item = JsonDocument.Parse(read.Text);
        AiToolResult saved = await _tools.Call(AiTools.Name("save_macro"),
            JsonSerializer.SerializeToElement(new { id = macro.Id.ToString(), item = item.RootElement }), App, TestContext.Current.CancellationToken);

        Assert.Equal("Nothing to change: the macro is already like this.", saved.Text);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task A_loop_and_a_remap_read_with_get_item_and_saved_as_they_are_have_nothing_to_change()
    {
        var loop = new LoopDefinition
        {
            Name = "Spam", Mode = ActivationMode.Toggle, Hotkey = HotkeyBinding.FromKey(0x75), AppExe = "notepad.exe", ShowInOverlay = false,
            Actions = { new LoopAction { Type = ActionType.Text, Text = "gg", EnterAfter = true, IntervalValue = 1.5, IntervalUnit = IntervalUnit.Seconds } },
            FloatingButton = { Enabled = true, Label = "SP", X = 10, Y = 20 },
        };
        var remap = new RemapDefinition
        {
            Source = new HotkeyBinding { Vk = 0x4A, Alt = true, AltSide = ModifierSide.Left },
            Target = HotkeyBinding.FromKey(0x25), Note = "Left", AppExe = "notepad.exe", Enabled = false,
        };
        _settings.Loops.Add(loop);
        _settings.Remaps.Add(remap);

        foreach (var (kind, id) in new[] { ("loop", loop.Id), ("remap", remap.Id) })
        {
            AiToolResult read = await Call("get_item", new { id = id.ToString() });
            using JsonDocument item = JsonDocument.Parse(read.Text);
            AiToolResult saved = await _tools.Call(AiTools.Name("save_" + kind),
                JsonSerializer.SerializeToElement(new { id = id.ToString(), item = item.RootElement }), App, TestContext.Current.CancellationToken);
            Assert.Equal($"Nothing to change: the {kind} is already like this.", saved.Text);
        }
        Assert.Empty(_host.Saved);
    }

    // ================= update_actions and patch_item compare numbers as numbers =================

    [Theory]
    [InlineData("120")]
    [InlineData("120.0")]
    [InlineData("1.2e2")]
    public async Task Update_actions_finds_a_delay_however_its_number_is_written(string delay)
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);
        using JsonDocument args = JsonDocument.Parse("{\"id\":\"" + macro.Id + "\",\"where\":{\"DelayMs\":" + delay + "},\"set\":{\"DelayMs\":1200}}");

        AiToolResult result = await _tools.Call(AiTools.Name("update_actions"), args.RootElement, App, TestContext.Current.CancellationToken);

        Assert.False(result.IsError, result.Text);
        Assert.Equal(30, _host.Macros.Find(macro.Id)!.Actions.Count(a => a.DelayMs == 1200));
    }

    [Fact]
    public async Task A_patch_s_test_compares_numbers_as_numbers()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);
        using JsonDocument args = JsonDocument.Parse("{\"id\":\"" + macro.Id + "\",\"patch\":[{\"op\":\"test\",\"path\":\"/Actions/0/DelayMs\",\"value\":120.0},{\"op\":\"replace\",\"path\":\"/Actions/0/DelayMs\",\"value\":125}]}");

        AiToolResult result = await _tools.Call(AiTools.Name("patch_item"), args.RootElement, App, TestContext.Current.CancellationToken);

        Assert.False(result.IsError, result.Text);
        Assert.Equal(125, _host.Macros.Find(macro.Id)!.Actions[0].DelayMs);
    }

    // ================= Restore of settings =================

    [Fact]
    public async Task Restore_takes_away_a_global_hotkey_an_AI_app_gave_where_the_user_had_none()
    {
        _settings.OpenMainHotkey = null; // the user cleared it (Settings: MainWindow saves null)
        _settings.AiAskBeforeSettings = false;

        AiToolResult changed = await Call("update_settings", new { settings = new { OpenMainHotkey = new { Vk = 0x78 } } });
        Assert.False(changed.IsError, changed.Text);
        _settings.OpenMainHotkey = HotkeyBinding.FromKey(0x78); // as the app's ApplySettings does
        AiActivity entry = Assert.Single(_log.Entries(), e => e.ItemKind == "settings");

        // As the AI page's Restore reads it (MainWindow.RestoreActivity).
        AiSettingsValues before = JsonSerializer.Deserialize<AiSettingsValues>(entry.Before!, CodeJson.Options)!;
        int applied = _host.Applied.Count;
        string? problem = _tools.RestoreSettings(before);

        // Previous() keeps "no hotkey" as null, which every reader takes as "keep this setting": Restore applies nothing
        // and says it is done, and F9 stays.
        bool cleared = _host.Applied.Count > applied && _host.Applied[^1].OpenMainHotkey is { IsSet: false };
        Assert.True(problem != null || cleared, $"Restore answered {(problem == null ? "done" : problem)}; Before: {entry.Before}");
    }

    // ================= Arguments the schema does not have =================

    [Fact]
    public async Task Update_actions_with_an_argument_it_does_not_know_changes_nothing()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);

        // "Where" as the item's own fields are written (DelayMs, Type): the schema's "where" is lowercase, and its
        // additionalProperties is false.
        AiToolResult result = await Call("update_actions", new { id = macro.Id.ToString(), Where = new { DelayMs = 120 }, set = new { DelayMs = 1200 } });

        // UpdateActions reads only "where": left out, it is every action, so the 30 delays of 80 ms and the paste and the
        // move become 1200 ms too, saved at once.
        Assert.True(result.IsError, result.Text);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task Save_macro_with_get_item_s_Id_in_the_item_rewrites_that_macro_instead_of_adding_a_copy()
    {
        MacroDefinition macro = SavedMacro();
        AiToolResult read = await Call("get_item", new { id = macro.Id.ToString() });
        var item = System.Text.Json.Nodes.JsonNode.Parse(read.Text)!.AsObject();
        item["Actions"]![0]!["DelayMs"] = 250;

        // get_item gives the Id inside the item; the AI app leaves out save_macro's own "id".
        AiToolResult saved = await _tools.Call(AiTools.Name("save_macro"),
            JsonSerializer.SerializeToElement(new { item }), App, TestContext.Current.CancellationToken);

        // ParseItem gives the item a new Id whatever its "Id" says: a second "Send" is made and the first one stays.
        Assert.Single(_host.Macros.Macros);
        Assert.Equal(250, _host.Macros.Find(macro.Id)!.Actions[0].DelayMs);
        Assert.False(saved.IsError && !saved.Text.Contains(macro.Id.ToString(), StringComparison.Ordinal), saved.Text);
    }
}
