using System;
using System.Collections.Generic;
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
/// FakeAiHost, except that the Library refuses what MainWindow refuses, with its texts in the language of the moment
/// (MainWindow.SaveComponent: a collection without a name is ComponentLibrary.CheckCollection's Strings.Library_NameMissing).
/// </summary>
internal sealed class LibraryCheckingHost(FakeAiHost inner) : IAiHost
{
    public AppSettings Settings => inner.Settings;
    public MacroLibrary Macros => inner.Macros;
    public bool IsRunning(Guid id) => inner.IsRunning(id);
    public bool AnyRunning => inner.AnyRunning;
    public bool OverlayModeActive => inner.OverlayModeActive;
    public bool UserIsEditing => inner.UserIsEditing;
    public Task<T> Ui<T>(Func<Task<T>> action) => inner.Ui(action);
    public void Save(object item, string app) => inner.Save(item, app);
    public string? AddCollections(IReadOnlyList<LibraryCollection> collections, string app) => inner.AddCollections(collections, app);

    public string? SaveComponent(LibraryComponent component, LibraryCollection? created, string app,
        IReadOnlyList<MacroDefinition>? macros = null, IReadOnlyList<LibraryComponent>? callers = null) =>
        created != null && Macros.Library.Find(component.Id) == null && ComponentLibrary.CheckCollection(created) is string bad
            ? bad
            : inner.SaveComponent(component, created, app, macros, callers);

    public void Delete(Guid id, string app) => inner.Delete(id, app);
    public void SetEnabled(Guid id, bool enabled, string app) => inner.SetEnabled(id, enabled, app);
    public void ApplySettings(AiSettingsValues values) => inner.ApplySettings(values);
    public Task<AiAnswer> Confirm(string app, string title, IReadOnlyList<string> lines, string primary) => inner.Confirm(app, title, lines, primary);
    public Task<AiAnswer> Ready(string app, AiAsk ask) => inner.Ready(app, ask);
    public Task<(AiAnswer Answer, List<(int X, int Y)> Points)> PickPoints(string app, string message, IReadOnlyList<string> labels) => inner.PickPoints(app, message, labels);
    public Task<(AiAnswer Answer, string? Exe)> PickApp(string app, string message) => inner.PickApp(app, message);
    public Task<(AiAnswer Answer, AiScreenPick? Pick)> PickOnScreen(string app, string message, AiScreenPickKind kind) => inner.PickOnScreen(app, message, kind);
    public Task<(AiAnswer Answer, HotkeyBinding? Keys)> CaptureKeys(string app, AiAsk ask) => inner.CaptureKeys(app, ask);
    public Task<AiAnswer> PlaceFloatingButton(Guid id, string app) => inner.PlaceFloatingButton(id, app);
    public Task<AiAnswer> OpenItem(Guid id, string app, string title, string description) => inner.OpenItem(id, app, title, description);
    public Task<(AiAnswer Answer, List<object> Items)> ShowImport(string app, string? text) => inner.ShowImport(app, text);
    public Task<(AiAnswer Answer, List<object> Items, string? File)> ShowExport(string app, IReadOnlyCollection<Guid> ids) => inner.ShowExport(app, ids);
    public void StopAll() => inner.StopAll();
    public IReadOnlyList<string> RunningApps() => inner.RunningApps();
    public Task<PuppyMacro.Services.Screen.ConditionReport> CheckCondition(MacroCondition condition) => inner.CheckCondition(condition);
    public Task<List<(OpenCvSharp.Rect Bounds, OpenCvSharp.Mat Picture)>> TakeScreen() => inner.TakeScreen();
    public Task<(AiAnswer Answer, AiRecording? Recording)> Record(string app, bool mouseMovement) => inner.Record(app, mouseMovement);
    public Task<AiTestRun?> TestMacro(Guid id, string app, double maxSeconds, string folder) => inner.TestMacro(id, app, maxSeconds, folder);
    public void Finished(string app) => inner.Finished(app);
}

/// <summary>
/// The AI tools against what an AI app may send: wrong types, odd ids, odd resource URIs, and the permissions after a
/// picture was taken. Each test fails on a bug of the tools (FakeAiHost as the app and the user).
/// </summary>
// DebugStore.Root is one folder for the whole process: these classes set it, one at a time.
[Collection(nameof(DebugStore))]
public class AiToolsInputTests : IDisposable
{
    private const string App = "Claude (claude-code)";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiTools _tools;

    private static readonly object Asking = new
    {
        title = "Show the game",
        description = "We are making a macro that waits for the bomb.",
        steps = new[] { "Open the game", "Press Enter" },
    };

