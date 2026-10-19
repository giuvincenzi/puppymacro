using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Screen;
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
    public string? UserHasOpen { get; set; }

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

    public string? SaveComponent(LibraryComponent component, LibraryCollection? created, string app,
        IReadOnlyList<MacroDefinition>? macros = null, IReadOnlyList<LibraryComponent>? callers = null)
    {
        // The calls the new signature changed, saved with it.
        foreach (var macro in macros ?? Array.Empty<MacroDefinition>())
            if (Macros.Find(macro.Id) is { } saved)
                saved.CopyFrom(macro);
        foreach (var caller in callers ?? Array.Empty<LibraryComponent>())
            if (Macros.Library.Find(caller.Id) is { } calling)
                calling.Collection.Components[calling.Collection.Components.IndexOf(calling.Component)] = caller.Clone();
        if (Macros.Library.Find(component.Id) is { } found)
        {
            found.Collection.Components[found.Collection.Components.IndexOf(found.Component)] = component.Clone();
            return null;
        }
        if (created == null)
            return "no collection";
        var collection = Macros.Library.FindCollection(created.Id) ?? created;
        if (!Macros.Library.Collections.Contains(collection))
            Macros.Library.Collections.Add(collection);
        collection.Components.Add(component.Clone());
        return null;
    }

    public string? AddCollections(IReadOnlyList<LibraryCollection> collections, string app)
    {
        foreach (var collection in collections)
            if (Macros.Library.FindCollection(collection.Id) == null)
                Macros.Library.Collections.Add(collection.Clone());
        return null;
    }

    public void Save(object item, string app)
    {
        AiTools.KeepUsersDebug(item, Macros); // as the app's IAiHost.Save (MainWindow.Ai.cs)
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

    public string? DeleteComponent(Guid id, string app)
    {
        if (Macros.Library.Find(id) is not { } found)
            return null;
        if (Macros.Library.CanDeleteComponent(found.Component, Macros.Macros) is string used)
            return used;
        found.Collection.Components.Remove(found.Component);
        return null;
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

    /// <summary>As the window: a pixel at 640, 360 (#3BD16F), an image of 40 × 20 taken at 100, 200, an area at 10, 20, 300 × 200.</summary>
    public Task<(AiAnswer Answer, AiScreenPick? Pick)> PickOnScreen(string app, string message, AiScreenPickKind kind) =>
        Task.FromResult<(AiAnswer, AiScreenPick?)>((AiAnswer.Done, kind switch
        {
            AiScreenPickKind.Pixel => new AiScreenPick(640, 360, 1, 1, Color: "#3BD16F"),
            AiScreenPickKind.Image => new AiScreenPick(100, 200, 40, 20, ImageId: Macros.Images.Add(new byte[] { 1, 2, 3 })),
            _ => new AiScreenPick(10, 20, 300, 200),
        }));

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

    public AiAnswer ImportAnswer { get; set; } = AiAnswer.Done;
    public List<string?> ImportShown { get; } = new();

    /// <summary>As Import with every item checked: they are added.</summary>
    public Task<(AiAnswer Answer, List<object> Items)> ShowImport(string app, string? text)
    {
        ImportShown.Add(text);
        if (ImportAnswer != AiAnswer.Done || text == null)
            return Task.FromResult((ImportAnswer, new List<object>()));
        List<object> items = ItemFile.Read(text, Settings, Macros, ItemFile.AppVersion).SelectedItems.ToList();
        foreach (object item in items)
            Save(item, app);
        return Task.FromResult((AiAnswer.Done, items));
    }

    public AiAnswer ExportAnswer { get; set; } = AiAnswer.Done;
    public List<IReadOnlyCollection<Guid>> ExportShown { get; } = new();

    /// <summary>As Export: the user saves the checked items.</summary>
    public Task<(AiAnswer Answer, List<object> Items, string? File)> ShowExport(string app, IReadOnlyCollection<Guid> ids)
    {
        ExportShown.Add(ids);
        List<object> items = Settings.Loops.Where(l => ids.Contains(l.Id)).Cast<object>().ToList();
        return Task.FromResult<(AiAnswer, List<object>, string?)>(ExportAnswer == AiAnswer.Done
            ? (AiAnswer.Done, items, @"C:\Users\Someone\Documents\Farm.puppyitems")
            : (ExportAnswer, new List<object>(), null));
    }

    public void StopAll() => StoppedAll = true;
    public IReadOnlyList<string> RunningApps() => new[] { "notepad.exe" };

    /// <summary>What Now on the screen saw: by default the image found at 10, 20, 97% similar.</summary>
    public ConditionReport Report { get; set; } = new(true, new OpenCvSharp.Point(10, 20), 0.97);
    public List<MacroCondition> Checked { get; } = new();

    public Task<ConditionReport> CheckCondition(MacroCondition condition)
    {
        Checked.Add(condition);
        return Task.FromResult(Report);
    }

    /// <summary>One monitor of 800 × 600 at 0, 0: gray, with a pattern at 100, 100 (40 × 40) and a white pixel at 5, 5.</summary>
    public static OpenCvSharp.Mat Screen()
    {
        var picture = new OpenCvSharp.Mat(600, 800, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.All(128));
        for (int y = 100; y < 140; y++)
            for (int x = 100; x < 140; x++)
                picture.Set(y, x, new OpenCvSharp.Vec3b((byte)(x * 5), (byte)(y * 3), (byte)((x + y) * 2)));
        picture.Set(5, 5, new OpenCvSharp.Vec3b(255, 255, 255));
        return picture;
    }

    public Task<List<(OpenCvSharp.Rect Bounds, OpenCvSharp.Mat Picture)>> TakeScreen() =>
        Task.FromResult(new List<(OpenCvSharp.Rect, OpenCvSharp.Mat)> { (new OpenCvSharp.Rect(0, 0, 800, 600), Screen()) });

    public AiAnswer RecordAnswer { get; set; } = AiAnswer.Done;

    /// <summary>As the recorder: Press A, then a click at 120, 120 on the pattern, each with the screen at that moment.</summary>
    public Task<(AiAnswer Answer, AiRecording? Recording)> Record(string app, bool mouseMovement) =>
        Task.FromResult<(AiAnswer, AiRecording?)>(RecordAnswer != AiAnswer.Done ? (RecordAnswer, null) : (AiAnswer.Done, new AiRecording(
            new List<MacroAction>
            {
                new() { Type = MacroActionType.PressKey, Vk = 0x41 },
                new() { Type = MacroActionType.Click, Vk = 1, X = 120, Y = 120, DelayMs = 500 },
            },
            new List<AiRecordedInput>
            {
                new("Key down A", false, 0, 0, 100, new() { (new OpenCvSharp.Rect(0, 0, 800, 600), Screen()) }),
                new("Click Left at 120,120", true, 120, 120, 600, new() { (new OpenCvSharp.Rect(0, 0, 800, 600), Screen()) }),
            })));

    public List<(Guid Id, double MaxSeconds, string Folder)> Tested { get; } = new();

    /// <summary>As a test run: its log, in the call's folder, says it pressed A (with a picture) and ended.</summary>
    public Task<AiTestRun?> TestMacro(Guid id, string app, double maxSeconds, string folder)
    {
        Tested.Add((id, maxSeconds, folder));
        var log = new RunLog(folder, Macros.Find(id), $"a test by {app}");
        log.Event("#1 Press A, held 50 ms", new List<(OpenCvSharp.Rect, OpenCvSharp.Mat)> { (new OpenCvSharp.Rect(0, 0, 800, 600), Screen()) });
        log.End("finished");
        RunLog.WaitWritten(TimeSpan.FromSeconds(10));
        return Task.FromResult<AiTestRun?>(new AiTestRun("finished", folder));
    }

    public List<string> FinishedFor { get; } = new();

    public void Finished(string app) => FinishedFor.Add(app);
}

// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
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
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
    }

    public void Dispose() => _folder.Dispose();

    /// <summary>A tool by its name without the prefix ("get_item") or its full one.</summary>
    private Task<AiToolResult> Call(string tool, object? args = null) =>
        _tools.Call(tool.StartsWith(AiTools.Prefix, StringComparison.Ordinal) ? tool : AiTools.Name(tool),
            JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static readonly HotkeyBinding CtrlX = new() { Vk = 0x58, Ctrl = true };

    private static readonly object CtrlXToA = new { item = new { Source = new { Vk = 0x58, Ctrl = true }, Target = new { Vk = 0x41 } } };

    private static readonly string[] Plan = { "1. Click the Send button: sends the message" };

    private static readonly object Asking = new
    {
        title = "Pick the Send button",
        count = 1,
        labels = new[] { "The Send button" },
        confirmed_steps = Plan,
        description = "We are making a macro that sends a message.",
        steps = new[] { "Open the chat", "Press Enter", "Click the Send button" },
    };

    /// <summary>What every request that opens a window takes (pick_points also takes count, labels and confirmed_steps).</summary>
    private static readonly object AskingWindow = new
    {
        title = "Pick the Send button",
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
    public async Task Get_item_gives_the_compact_json_and_an_error_for_an_unknown_id()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41), Note = "Cut is A" };
        _settings.Remaps.Add(remap);

        AiToolResult found = await Call("get_item", new { id = remap.Id.ToString() });
        AiToolResult missing = await Call("get_item", new { id = Guid.NewGuid().ToString() });

        Assert.Equal(AiJson.Compact(remap), found.Text);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task Patching_a_big_macro_changes_only_what_it_says_and_answers_with_the_changes()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);

        AiToolResult result = await Call("patch_item", new
        {
            id = macro.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Actions/4/DelayMs", value = 250 } },
        });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Saved the changes to the macro:\n- Action 5", result.Text);
        Assert.DoesNotContain("\"Actions\"", result.Text); // the answer is the changes, not the macro
        var (saved, app) = Assert.Single(_host.Saved);
        Assert.Equal(App, app);
        MacroDefinition after = Assert.IsType<MacroDefinition>(saved);
        Assert.Equal(250, after.Actions[4].DelayMs);
        Assert.Equal(macro.Actions.Count, after.Actions.Count);
        Assert.Equal(macro.Id, after.Id);
        Assert.Contains(_log.Entries(), e => e.Result == AiActivityResult.Saved && e.Before != null); // Restore can undo it
    }

    [Fact]
    public async Task Update_actions_changes_every_matching_delay_in_one_call()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);

        AiToolResult result = await Call("update_actions", new { id = macro.Id.ToString(), where = new { DelayMs = 120 }, set = new { DelayMs = 1200 } });

        Assert.False(result.IsError, result.Text);
        MacroDefinition after = Assert.IsType<MacroDefinition>(Assert.Single(_host.Saved).Item);
        Assert.Equal(30, after.Actions.Count(a => a.DelayMs == 1200));
        Assert.Contains("- Action 1", result.Text);
    }

    [Fact]
    public async Task Update_actions_is_only_for_loops_and_macros()
    {
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);

        AiToolResult result = await Call("update_actions", new { id = remap.Id.ToString(), set = new { DelayMs = 1 } });

        Assert.True(result.IsError);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task A_patch_that_breaks_the_item_saves_nothing()
    {
        MacroDefinition macro = AiJsonTests.BigMacro();
        _host.Macros.Macros.Add(macro);

        AiToolResult badValue = await Call("patch_item", new
        {
            id = macro.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Speed", value = 3 } },
        });
        AiToolResult badPath = await Call("patch_item", new
        {
            id = macro.Id.ToString(),
            patch = new object[] { new { op = "replace", path = "/Actions/999/DelayMs", value = 1 } },
        });

        Assert.True(badValue.IsError);
        Assert.True(badPath.IsError);
        Assert.Contains("Operation 1", badPath.Text);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task A_new_item_answers_with_its_id_not_its_json()
    {
        AiToolResult result = await Call("save_remap", CtrlXToA);

        Assert.StartsWith("Saved the new remap. Its Id: " + _settings.Remaps.Single().Id, result.Text);
        Assert.DoesNotContain("\"Source\"", result.Text);
    }

    [Fact]
    public async Task The_schema_for_AI_apps_gives_the_defaults()
    {
        AiToolResult schema = await Call("get_schema", new { kind = "macro" });

        using JsonDocument doc = JsonDocument.Parse(schema.Text);
        JsonElement properties = doc.RootElement.GetProperty("properties");
        Assert.True(properties.GetProperty("ShowInOverlay").GetProperty("default").GetBoolean());
        Assert.Equal("Once", properties.GetProperty("Repeat").GetProperty("default").GetString());
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
    public async Task A_loop_on_the_keys_of_a_remap_for_the_same_apps_is_refused_as_in_the_editor()
    {
        _settings.Remaps.Add(new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x42), Note = "Cut" });
        object Spam(string? app) => new
        {
            item = new { Name = "Spam", AppExe = app, Hotkey = new { Vk = 0x58, Ctrl = true }, Actions = new[] { new { Type = "Key", KeyVk = 0x87, IntervalValue = 100 } } },
        };

        AiToolResult refused = await Call("save_loop", Spam(null));
        AiToolResult forGame = await Call("save_loop", Spam("game.exe")); // the remap for all apps works in the other apps

        Assert.True(refused.IsError);
        Assert.Contains("Ctrl + X is already used by the remap \"Cut\".", refused.Text);
        Assert.False(forGame.IsError);
        Assert.Single(_host.Saved);
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
    public async Task The_Open_PuppyMacro_hotkey_is_read_and_changed_like_the_other_global_hotkeys()
    {
        _settings.OpenMainHotkey = AppSettings.DefaultOpenMainHotkey();

        Assert.Contains("\"OpenMainHotkey\"", (await Call("get_settings", new { })).Text);
        Assert.Contains("\"OpenMain\": \"Alt + Shift + A\"", (await Call("get_overview", new { })).Text);

        AiToolResult clash = await Call("update_settings", new { settings = new { OpenMainHotkey = new { Vk = 0x53, Alt = true, Shift = true } } });
        Assert.True(clash.IsError);
        Assert.Contains("Open PuppyMacro hotkey: Alt + Shift + S is already used by Stop all.", clash.Text);

        AiToolResult result = await Call("update_settings", new { settings = new { OpenMainHotkey = new { Vk = 0x7A } } });
        Assert.StartsWith("Settings changed", result.Text);
        Assert.Contains("Open PuppyMacro hotkey: Alt + Shift + A → F11", Assert.Single(_host.Confirmed).Lines);
        Assert.Equal(0x7A, Assert.Single(_host.Applied).OpenMainHotkey!.Vk);
    }

    [Fact]
    public async Task Unknown_settings_are_refused()
    {
        AiToolResult result = await Call("update_settings", new { settings = new { AllowAiAccess = false } });

        Assert.True(result.IsError);
        Assert.Empty(_host.Applied);
    }

    [Fact]
    public async Task The_user_sees_their_language_and_the_AI_app_gets_English()
    {
        // No target; Ctrl+Y, so the remap saved meanwhile (Ctrl+X) does not add a clash the first answer did not have.
        object broken = new { item = new { Source = new { Vk = 0x59, Ctrl = true } } };
        AiToolResult refusedInEnglish = await Call("save_remap", broken);
        CultureInfo italian = CultureInfo.GetCultureInfo("it");
        CultureInfo.CurrentUICulture = italian;
        try
        {
            _settings.AiAskBeforeChanging = true;
            AiToolResult saved = await Call("save_remap", CtrlXToA);
            AiToolResult refused = await Call("save_remap", broken);

            // The confirmation window and the activity: Italian.
            Assert.Equal(Strings.ResourceManager.GetString("Ai_ConfirmNew_remap", italian), _host.Confirmed[0].Title);
            Assert.NotEqual("Save this new remap?", _host.Confirmed[0].Title);
            Assert.NotEqual(_log.Entries()[0].Request, _log.Entries()[0].ShownRequest);
            // The answers: English, also the checks' problems that come from code shared with the UI.
            Assert.StartsWith("Saved the new remap.", saved.Text);
            Assert.Equal(refusedInEnglish.Text, refused.Text);
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }
    }

    [Fact]
    public async Task The_language_is_left_to_the_user()
    {
        // It applies only after a restart, which would end the AI app's connection (AiSettingsValues).
        AiToolResult result = await Call("update_settings", new { settings = new { Language = "it" } });

        Assert.True(result.IsError);
        Assert.Empty(_host.Applied);
        Assert.DoesNotContain("Language", (await Call("get_settings", new { })).Text);
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
        AiToolResult one = await Call("pick_points", Asking);
        AiToolResult three = await Call("pick_points", new
        {
            confirmed_steps = Plan,
            count = 3,
            labels = new[] { "First item", "Second item", "Third item" },
            title = "Pick the 3 items",
            description = "We are making a macro.",
            steps = new[] { "Click the 3 items" },
        });

        Assert.Equal("[{\"X\":640,\"Y\":360}]", one.Text);
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

        AiToolResult result = await Call("pick_points", Asking);

        Assert.Contains("cancelled", result.Text);
        Assert.Empty(_host.PointsAsked);
    }

    [Fact]
    public async Task Interactions_need_a_title_a_description_and_steps()
    {
        string[] steps = { "Click it" };
        string[] it = { "It" };
        Assert.True((await Call("pick_points", new { count = 1, labels = it, title = "Pick", description = "", steps })).IsError);
        Assert.True((await Call("pick_points", new { confirmed_steps = Plan, count = 1, title = "Pick", description = "Why", steps })).IsError); // no label
        Assert.True((await Call("pick_points", new { count = 1, labels = it, title = "Pick", description = "Why", steps })).IsError); // no steps confirmed
        Assert.True((await Call("pick_points", new { confirmed_steps = new[] { " " }, count = 1, labels = it, title = "Pick", description = "Why", steps })).IsError);
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

        await Call("pick_points", new { confirmed_steps = Plan, count = 1, labels = new[] { "It" }, title, description, steps = new[] { "Click it" } });

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
        AiToolResult app = await Call("pick_app", AskingWindow);
        AiToolResult keys = await Call("capture_hotkey", AskingWindow);

        Assert.Contains("notepad.exe", app.Text);
        Assert.StartsWith(KeyNames.Format(CtrlX), keys.Text);
    }

    [Fact]
    public async Task Screen_picks_give_a_pixel_and_its_color_an_image_kept_by_PuppyMacro_and_an_area()
    {
        AiToolResult pixel = await Call("pick_pixel", AskingWindow);
        AiToolResult image = await Call("take_image", AskingWindow);
        AiToolResult area = await Call("draw_area", AskingWindow);

        Assert.False(pixel.IsError || image.IsError || area.IsError);
        Assert.Equal("{\"X\":640,\"Y\":360,\"Color\":\"#3BD16F\"}", pixel.Text);
        string id = JsonDocument.Parse(image.Text).RootElement.GetProperty("Id").GetString()!;
        Assert.True(_host.Macros.Images.Has(id));
        Assert.Contains("\"X\":100,\"Y\":200,\"Width\":40,\"Height\":20", image.Text);
        Assert.Equal("{\"AreaX\":10,\"AreaY\":20,\"AreaWidth\":300,\"AreaHeight\":200}", area.Text);
    }

    [Fact]
    public async Task Screen_recognition_is_a_setting_AI_apps_read_and_change()
    {
        AiToolResult read = await Call("get_settings", new { });
        AiToolResult changed = await Call("update_settings", new { settings = new { RecognitionDevice = "Cpu" } });

        Assert.Contains("\"RecognitionDevice\"", read.Text);
        Assert.False(changed.IsError, changed.Text);
        Assert.Equal(RecognitionDevice.Cpu, _host.Applied.Last().RecognitionDevice);
    }

    [Fact]
    public async Task A_user_who_takes_long_gets_a_request_id_and_wait_for_user_gives_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();

        AiToolResult first = await Call("pick_points", Asking);
        string id = RequestId(first);
        AiToolResult other = await Call("pick_app", AskingWindow);
        AiToolResult stillWaiting = await Call("wait_for_user", new { request_id = id });
        _host.Hold.SetResult(true);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.StartsWith("Waiting", first.Text);
        Assert.Equal(8, id.Length);
        Assert.True(other.IsError);
        Assert.Contains($"request_id \"{id}\"", other.Text);
        Assert.StartsWith("Waiting", stillWaiting.Text);
        Assert.Equal("[{\"X\":640,\"Y\":360}]", answer.Text);
        Assert.True((await Call("wait_for_user", new { request_id = id })).IsError); // given once
    }

    [Fact]
    public async Task An_answer_not_taken_yet_is_kept_when_another_request_starts()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("pick_points", Asking));
        _host.Hold.SetResult(true);
        _host.Hold = null;

        AiToolResult app = await Call("pick_app", AskingWindow);
        AiToolResult answer = await Call("wait_for_user", new { request_id = id });

        Assert.False(app.IsError, app.Text);
        Assert.Equal("[{\"X\":640,\"Y\":360}]", answer.Text);
    }

    [Fact]
    public async Task Only_the_app_that_asked_gets_the_answer()
    {
        _host.Hold = new TaskCompletionSource<bool>();
        string id = RequestId(await Call("pick_points", Asking));
        _host.Hold.SetResult(true);

        AiToolResult other = await _tools.Call("wait_for_user", JsonSerializer.SerializeToElement(new { request_id = id }), "Codex",
            TestContext.Current.CancellationToken);
        AiToolResult mine = await Call("wait_for_user", new { request_id = id });

        Assert.True(other.IsError);
        Assert.Equal("[{\"X\":640,\"Y\":360}]", mine.Text);
    }

    [Fact]
    public async Task Nothing_runs_while_the_user_has_a_PuppyMacro_window_open_and_the_answer_names_it()
    {
        _host.UserHasOpen = "the macro editor of \"Salvage\"";

        AiToolResult result = await Call("save_remap", CtrlXToA);
        AiToolResult picked = await Call("pick_app", new { title = "Pick", description = "Why", steps = new[] { "Click it" } });
        using JsonDocument overview = JsonDocument.Parse((await Call("get_overview", new { })).Text);

        Assert.True(result.IsError);
        Assert.StartsWith("The user has the macro editor of \"Salvage\" open in PuppyMacro.", result.Text);
        Assert.True(picked.IsError);
        Assert.Contains("the macro editor of \"Salvage\"", picked.Text);
        Assert.Empty(_host.Saved);
        Assert.Empty(_host.ReadyShown);
        Assert.True(overview.RootElement.GetProperty("UserIsEditing").GetBoolean());
        Assert.Equal("the macro editor of \"Salvage\"", overview.RootElement.GetProperty("UserHasOpen").GetString());
    }

    [Fact]
    public async Task Stop_all_stops_everything_and_only_a_test_plays_a_macro()
    {
        AiToolResult result = await Call("stop_all");

        Assert.True(_host.StoppedAll);
        Assert.False(result.IsError);
        // An AI app plays a macro only as a test, after the user's Start (test_macro): nothing starts loops or macros.
        Assert.DoesNotContain(_tools.All, t => t.Name.StartsWith("puppymacro_start", StringComparison.Ordinal));
        Assert.Contains(_tools.All, t => t.Name == "puppymacro_test_macro");
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
    // ================= Sharing (.puppyitems) =================

    private static LoopDefinition Farm() => new()
    {
        Name = "Farm",
        Hotkey = HotkeyBinding.FromKey(0x75),
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds } },
    };

    private static MacroDefinition Craft() => new()
    {
        Name = "Craft",
        Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } },
    };

    private static string FileWith(params object[] items) => ItemFile.Write(items, ItemFile.AppVersion);

    [Fact]
    public async Task Export_items_gives_a_file_with_the_items_asked_or_all()
    {
        LoopDefinition farm = Farm();
        _settings.Loops.Add(farm);
        _host.Macros.Macros.Add(Craft());
        var remap = new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) };
        _settings.Remaps.Add(remap);

        AiToolResult some = await Call("export_items", new { ids = new[] { farm.Id.ToString(), remap.Id.ToString() } });
        AiToolResult all = await Call("export_items");
        AiToolResult unknown = await Call("export_items", new { ids = new[] { Guid.NewGuid().ToString() } });

        var empty = new AppSettings();
        var macros = new MacroLibrary(_folder.File("other"));
        Assert.Equal(new[] { "loop", "remap" }, ItemFile.Read(some.Text, empty, macros, ItemFile.AppVersion).Entries.Select(e => e.Kind));
        Assert.Equal(3, ItemFile.Read(all.Text, empty, macros, ItemFile.AppVersion).Entries.Count);
        Assert.True(unknown.IsError);
        Assert.Contains(_log.Entries(), e => e.Kind == AiActivityKind.Read && e.Request == "Exported 2 items");
    }

    [Fact]
    public async Task Import_items_adds_new_disabled_items_without_replacing_any()
    {
        LoopDefinition farm = Farm();
        _settings.Loops.Add(farm);
        string text = FileWith(Farm(), Craft());

        AiToolResult result = await Call("import_items", new { content = text });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Imported 2 items, all disabled", result.Text);
        Assert.Contains("You already have a loop with this name, so this one is called \"Farm (2)\".", result.Text);
        Assert.Contains("F6 is already used by the loop \"Farm\", so this comes in without a hotkey.", result.Text);
        Assert.Equal(2, _host.Saved.Count);
        Assert.All(_host.Saved, s => Assert.Equal(App, s.App));
        var imported = Assert.IsType<LoopDefinition>(_host.Saved[0].Item);
        Assert.NotEqual(farm.Id, imported.Id);
        Assert.False(imported.Enabled);
        Assert.Null(imported.Hotkey);
        Assert.Equal(2, _settings.Loops.Count); // the user's Farm is still there
        Assert.Equal(2, _log.Entries().Count(e => e.Result == AiActivityResult.Saved && e.Request.StartsWith("Imported")));
    }

    [Fact]
    public async Task Import_items_takes_only_the_chosen_items_and_the_file_as_a_json_object()
    {
        JsonElement file = JsonSerializer.Deserialize<JsonElement>(FileWith(Farm(), Craft()));

        AiToolResult result = await Call("import_items", new { content = file, only = new[] { 1 } });
        AiToolResult wrong = await Call("import_items", new { content = file, only = new[] { 5 } });

        Assert.False(result.IsError, result.Text);
        Assert.IsType<MacroDefinition>(Assert.Single(_host.Saved).Item);
        Assert.True(wrong.IsError);
        Assert.Equal("The file has no item 5: its Items go from 0 to 1.", wrong.Text);
    }

    [Fact]
    public async Task Import_items_refuses_a_file_that_is_not_an_items_file()
    {
        AiToolResult notAFile = await Call("import_items", new { content = "hello" });

        Assert.True(notAFile.IsError);
        Assert.Equal("This is not a PuppyMacro items file.", notAFile.Text);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task A_remap_imported_without_its_hotkey_cannot_be_enabled()
    {
        _settings.Remaps.Add(new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x41) });

        AiToolResult taken = await Call("import_items", new
        {
            content = FileWith(new RemapDefinition { Source = CtrlX, Target = HotkeyBinding.FromKey(0x42) }),
        });

        Assert.False(taken.IsError);
        Assert.Contains("is already remapped for all apps, so this comes in without a hotkey.", taken.Text);
        var imported = (RemapDefinition)Assert.Single(_host.Saved).Item;
        Assert.Null(imported.Source);
        Assert.False(imported.Enabled);

        AiToolResult enable = await Call("set_enabled", new { id = imported.Id.ToString(), enabled = true });

        Assert.True(enable.IsError);
        Assert.Contains("has no hotkey", enable.Text);
        Assert.False(imported.Enabled);
    }

    [Fact]
    public async Task Import_items_asks_first_with_Ask_before_changing_and_a_cancel_imports_nothing()
    {
        _settings.AiAskBeforeChanging = true;
        _host.ConfirmAnswer = AiAnswer.Cancelled;

        AiToolResult cancelled = await Call("import_items", new { content = FileWith(Farm(), Craft()) });
        _host.ConfirmAnswer = AiAnswer.Done;
        AiToolResult confirmed = await Call("import_items", new { content = FileWith(Craft()) });

        Assert.Equal("The user cancelled: nothing changed.", cancelled.Text);
        Assert.Equal("Import these 2 items?", _host.Confirmed[0].Title);
        Assert.Equal(new[] { "Loop \"Farm\"", "Macro \"Craft\"" }, _host.Confirmed[0].Lines);
        Assert.Equal("Import this item?", _host.Confirmed[1].Title);
        Assert.StartsWith("Imported 1 item", confirmed.Text);
        Assert.IsType<MacroDefinition>(Assert.Single(_host.Saved).Item);
    }

    [Fact]
    public async Task The_Import_window_opens_with_the_file_and_answers_what_the_user_imported()
    {
        string text = FileWith(Craft());

        AiToolResult imported = await Call("open_import_window", new { content = text });
        _host.ImportAnswer = AiAnswer.Cancelled;
        AiToolResult cancelled = await Call("open_import_window");

        Assert.Equal(new[] { text, null }, _host.ImportShown);
        Assert.StartsWith("The user imported 1 item, all disabled:\n- Macro \"Craft\", Id ", imported.Text);
        Assert.Equal("The user cancelled: nothing changed.", cancelled.Text);
    }

    [Fact]
    public async Task The_Export_window_opens_with_the_items_checked_and_answers_the_file_name()
    {
        LoopDefinition farm = Farm();
        _settings.Loops.Add(farm);

        AiToolResult exported = await Call("open_export_window", new { ids = new[] { farm.Id.ToString() } });
        AiToolResult unknown = await Call("open_export_window", new { ids = new[] { Guid.NewGuid().ToString() } });

        Assert.Equal(farm.Id, Assert.Single(Assert.Single(_host.ExportShown)));
        Assert.Equal("The user exported 1 item to Farm.puppyitems.", exported.Text);
        Assert.True(unknown.IsError);
    }

    // ================= Arguments: a wrong one is an error naming it =================

    /// <summary>
    /// An argument of the wrong type, an unknown value of a list of values, a number out of its range or an argument that
    /// goes with another one left out: an input error (isError) that names the argument, never read as left out (ID: a new
    /// Id, for the tools that read the arguments before looking for the item).
    /// </summary>
    [Theory]
    [InlineData("get_item", """{"id":"ID","path":5}""", "\"path\"")]
    [InlineData("get_item", """{"id":"ID","outline":true,"path":"/Actions/0"}""", "\"path\"")]
    [InlineData("get_item", """{"id":"ID","outline_depth":2}""", "\"outline_depth\"")]
    [InlineData("get_item", """{"id":"ID","outline":true,"outline_depth":30}""", "\"outline_depth\"")]
    [InlineData("get_item", """{"id":5}""", "\"id\"")]
    [InlineData("get_overview", """{"limit":0}""", "\"limit\"")]
    [InlineData("get_overview", """{"offset":-1}""", "\"offset\"")]
    [InlineData("get_overview", """{"words":["diablo"]}""", "\"words\"")]
    [InlineData("get_schema", """{"kind":"button"}""", "\"kind\"")]
    [InlineData("get_schema", """{"kind":1}""", "\"kind\"")]
    [InlineData("get_help", """{"topic":2}""", "\"topic\"")]
    [InlineData("check_hotkey", """{"hotkey":{"Vk":112},"for":"macro"}""", "\"for\"")]
    [InlineData("check_hotkey", """{"hotkey":{"Vk":112},"mode":"Press"}""", "\"mode\"")]
    [InlineData("check_hotkey", """{"hotkey":{"Vk":112},"app":5}""", "\"app\"")]
    [InlineData("check_hotkey", """{"hotkey":"F1"}""", "\"hotkey\"")]
    [InlineData("search_library", """{"domain":"games"}""", "\"domain\"")]
    [InlineData("search_library", """{"purpose":"click"}""", "\"purpose\"")]
    [InlineData("search_library", """{"app":3}""", "\"app\"")]
    [InlineData("search_library", """{"collection":7}""", "\"collection\"")]
    [InlineData("search_library", """{"limit":500}""", "\"limit\"")]
    [InlineData("update_actions", """{"id":"ID","set":{"DelayMs":5},"group":3}""", "\"group\"")]
    [InlineData("update_settings", """{"settings":{"Theme":true}}""", "\"settings\"")]
    [InlineData("import_items", """{"content":"{}","only":["a"]}""", "\"only\"")]
    [InlineData("pick_points", """{"title":"T","description":"D","steps":["S"],"confirmed_steps":["C"],"count":"2","labels":["A","B"]}""", "\"count\"")]
    [InlineData("pick_points", """{"title":"T","description":"D","steps":["S"],"confirmed_steps":["C"],"count":1,"labels":"A"}""", "\"labels\"")]
    [InlineData("pick_points", """{"title":"T","description":"D","steps":["S"],"confirmed_steps":["C"],"count":1,"labels":[1]}""", "\"labels\"")]
    [InlineData("pick_points", """{"title":"T","description":"D","steps":["S"],"confirmed_steps":"C","count":1,"labels":["A"]}""", "\"confirmed_steps\"")]
    [InlineData("pick_app", """{"title":"T","description":"D","steps":"Click it"}""", "\"steps\"")]
    [InlineData("pick_app", """{"title":"T","description":"D","steps":["Click it",2]}""", "\"steps\"")]
    [InlineData("pick_app", """{"title":5,"description":"D","steps":["S"]}""", "\"title\"")]
    [InlineData("pick_pixel", """{"title":"T","description":"D","steps":["S"],"x":5,"y":6}""", "\"x\"")]
    [InlineData("pick_pixel", """{"snapshot_id":5,"x":5,"y":6}""", "\"snapshot_id\"")]
    [InlineData("take_image", """{"title":"T","description":"D","steps":["S"],"area":{"X":0,"Y":0,"Width":9,"Height":9}}""", "\"area\"")]
    [InlineData("test_macro", """{"id":"ID","title":"T","description":"D","steps":["S"]}""", "\"confirmed_steps\"")]
    [InlineData("test_macro", """{"id":"ID","title":"T","description":"D","steps":["S"],"confirmed_steps":["C"],"max_seconds":900}""", "\"max_seconds\"")]
    [InlineData("record_macro", """{"title":"T","description":"D","steps":["S"],"mouse_movement":"yes"}""", "\"mouse_movement\"")]
    [InlineData("wait_for_user", """{"request_id":5}""", "\"request_id\"")]
    [InlineData("wait_for_user", """{}""", "\"request_id\"")]
    [InlineData("save_component", """{"component":{"Name":"X"},"renamed":"Slot"}""", "\"renamed\"")]
    [InlineData("save_component", """{"component":{"Name":"X"},"renamed":{"Old":5}}""", "\"renamed.Old\"")]
    [InlineData("save_component", """{"component":{"Name":"X"},"call_values":3}""", "\"call_values\"")]
    [InlineData("save_component", """{"component":{"Name":"X"},"collection":"Diablo"}""", "\"collection\"")]
    [InlineData("save_component", """{"component":{"Name":"X"},"collection":{"name":"Diablo","app":5}}""", "\"collection.app\"")]
    [InlineData("open_item", """{"id":"ID","title":"T","description":["D"]}""", "\"description\"")]
    public async Task A_wrong_argument_is_an_error_that_names_it(string tool, string args, string named)
    {
        AiToolResult result = await _tools.Call(AiTools.Name(tool),
            JsonDocument.Parse(args.Replace("ID", Guid.NewGuid().ToString(), StringComparison.Ordinal)).RootElement,
            App, TestContext.Current.CancellationToken);

        Assert.True(result.IsError, result.Text);
        Assert.Contains(named, result.Text);
        Assert.Empty(_host.ReadyShown);
        Assert.Empty(_host.Saved);
    }

    [Fact]
    public async Task An_argument_left_out_or_null_keeps_meaning_not_given_and_values_are_read_in_any_case()
    {
        AiToolResult nulls = await Call("check_hotkey", new { hotkey = new { Vk = 0x87 }, @for = (string?)null, mode = (string?)null, app = (string?)null });
        AiToolResult hold = await Call("check_hotkey", new { hotkey = new { Vk = 0x87 }, @for = "HOTKEY", mode = "hold" });
        AiToolResult search = await Call("search_library", new { domain = "Game", purpose = (string?)null, collection = "" });

        Assert.False(nulls.IsError, nulls.Text);
        Assert.False(hold.IsError, hold.Text);
        Assert.False(search.IsError, search.Text);
    }

    [Fact]
    public async Task The_name_of_a_debug_folder_never_fails_on_a_wrong_id()
    {
        Assert.Null(await _tools.SubjectOf(JsonSerializer.SerializeToElement(new { id = 5 })));
        Assert.Null(await _tools.SubjectOf(JsonSerializer.SerializeToElement(new { id = "abc" })));
    }

    [Fact]
    public async Task A_picture_s_area_that_is_not_an_object_is_an_error_naming_it()
    {
        AiToolResult look = await Call("look_at_screen", new { title = "T", description = "D", steps = new[] { "S" } });
        string uri = DebugStore.UriOf(Assert.Single(look.Files, f => f.EndsWith(".png", StringComparison.Ordinal)));

        AiToolResult read = await Call("read_resource", new { uri, area = "all" });

        Assert.True(read.IsError, read.Text);
        Assert.Contains("\"area\" must be an object", read.Text);
    }

    [Fact]
    public void The_first_answer_gives_the_global_rules_and_what_the_Library_holds()
    {
        // An empty Library: the rules, and that it has no components yet.
        string empty = _tools.FirstAnswer();
        Assert.StartsWith("PuppyMacro's rules for AI apps:\n- ", empty);
        Assert.All(AiTools.GlobalRules, rule => Assert.Contains("- " + rule, empty));
        Assert.Contains("never duplicate what already exists", empty);
        Assert.EndsWith("The user's Library has no components yet.", empty);

        static LibraryComponent Point(string concept) => new()
        {
            Name = concept, Purpose = ComponentPurpose.Point, Concept = concept,
            Actions = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 5, Y = 6 } },
        };
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Diablo IV", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe", Components = { Point("slot"), Point("cube") },
        });
        _host.Macros.Library.Collections.Add(new LibraryCollection { Name = "General", Domain = LibraryDomain.Other, Components = { Point("any") } });
        _host.Macros.Library.Collections.Add(new LibraryCollection { Name = "Empty", Domain = LibraryDomain.Other });

        string full = _tools.FirstAnswer();
        Assert.Contains("The user's Library has components that already work: \"Diablo IV\" (Diablo IV.exe) 2, \"General\" (any app) 1. " +
            "puppymacro_search_library lists them with what each takes.", full);
        Assert.DoesNotContain("\"Empty\"", full); // a collection without components says nothing
    }

    [Fact]
    public void The_instructions_give_the_global_rules_too()
    {
        Assert.Contains("PuppyMacro's rules for AI apps:", AiTools.InstructionsText);
        Assert.All(AiTools.GlobalRules, rule => Assert.Contains("- " + rule, AiTools.InstructionsText));
    }

    [Fact]
    public async Task A_changed_component_never_moves_to_another_collection()
    {
        var component = new LibraryComponent
        {
            Name = "Slot", Purpose = ComponentPurpose.Point, Concept = "slot",
            Actions = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 5, Y = 6 } },
        };
        var collection = new LibraryCollection { Name = "Diablo", Domain = LibraryDomain.Game, Components = { component } };
        _host.Macros.Library.Collections.Add(collection);
        object item = new { Name = "Slot", Purpose = "Point", Concept = "slot", Actions = new[] { new { Type = "Click", Vk = 1, X = 7, Y = 8 } } };

        AiToolResult moved = await Call("save_component", new { id = component.Id.ToString(), collection = new { name = "Other" }, component = item });
        AiToolResult kept = await Call("save_component", new { id = component.Id.ToString(), collection = new { id = collection.Id.ToString() }, component = item });

        Assert.True(moved.IsError);
        Assert.Contains("never moves to another collection", moved.Text);
        Assert.False(kept.IsError, kept.Text);
        Assert.Equal(7, Assert.Single(collection.Components).Actions[0].X);
    }

    // ================= get_item's outline =================

    [Fact]
    public async Task The_outline_starts_with_the_macro_s_settings_that_differ_from_their_defaults()
    {
        var plain = new MacroDefinition { Name = "Plain", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } } };
        var tuned = new MacroDefinition
        {
            Name = "Salvage", Hotkey = new HotkeyBinding { Vk = 0x74 }, Mode = ActivationMode.Hold, Repeat = RepeatMode.Loop,
            ChecksPerSecond = 60, Debug = true, AppExe = "Diablo IV.exe",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } },
        };
        _host.Macros.Macros.Add(plain);
        _host.Macros.Macros.Add(tuned);

        string[] plainLines = (await Call("get_item", new { id = plain.Id.ToString(), outline = true })).Text.Split('\n');
        string[] tunedLines = (await Call("get_item", new { id = tuned.Id.ToString(), outline = true })).Text.Split('\n');

        Assert.Equal("Settings: all at their defaults", plainLines[0]);
        Assert.StartsWith("/Actions/0 PressKey", plainLines[1]);
        Assert.StartsWith("Settings: ", tunedLines[0]);
        foreach (string setting in new[] { "Hotkey F5", "Mode Hold", "Repeat Loop", "ChecksPerSecond 60", "Debug on", "AppExe Diablo IV.exe" })
            Assert.Contains(setting, tunedLines[0]);
        Assert.DoesNotContain("Salvage", tunedLines[0]);
        Assert.DoesNotContain("Actions", tunedLines[0]);
        Assert.StartsWith("/Actions/0 PressKey", tunedLines[1]);
    }

    [Fact]
    public void The_settings_line_names_a_loop_s_and_a_remap_s_settings_too()
    {
        var loop = new LoopDefinition { Name = "Spam", Enabled = false, Hotkey = new HotkeyBinding { Vk = 0x74 } };
        var remap = new RemapDefinition { Source = CtrlX.Clone(), Target = new HotkeyBinding { Vk = 0x41 } };

        string loopLine = AiTools.SettingsLine(loop);
        string remapLine = AiTools.SettingsLine(remap);

        Assert.Contains("Enabled off", loopLine);
        Assert.Contains("Hotkey F5", loopLine);
        Assert.DoesNotContain("Spam", loopLine);
        Assert.Contains("Source ", remapLine);
        Assert.Contains("Target A", remapLine);
    }

    // ================= Instructions =================

    [Fact]
    public void The_instructions_and_save_component_name_every_type_a_parameter_can_have_and_a_grid_s_cell()
    {
        string saveComponent = AiTools.Definitions().Single(t => t.Name == AiTools.Name("save_component")).Description;
        foreach (string text in new[] { AiTools.Instructions, saveComponent })
        {
            foreach (var type in Enum.GetValues<ParameterType>())
                Assert.Contains(type.ToString(), text);
            Assert.Contains("Cell", text);
        }
    }

    [Fact]
    public void The_instructions_and_save_component_say_a_shared_value_can_be_one_parameter_passed_down()
    {
        string saveComponent = AiTools.Definitions().Single(t => t.Name == AiTools.Name("save_component")).Description;
        foreach (string text in new[] { AiTools.Instructions, saveComponent })
        {
            Assert.Contains("passed down with {\"From\"}", text);
            Assert.Contains("a Default copied into each component has to be changed in each", text);
        }
    }
}