    public AiToolsInputTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.AiAskBeforeChanging = false;
        _settings.Loops.Clear();
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("macros")));
        _tools = new AiTools(_host, new AiActivityLog(_folder.File("ai-activity.json")));
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null) => Call(_tools, tool, args);

    private static Task<AiToolResult> Call(AiTools tools, string tool, object? args) =>
        tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static string SnapshotId(AiToolResult result) => Regex.Match(result.Text, "\"SnapshotId\": \"([^\"]+)\"").Groups[1].Value;

    private LoopDefinition AddLoop(string name, bool enabled = true)
    {
        var loop = new LoopDefinition { Name = name, Enabled = enabled };
        _settings.Loops.Add(loop);
        return loop;
    }

    // ================= Permissions =================

    [Fact]
    public async Task A_look_at_screen_picture_is_not_read_again_once_Let_AI_apps_see_the_screen_is_off()
    {
        AiToolResult look = await Call("look_at_screen", Asking);
        string snapshot = SnapshotId(look);
        Assert.False(string.IsNullOrEmpty(snapshot), look.Text);

        // The user turns the permission off: read_resource refuses the same picture...
        _settings.AiSeeScreen = false;
        string png = Assert.Single(look.Files, f => f.EndsWith(".png", StringComparison.Ordinal));
        Assert.True((await Call("read_resource", new { uri = DebugStore.UriOf(png) })).IsError);

        // ...and so must a read of its pixels through snapshot_id (AiTools.Screen.cs FromSnapshotPick has no AiSeeScreen check).
        AiToolResult pixel = await Call("pick_pixel", new { snapshot_id = snapshot, x = 5, y = 5 });
        Assert.True(pixel.IsError, pixel.Text);
        Assert.Contains("Let AI apps see the screen", pixel.Text);
        Assert.DoesNotContain("#FFFFFF", pixel.Text);

        AiToolResult image = await Call("take_image", new { snapshot_id = snapshot, area = new { X = 100, Y = 100, Width = 40, Height = 40 } });
        Assert.True(image.IsError, image.Text);
    }

    // ================= Resource URIs =================

    [Theory]
    [InlineData("puppymacro://debug/ai%00/x.txt")]
    [InlineData("puppymacro://debug/a\u0000b")]
    public async Task A_resource_uri_with_a_null_character_is_a_missing_file_not_a_crash(string uri)
    {
        // DebugStore.PathOf: Uri.UnescapeDataString gives "\0", Path.GetFullPath throws ArgumentException, which
        // AiTools.Call does not catch (only Json, InvalidOperation, Format and KeyNotFound exceptions).
        AiTools.OpenedResource opened = _tools.OpenResource(uri);
        Assert.True(opened.Missing);

        AiToolResult read = await Call("read_resource", new { uri });
        Assert.True(read.IsError);
        Assert.Contains("No file", read.Text);
    }

    [Fact]
    public async Task A_snapshot_id_with_a_null_character_is_an_unknown_picture_not_a_crash()
    {
        AiToolResult pixel = await Call("pick_pixel", new { snapshot_id = "ai%00x", x = 1, y = 1 });

        Assert.True(pixel.IsError);
        Assert.Contains("look_at_screen", pixel.Text);
    }

    [Fact]
    public async Task A_picture_named_by_its_data_stream_is_still_a_picture_for_Let_AI_apps_see_the_screen()
    {
        AiToolResult look = await Call("look_at_screen", Asking);
        string uri = DebugStore.UriOf(Assert.Single(look.Files, f => f.EndsWith(".png", StringComparison.Ordinal)));
        _settings.AiSeeScreen = false;

        // NTFS: "monitor-1.png::$DATA" is the same file. DebugStore.PathOf finds it, DebugStore.MimeOf reads its extension
        // as ".png::$data", so it is text/plain and AiTools.OpenResource lets it through with the permission off.
        AiTools.OpenedResource opened = _tools.OpenResource(uri + "::$DATA");
        Assert.True(opened.File == null || opened.File.IsImage, opened.File?.MimeType);

        AiToolResult read = await Call("read_resource", new { uri = uri + "::$DATA" });
        Assert.True(read.IsError, read.Text.Length > 200 ? read.Text[..200] : read.Text);
    }

    /// <summary>Set in the process <see cref="A_picture_s_area_past_the_largest_number_is_refused_in_its_own_process"/> starts.</summary>
    private const string OverflowProcess = "PUPPYMACRO_TESTS_OVERFLOW_AREA";

    [Fact]
    public async Task A_picture_s_area_past_the_largest_number_is_refused_in_its_own_process()
    {
        // AiTools.Resources.cs ReadPicture: part.Right (X + Width) overflows int and passes the "inside the picture" check,
        // and OpenCV's own check in new Mat(picture, part) overflows the same way, so the view reaches far past the picture.
        // Reading it (CopyTo at scale 1, Resize at a smaller one) touches memory that is not the picture's: an access
        // violation (0xC0000005) that ends the whole process, or memory written over that crashes it later. So the call
        // runs in a process of its own (this test assembly, only the test below), and its exit code says how it went.
        string exe = Environment.ProcessPath!;
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AiToolsInputTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(AiToolsInputTests).FullName}.{nameof(Area_past_the_largest_number)}");
        start.Environment[OverflowProcess] = "1";

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "the process did not end");
        string said = await errors + "\n" + await output;

        Assert.True(process.ExitCode == 0, $"exit code 0x{process.ExitCode:X8}\n{said}");
    }

    /// <summary>The call itself, run only in the process the test above starts (otherwise it passes at once).</summary>
    [Fact]
    public async Task Area_past_the_largest_number()
    {
        if (Environment.GetEnvironmentVariable(OverflowProcess) != "1")
            return;
        AiToolResult look = await Call("look_at_screen", Asking);
        string uri = DebugStore.UriOf(Assert.Single(look.Files, f => f.EndsWith(".png", StringComparison.Ordinal)));

        AiToolResult read = await Call("read_resource", new { uri, area = new { X = 1, Y = 0, Width = int.MaxValue, Height = 10 } });

        Assert.True(read.IsError, read.Text);
        Assert.Contains("\"area\" must be inside the picture", read.Text);
    }

    [Fact]
    public async Task Read_resource_gives_the_lines_to_the_end_with_the_largest_limit()
    {
        AiToolResult recorded = await Call("record_macro", Asking);
        string log = recorded.Files[0];
        int lines = File.ReadAllLines(log).Length;

        // AiTools.Resources.cs ReadText: first - 1 + limit overflows int, so the range is negative and throws.
        AiToolResult read = await Call("read_resource", new { uri = DebugStore.UriOf(log), offset = 2, limit = int.MaxValue });

        Assert.False(read.IsError, read.Text);
        Assert.StartsWith($"Lines 2–{lines} of {lines}", read.Text);
    }

    // ================= Wrong types and ids =================

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task Set_enabled_never_turns_an_item_off_when_enabled_is_not_false(string? enabled)
    {
        LoopDefinition loop = AddLoop("Spam");
        string args = enabled == null
            ? $"{{\"id\":\"{loop.Id}\"}}"
            : $"{{\"id\":\"{loop.Id}\",\"enabled\":{enabled}}}";

        // AiTools.cs SetEnabled: anything but the JSON true (a string, a number, nothing) reads as false.
        AiToolResult result = await _tools.Call(AiTools.Name("set_enabled"), JsonDocument.Parse(args).RootElement, App,
            TestContext.Current.CancellationToken);

        Assert.True(loop.Enabled, result.Text);
        Assert.True(result.IsError, result.Text);
        Assert.Contains("enabled", result.Text);
    }

    [Fact]
    public async Task Import_items_with_only_not_a_list_does_not_import_every_item()
    {
        var first = new MacroDefinition { Name = "First", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        var second = new MacroDefinition { Name = "Second", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x42 } } };
        string file = ItemFile.Write(new object[] { first, second }, ItemFile.AppVersion, _host.Macros.Images, _host.Macros.Library);

        // AiTools.cs ImportItems: "only" is read only when it is an array; otherwise it is dropped and everything comes in.
        AiToolResult result = await Call("import_items", new { content = file, only = 1 });

        Assert.True(result.IsError, result.Text);
        Assert.Contains("only", result.Text);
        Assert.Empty(_host.Saved);
    }

    [Theory]
    [InlineData("save_macro", "{\"id\":\"abc\",\"item\":{\"Name\":\"X\"}}", "\"id\"")]
    [InlineData("save_loop", "{\"id\":\"abc\",\"item\":{\"Name\":\"X\"}}", "\"id\"")]
    [InlineData("save_component", "{\"id\":\"abc\",\"component\":{\"Name\":\"X\"}}", "\"id\"")]
    [InlineData("check_hotkey", "{\"hotkey\":{\"Vk\":112},\"ignore_id\":\"abc\"}", "ignore_id")]
    [InlineData("export_items", "{\"ids\":[123]}", "\"ids\"")]
    public async Task A_wrong_id_says_which_argument_to_change(string tool, string args, string named)
    {
        // Guid.Parse and JsonElement.GetString throw .NET's own text ("Unrecognized Guid format.", "The requested
        // operation requires an element of type 'String'…"), which names no argument.
        AiToolResult result = await _tools.Call(AiTools.Name(tool), JsonDocument.Parse(args).RootElement, App,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains(named, result.Text);
    }

    [Fact]
    public async Task Export_items_names_an_unknown_id_also_when_it_is_all_zeros()
    {
        LoopDefinition loop = AddLoop("Spam");

        // AiTools.cs ItemsByIds: FirstOrDefault gives Guid.Empty both for "none missing" and for a missing Guid.Empty.
        AiToolResult result = await Call("export_items", new { ids = new[] { loop.Id.ToString(), Guid.Empty.ToString() } });

        Assert.True(result.IsError, result.Text);
        Assert.Contains(Guid.Empty.ToString(), result.Text);
    }

    // ================= Library =================

    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    public async Task A_new_collection_s_domain_is_one_of_the_domains(string domain)
    {
        var component = new { Name = "Remove one affix", Purpose = "Act", Concept = "remove_one_affix", Actions = new object[] { new { Type = "PressKey", Vk = 65 } } };

        // AiTools.Library.cs CollectionArg: Enum.TryParse accepts any number, so a collection gets a domain that does not exist.
        AiToolResult result = await Call("save_component", new { collection = new { name = "Diablo", domain }, component });

        Assert.All(_host.Macros.Library.Collections, c => Assert.True(Enum.IsDefined(c.Domain), $"{c.Domain}: {result.Text}"));
    }

    [Fact]
    public async Task A_collection_without_a_name_is_refused_before_the_user_is_asked()
    {
        _settings.AiAskBeforeChanging = true;
        var tools = new AiTools(new LibraryCheckingHost(_host), new AiActivityLog(_folder.File("ai-activity-2.json")));
        var component = new { Name = "Remove one affix", Purpose = "Act", Concept = "remove_one_affix", Actions = new object[] { new { Type = "PressKey", Vk = 65 } } };

        // AiTools.Library.cs CollectionArg takes "   " as a name; only the app's save refuses it, after the user said yes.
        AiToolResult result = await Call(tools, "save_component", new { collection = new { name = "   ", domain = "game" }, component });

        Assert.True(result.IsError, result.Text);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task The_app_s_refusal_of_a_component_reaches_the_AI_app_in_English()
    {
        var tools = new AiTools(new LibraryCheckingHost(_host), new AiActivityLog(_folder.File("ai-activity-2.json")));
        var component = new { Name = "Remove one affix", Purpose = "Act", Concept = "remove_one_affix", Actions = new object[] { new { Type = "PressKey", Vk = 65 } } };
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it");
        AiToolResult result;
        try
        {
            result = await Call(tools, "save_component", new { collection = new { name = "   ", domain = "game" }, component });
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }

        // AiTools.Library.cs Apply: Loc.English(() => notSaved) wraps a text the app already wrote in the user's language.
        Assert.True(result.IsError, result.Text);
        Assert.DoesNotContain("Dagli un nome", result.Text);
        Assert.Contains("Give it a name", result.Text);
    }

    // ================= get_item in pieces =================

    [Fact]
    public async Task Get_item_s_path_reads_a_field_get_item_left_out_with_its_default()
    {
        var macro = new MacroDefinition { Name = "Keys", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } } };
        _host.Macros.Macros.Add(macro);

        // patch_item's schema: "a field get_item left out exists with its default, so replace works on it"; reading it
        // with path says nothing is there (AiTools.Library.cs AtPath reads the compact JSON).
        AiToolResult patched = await Call("patch_item", new { id = macro.Id.ToString(), patch = new object[] { new { op = "test", path = "/Actions/0/DelayMs", value = 0 } } });
        Assert.DoesNotContain("there is no field", patched.Text);
        AiToolResult part = await Call("get_item", new { id = macro.Id.ToString(), path = "/Actions/0/DelayMs" });

        Assert.False(part.IsError, part.Text);
        Assert.Equal("0", part.Text);
    }

    // ================= Annotations =================

    [Fact]
    public void A_tool_that_can_remove_actions_or_rewrite_an_item_is_not_announced_as_only_additive()
    {
        // MCP 2025-11-25, ToolAnnotations.destructiveHint: "If false, the tool performs only additive updates."
        // patch_item has "remove" and "replace", save_macro with an id rewrites the macro, like delete_item they are restorable.
        foreach (string name in new[] { "patch_item", "save_macro", "update_actions" })
            Assert.True(_tools.All.Single(t => t.Name == AiTools.Name(name)).Destructive, name);
    }
}
