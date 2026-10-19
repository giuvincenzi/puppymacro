using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Ai;

/// <summary>A tool's answer to the AI app: text (often JSON), or an error the AI should tell the user about.</summary>
internal sealed record AiToolResult(string Text, bool IsError = false)
{
    public static AiToolResult Error(string text) => new(text, true);

    /// <summary>
    /// Files of the call's debug folder the answer points to (a log, pictures of the screen): the server gives them as MCP
    /// resource links, never their content, so the AI app reads only what it needs.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Pictures given in the answer itself (only read_resource, when the AI app asks for one).</summary>
    public IReadOnlyList<AiImage> Images { get; init; } = Array.Empty<AiImage>();

    /// <summary>
    /// Texts given after the answer, each in a text block of its own, so the answer's own text (often JSON) stays whole (the
    /// first answer's <see cref="AiTools.FirstAnswer"/>).
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>A picture in an answer: its encoded bytes and type.</summary>
internal sealed record AiImage(byte[] Data, string MimeType);

/// <summary>
/// One MCP tool: its name, what it does (for the AI), its input JSON Schema, what runs it, and how it behaves (MCP tool
/// annotations: it only reads, it may change or destroy something, calling it twice is the same as once).
/// </summary>
internal sealed record AiTool(string Name, string Description, string InputSchema, bool ReadOnly, bool Destructive,
    Func<JsonElement, string, CancellationToken, Task<AiToolResult>> Run)
{
    /// <summary>A short name people read in the AI app's interface.</summary>
    public string Title { get; init; } = "";

    public bool Idempotent { get; init; }
}

/// <summary>
/// The tools PuppyMacro gives AI apps (MCP), without the protocol (<see cref="AiServer"/> adds it). Reads answer at once.
/// Changes are saved at once, as the user's Save would, and kept in the item's History; they wait for the user's OK only
/// where the AI page's Ask before … says so. Interactions show what the AI asks and why, and start when the user is ready.
/// A request that waits for the user answers "waiting" after <see cref="WaitStep"/> (AI apps cut long calls) and the AI
/// calls wait_for_user. One such request at a time. Everything is refused while Allow AI access is off.
/// </summary>
internal sealed partial class AiTools
{
    /// <summary>How long a window opened for the AI stays open while nobody uses PuppyMacro.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long one call waits for the user before answering "waiting" (AI apps cut calls after a minute or more).</summary>
    public static TimeSpan WaitStep { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>The most points pick_points asks for at once.</summary>
    public const int MaxPoints = 20;

    // The texts for the AI app describe how PuppyMacro works and what each choice brings, not orders: the model decides.
    // The limits that matter are in the code (the ready window, confirmed_steps, the permissions, the checks of a save).
    public const string Instructions =
        "PuppyMacro automates keyboard and mouse input on Windows: loops (keys pressed or clicks at intervals), macros " +
        "(recorded or built step by step, and able to look at the screen) and remaps (a key or combination sends another " +
        "one). These tools read and change the user's loops, macros, remaps and settings, and work with the user's screen.\n\n" +
        "How requests work:\n" +
        "- Reads (get_overview, get_item, get_schema, get_settings, check_hotkey, list_apps, get_help, " +
        "export_items, get_last_run, read_resource, search_library, get_component) answer at once.\n" +
        "- save_loop, save_macro and save_remap create or rewrite an item; patch_item and update_actions change only what " +
        "they name, which is much faster for a big item, since every character of a call is written by you. delete_item, " +
        "set_enabled and update_settings do what they say. Everything is saved at once and kept in the item's History, " +
        "where the user can restore any version. save_component, patch_component and delete_component do the same for the " +
        "Library's components. Some changes wait for the user's OK, as the user chose on PuppyMacro's AI page; the answer " +
        "says whether they confirmed. When a change leaves a component that something called before called by nothing, " +
        "its answer names it.\n" +
        "- Some tools need the user: pick_points, pick_app, capture_hotkey, pick_pixel, take_image, draw_area, " +
        "look_at_screen, check_condition, record_macro, test_macro and place_floating_button. PuppyMacro first shows a " +
        "window in the middle of the screen, on top of everything, with your title, a short description of what you are " +
        "making together and why, and the steps, numbered; it waits for Start or Enter, so the user can first open the right " +
        "screen. The user reads these texts, so they are best in the language the user writes in. Points, app names, keys, " +
        "colors and images that come from these tools match the user's PC, which guesses can't. A point's label is shown " +
        "next to the cursor while the user picks it.\n" +
        "- A request that waits for the user answers within about 40 seconds. If the user has not answered yet, the answer " +
        "has a request_id, and wait_for_user waits again. One such request runs at a time.\n\n" +
        "Macros that look at the screen:\n" +
        "- An action of Type If checks its conditions: a pixel's color, an image or a text on the screen, or with Not that " +
        "it is not there (to wait until something goes away); all of them or any one; once, up to some seconds, or as long " +
        "as it takes. Then it plays its Then actions; otherwise its Else actions, or Stop the macro, which ends the whole " +
        "macro: the actions after the If and the next rounds do not play. An Else with no actions lets the macro go on to " +
        "the next action.\n" +
        "- The kinds trade speed for sturdiness. A pixel is the fastest and lightest, but breaks when the color shifts " +
        "(light, effects, a hover). An image is the most reliable when it is small and distinctive (a button, an icon), " +
        "and a flat area of one color can't be found. A text is the slowest, and only in the languages Windows can read " +
        "(get_schema names them). Similarity counts in whole percent, 100 = identical; around 90 leaves room for small " +
        "changes. The macro's ChecksPerSecond sets how often conditions look while they wait.\n" +
        "- A Click, Mouse button down / up or Move to inside Then can go where an Image or Text condition without Not found " +
        "its match (FoundId, plus FoundOffsetX/Y), wherever it is that time.\n" +
        "- look_at_screen shows the screen; take_image and pick_pixel can then read that same picture without asking the " +
        "user. check_condition shows what a condition sees now, as the editor does: on the right screen and on a wrong one it " +
        "shows whether the condition tells them apart. record_macro gives what the user did, with an image around each " +
        "click. test_macro plays a saved macro after the user's Start. What these capture (a log, the screen's pictures) stays " +
        "in PuppyMacro's debug data, and the answer links each file (MCP resource links) and lists it with what it holds, its size and its weight (also " +
        "its path): a file is read only when you open it, so the conversation keeps only what you need. A link opens with " +
        "resources/read, or, in an AI app that cannot open resource links, with read_resource, which can also give a part or " +
        "a smaller copy of a picture and some lines of a log. get_last_run links a macro's last run again. The user guide (get_help, topic \"conditions\") has examples of common macros. " +
        "A picture takes much more of your context than text.\n\n" +
        "The Library: components that already work in the user's apps (a point, an If that finds something, a sequence), " +
        "kept by earlier work, the user's or an AI app's, and linked by macros instead of made again: a linked row plays the " +
        "component as it is in the Library, also after it changes. " + ComponentsAreFunctions + " " + ReusableComponents + " " +
        "Before building a macro, search_library finds what can be linked or extended; after a new piece works, save_component " +
        "keeps it, with a structured name in English (domain-app-purpose-concept) that later searches find. A component's " +
        "points are the screen it was made on.\n\n" +
        "New loops and macros: the user knows what each key and click must do in their app, and PuppyMacro's pick tools take " +
        "confirmed_steps, the steps the item will play as the user confirmed them in the chat, before anything is picked. " +
        "Questions that the user's words already answer, or that do not apply to the item, only slow them down. Once saved, " +
        "an item can also have a hotkey, Show in overlay and a floating button (Toggle items), a sound, and a specific app.\n\n" +
        "Playing: test_macro plays a saved macro as a test, after the user's Start, so its keys and clicks go to the app the " +
        "user brought in front, not to this chat. Otherwise loops and macros start in PuppyMacro: the Play button on the " +
        "item's card, its hotkey, or overlay mode. A running item that is changed stops. stop_all stops everything.\n\n" +
        "Formats: items are JSON exactly as PuppyMacro saves them (get_schema gives the JSON Schema, get_item real " +
        "examples); PuppyMacro gives the Id of a new item. Keys are Windows virtual-key codes (A = 65, 0 = 48, F1 = 112, " +
        "Enter = 13, Space = 32; mouse: 1 left, 2 right, 4 middle, 5 and 6 side buttons). Points are physical screen " +
        "pixels. get_help reads the user guide.";

    /// <summary>How components work as functions: in the Instructions and in the Library tools' descriptions (AI apps may not pass the Instructions on).</summary>
    private const string ComponentsAreFunctions =
        "A component is a function. Its Parameters are the values a call gives it, each of a type (Point, Number with a Unit, " +
        "Text, Key, Color, Image, Area, Grid), going to fields of its actions through Bind ({\"Type\":\"Click\",…,\"Bind\":{\"Point\":\"Slot\"}}); a bound " +
        "field holds the parameter's Example, which PuppyMacro writes there at save. A Grid is cells all alike in rows, as an " +
        "inventory or a keypad: {\"First\":{\"X\",\"Y\"},\"Last\":{\"X\",\"Y\"},\"Columns\":11,\"Rows\":3}, the same point of the top-left and " +
        "bottom-right cell. A click's, mouse button's, move's or pixel's Point bound to a Grid takes one of its cells, the field's " +
        "Cell (row by row from 1), which a Number Count parameter can give (\"Bind\":{\"Point\":\"Inventory\",\"Cell\":\"Slot\"}): then a " +
        "call gives only the slot's number, and two kinds of points of the same slots (where to read a color, where to click) " +
        "are two Grids used with the same cell. Every call's cells are checked against its grid. A parameter with no Default is given by " +
        "every call. A row calls it: {\"Type\":\"Component\",\"ComponentId\":…,\"Arguments\":{\"Slot\":{\"X\":3986,\"Y\":1056}}}; an argument " +
        "is a value, {\"From\":\"a parameter\"} (inside a component, one of its own passed on) or {\"Found\":\"a condition's Id\"} (a Point: " +
        "where an If around the call found an image or a text this time, with OffsetX and OffsetY; when it found nothing, what goes there is skipped and the rest plays, as rows would). A component with Returns " +
        "(one of its conditions) is also a condition: {\"Kind\":\"Component\",\"Name\":…,\"ComponentId\":…,\"Arguments\":…} in an If is true " +
        "when that condition is, checked as the If checks any condition, and a \"Where … was found\" goes where it found it. " +
        "Components call components (never in a loop, at most 10 levels). A call costs microseconds when the macro plays, where " +
        "a key or a look at the screen takes milliseconds: linking never makes a macro slower.";

    /// <summary>What makes a component reusable: in the Instructions and in save_component's description.</summary>
    private const string ReusableComponents =
        "A component with one concept (one thing it finds, one point, one operation) links into more macros than one that " +
        "does three things. What changes between uses (a slot's position, a key, a wait) is a parameter, so one component " +
        "serves every slot instead of a copy per value; slots in rows and columns the same distance apart are a Grid, so the " +
        "calls give a number instead of 33 points. A component that finds something with an image or a text and returns " +
        "it works wherever the thing is, where fixed coordinates tie it to one place. A value that several nested components " +
        "share, such as a Grid, can be a parameter of the top component, given once by its call and passed down with " +
        "{\"From\"}: it then lives in one place, and a change of screen size edits one value, while a Default copied into " +
        "each component has to be changed in each.";

    /// <summary>In the descriptions of the tools that make a new loop or macro: AI apps may not pass <see cref="Instructions"/> to the model.</summary>
    private const string NewItemRule =
        " For a NEW loop or macro, the user confirms in the chat the exact steps it will play (each key or click and what it " +
        "does there) before points are picked: the pick tools take them as confirmed_steps. Questions the user's words " +
        "already answer, or that do not apply, only slow the user down. A change to an existing item needs no such step.";

    private const string AfterNewItem =
        " Once saved, an item can also have a hotkey, Show in overlay and a floating button (place_floating_button, Toggle " +
        "only), a sound, and a specific app (AppExe). test_macro plays a macro to see how it goes; loops start in PuppyMacro.";

    private const string UserWindow =
        " The user reads the title, description and steps, so they are best in the language the user writes in. If the user " +
        "takes long, the answer is \"waiting\" with a request_id, and wait_for_user waits again.";

    private const string RewriteRule =
        " patch_item and update_actions change an existing item by sending only what changes: rewriting a big macro here " +
        "takes minutes, since you write every character.";

    private const string NoStart =
        "Loops and macros start in PuppyMacro (the Play button on the card, the hotkey, overlay mode); test_macro plays a " +
        "saved macro as a test after the user's Start.";

    private static readonly JsonSerializerOptions ReadOptions = CodeJson.StrictOptions;

    /// <summary>The data files' format, with "Ctrl + X → A" written as it is (the answers are read by the AI, not put in HTML).</summary>
    private static readonly JsonSerializerOptions AnswerOptions = new(CodeJson.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAiHost _host;
    private readonly AiActivityLog _log;
    private readonly object _sync = new();
    private Pending? _pending;
    private readonly Dictionary<string, Pending> _answered = new();

    public AiTools(IAiHost host, AiActivityLog log)
    {
        _host = host;
        _log = log;
        All = Build().Select(t => t with
        {
            Name = Prefix + t.Name,
            Title = Titles[t.Name],
            Description = Prefixed(t.Description),
            InputSchema = Prefixed(t.InputSchema),
            Idempotent = PureReads.Contains(t.Name),
        }).ToList();
    }

    public IReadOnlyList<AiTool> All { get; }

    /// <summary>
    /// Before every tool's name (MCP: tools named by server, as the guides for agents advise, so an AI app with many
    /// servers tells "puppymacro_get_item" from another server's "get_item").
    /// </summary>
    public const string Prefix = "puppymacro_";

    /// <summary>A tool's full name.</summary>
    public static string Name(string bare) => Prefix + bare;

    /// <summary>The <see cref="Instructions"/> and the <see cref="GlobalRules"/>, with the tools' full names.</summary>
    public static string InstructionsText => Prefixed(Instructions + "\n\n" + RulesText);

    /// <summary>
    /// The global rules for AI apps: the one exception to texts that describe (architecture.md), as few as can be, each added
    /// or changed only with the user's approval. Given in the first answer of every conversation (<see cref="FirstAnswer"/>:
    /// the answers are what every AI app reads) and in the <see cref="InstructionsText"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> GlobalRules = new[]
    {
        "When you build or change a loop, a macro or a component, never duplicate what already exists: link or extend what " +
        "the user's Library has (a component, a parameter, a Grid), and make only what is missing.",
        "Leave nothing behind: when a change of yours stops using a component you made, use it again or delete it.",
    };

    private static string RulesText => "PuppyMacro's rules for AI apps:\n" + string.Join("\n", GlobalRules.Select(r => "- " + r));

    /// <summary>
    /// What the first answer of a conversation adds after its own text, once (<see cref="AiServer"/>): the
    /// <see cref="GlobalRules"/> and what the user's Library holds (each collection, its app and how many components), so
    /// the AI app knows them before it builds anything.
    /// </summary>
    /// <summary>
    /// <see cref="FirstAnswer"/> read on the UI thread, or null while Allow AI access is off: a refused call gives nothing of
    /// the Library, and the rules come with the first answer once access is on.
    /// </summary>
    public Task<string?> FirstAnswerIfAllowed() =>
        _host.Ui(() => Task.FromResult(_host.Settings.AllowAiAccess ? FirstAnswer() : null));

    public string FirstAnswer()
    {
        var collections = _host.Macros.Library.Collections.Where(c => c.Components.Count > 0).ToList();
        string library = collections.Count == 0 ? "The user's Library has no components yet."
            : "The user's Library has components that already work: " + string.Join(", ", collections.Select(c =>
                  $"\"{c.Name}\" ({(string.IsNullOrEmpty(c.AppExe) ? "any app" : c.AppExe)}) {c.Components.Count}")) +
              $". {Name("search_library")} lists them with what each takes.";
        return Prefixed(RulesText) + "\n" + library;
    }

    /// <summary>The short names people read in the AI app's interface (MCP's title).</summary>
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["get_overview"] = "List loops, macros and remaps",
        ["get_item"] = "Read an item",
        ["get_schema"] = "Read an item's format",
        ["get_settings"] = "Read settings",
        ["check_hotkey"] = "Check a hotkey",
        ["list_apps"] = "List open apps",
        ["get_help"] = "Read the user guide",
        ["save_loop"] = "Save a loop",
        ["save_macro"] = "Save a macro",
        ["save_remap"] = "Save a remap",
        ["patch_item"] = "Change part of an item",
        ["update_actions"] = "Change many actions",
        ["delete_item"] = "Delete an item",
        ["set_enabled"] = "Turn an item on or off",
        ["update_settings"] = "Change settings",
        ["export_items"] = "Export items",
        ["import_items"] = "Import items",
        ["pick_points"] = "Ask for points",
        ["pick_app"] = "Ask for an app",
        ["pick_pixel"] = "Ask for a pixel",
        ["take_image"] = "Ask for an image",
        ["draw_area"] = "Ask for an area",
        ["look_at_screen"] = "Look at the screen",
        ["check_condition"] = "Check a condition",
        ["record_macro"] = "Record the user",
        ["test_macro"] = "Test a macro",
        ["get_last_run"] = "Read a macro's last run",
        ["read_resource"] = "Read a linked file",
        ["search_library"] = "Search the Library",
        ["get_component"] = "Read a component",
        ["save_component"] = "Save a component",
        ["patch_component"] = "Change part of a component",
        ["delete_component"] = "Delete a component",
        ["capture_hotkey"] = "Ask for a key",
        ["place_floating_button"] = "Place a floating button",
        ["open_item"] = "Open an item's editor",
        ["open_import_window"] = "Open Import",
        ["open_export_window"] = "Open Export",
        ["wait_for_user"] = "Wait for the user",
        ["stop_all"] = "Stop everything",
    };

    /// <summary>The tools that only read: calling one twice gives the same, and asks the user nothing (MCP idempotentHint).</summary>
    private static readonly HashSet<string> PureReads = new()
    {
        "get_overview", "get_item", "get_schema", "get_settings", "check_hotkey", "list_apps", "get_help", "export_items", "get_last_run",
        "read_resource", "search_library", "get_component",
    };

    private static readonly System.Text.RegularExpressions.Regex ToolWords = new(
        @"\b(" + string.Join("|", Titles.Keys.OrderByDescending(k => k.Length)) + @")\b");

    /// <summary>A text with every tool's name written in full ("call wait_for_user" → "call puppymacro_wait_for_user").</summary>
    public static string Prefixed(string text) => ToolWords.Replace(text, Prefix + "$1");

    private static readonly System.Text.RegularExpressions.Regex Quoted = new("\"[^\"\\n]*\"");

    /// <summary>
    /// An answer with every tool's name written in full, but what is between quotes as it is: the user's data (a name, a
    /// component's structured name with "get_item" in it) is always quoted in PuppyMacro's sentences.
    /// </summary>
    private static string PrefixedAnswer(string text)
    {
        var result = new StringBuilder();
        int at = 0;
        foreach (System.Text.RegularExpressions.Match quoted in Quoted.Matches(text))
        {
            result.Append(Prefixed(text[at..quoted.Index])).Append(quoted.Value);
            at = quoted.Index + quoted.Length;
        }
        return result.Append(Prefixed(text[at..])).ToString();
    }

    /// <summary>Debug is the user's alone: an AI app's save keeps the value the macro had (off for a new one).</summary>
    public static void KeepUsersDebug(object item, MacroLibrary macros)
    {
        if (item is MacroDefinition macro)
            macro.Debug = macros.Find(macro.Id)?.Debug ?? false;
    }

    /// <summary>The item a call is about, for its debug folder's name: the loop's, macro's or remap's name from "id".</summary>
    public async Task<string?> SubjectOf(JsonElement args)
    {
        // Only a name for the folder: a wrong "id" is the tool's own input error, said by the call.
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("id", out JsonElement given)
            || given.ValueKind != JsonValueKind.String || !Guid.TryParse(given.GetString(), out Guid id))
            return null;
        return await _host.Ui(() => Task.FromResult(Find(id) switch
        {
            LoopDefinition loop => loop.Name,
            MacroDefinition macro => macro.Name,
            RemapDefinition => "remap",
            _ => (string?)null,
        })).ConfigureAwait(false);
    }

    /// <summary>The tools' names, descriptions and schemas without a host: for the bridge while PuppyMacro is closed (never run).</summary>
    public static IReadOnlyList<AiTool> Definitions() => new AiTools(null!, null!).All;

    /// <summary>A request waiting for the user: its id (random), the app that asked, the answer it will give.</summary>
    private sealed class Pending(string id, string app, Task<AiToolResult> answer)
    {
        public string Id { get; } = id;
        public string App { get; } = app;
        public Task<AiToolResult> Answer { get; } = answer;

        /// <summary>A call gave its answer (under <c>_sync</c>).</summary>
        public bool Given { get; set; }
    }

    /// <summary>Runs a tool by name (for tests and the server): refused while AI access is off.</summary>
    public async Task<AiToolResult> Call(string name, JsonElement args, string app, CancellationToken cancel = default)
    {
        AiTool? tool = All.FirstOrDefault(t => t.Name == name);
        if (tool == null)
            return AiToolResult.Error($"Unknown tool \"{name}\".");
        if (!_host.Settings.AllowAiAccess)
        {
            _log.Add(app, AiActivityKind.Connection, Phrase.Of("Ai_CalledWhileOff", name), AiActivityResult.Refused);
            return AiToolResult.Error(AccessOff);
        }
        if (UnknownArguments(tool, args) is string unknown)
            return AiToolResult.Error(unknown);
        try
        {
            AiToolResult result = await tool.Run(args, app, cancel).ConfigureAwait(false);
            // Errors and PuppyMacro's own notes name tools by their full names; an item's data is given as it is.
            return result.IsError || result.Text.StartsWith("Waiting:", StringComparison.Ordinal) ? result with { Text = PrefixedAnswer(result.Text) } : result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return AiToolResult.Error(PrefixedAnswer(ex.Message));
        }
    }

    /// <summary>
    /// Arguments the tool's schema doesn't have (it allows no others), named with the ones it takes, or null: an argument
    /// written another way ("Where" for "where") is an input error, never left out (update_actions without where changes
    /// every action).
    /// </summary>
    private static string? UnknownArguments(AiTool tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return null;
        using var schema = JsonDocument.Parse(tool.InputSchema);
        var root = schema.RootElement;
        if (!root.TryGetProperty("additionalProperties", out JsonElement more) || more.ValueKind != JsonValueKind.False)
            return null;
        var known = root.TryGetProperty("properties", out JsonElement properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToList() : new List<string>();
        var unknown = args.EnumerateObject().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
        if (unknown.Count == 0)
            return null;
        return $"{tool.Name} has no argument {string.Join(", ", unknown.Select(n => $"\"{n}\""))}: nothing was done. " +
               (known.Count == 0 ? "It takes no arguments." : $"It takes {string.Join(", ", known.Select(n => $"\"{n}\""))}.");
    }

    // ================= Tool list =================

    private List<AiTool> Build()
    {
        const string none = """{"type":"object","properties":{},"additionalProperties":false}""";
        const string idOnly = """{"type":"object","properties":{"id":{"type":"string","description":"The Id of a loop, macro or remap (get_overview lists them)."}},"required":["id"],"additionalProperties":false}""";
        const string componentId = """{"type":"object","properties":{"id":{"type":"string","description":"A component's Id (search_library finds them)."}},"required":["id"],"additionalProperties":false}""";
        const string asking = """
            "title":{"type":"string","description":"Short title shown big, e.g. \"Pick the 3 items to destroy\"."},
            "description":{"type":"string","description":"One or two short sentences: what you are making together and why the user does this step."},
            "steps":{"type":"array","items":{"type":"string"},"minItems":1,"description":"What to do, in order, one short step each (shown numbered), e.g. [\"Open the game's inventory\", \"Press Enter\", \"Click the 3 items, in order\"]. For PuppyMacro's own screens take them from the user guide (get_help)."}
            """;
        const string askingRequired = "\"title\",\"description\",\"steps\"";
        const string snapshot = "\"snapshot_id\":{\"type\":\"string\",\"description\":\"A look_at_screen picture: read it at once instead of asking the user. Then title, description and steps are not needed.\"}";
        const string areaArg = "\"area\":{\"type\":\"object\",\"description\":\"A part of the screen in physical pixels.\",\"properties\":{\"X\":{\"type\":\"integer\"},\"Y\":{\"type\":\"integer\"},\"Width\":{\"type\":\"integer\"},\"Height\":{\"type\":\"integer\"}},\"required\":[\"X\",\"Y\",\"Width\",\"Height\"],\"additionalProperties\":false}";
        const string confirmed = """
            "confirmed_steps":{"type":"array","items":{"type":"string"},"minItems":1,"description":"The steps the loop or macro will play, one per item, each key or click and what it does: for a NEW item, exactly as you wrote them in the chat and the user confirmed them (the pick comes after that OK); for a change to an existing one, the steps as they will be after it."},
            """;
        static string Save(string kind) => """
            {"type":"object","properties":{
              "item":{"type":"object","description":"The KIND as JSON, in the format of get_schema(\"KIND\"). Leave out its Id."},
              "id":{"type":"string","description":"To change an existing KIND: its Id. Leave out for a new KIND."}},
             "required":["item"],"additionalProperties":false}
            """.Replace("KIND", kind);

        return new List<AiTool>
        {
            new("get_overview",
                "Lists the user's loops, macros and remaps (Id, name, hotkey, app, enabled, running, and Characters: how long " +
                "the item's compact JSON is) and the global hotkeys, says whether overlay mode is on, whether a request waits " +
                "for the user (its request_id) and whether the user has a PuppyMacro editor or dialog open (UserHasOpen " +
                "names it), and lists the Library's collections of reusable components (Id, name, how many components). With many items: words keeps " +
                "those whose name or app has every word, and offset and limit (100 when left out) page each list; Total and " +
                "Library.Collections say how many there are.",
                """{"type":"object","properties":{"words":{"type":"string","description":"Words the name or the app must have, e.g. \"diablo salvage\"."},"offset":{"type":"integer","minimum":0,"description":"How many of each list to skip; 0 when left out."},"limit":{"type":"integer","minimum":1,"maximum":1000,"description":"How many of each list to give; 100 when left out."}},"additionalProperties":false}""",
                true, false, (a, app, _) => Read(app, Phrase.Of("Ai_ReadOverview"), () => Overview(a))),
            new("get_item",
                "Gives one loop, macro or remap as compact JSON: one line, and the fields that have their default value left out. " +
                "Saves take it as it is, and patch paths point into it. A big item reads better in pieces: outline gives a " +
                "line with the macro's settings that differ from their defaults, then one line per action with its path (a " +
                "JSON Pointer), type, name and an If's conditions, down to outline_depth levels of Then and Else; path gives only the part there. A macro's rows linked to Library components are " +
                "listed after it.",
                """{"type":"object","properties":{"id":{"type":"string","description":"The Id of a loop, macro or remap (get_overview lists them)."},"outline":{"type":"boolean","description":"A macro: its outline instead of its JSON."},"outline_depth":{"type":"integer","minimum":1,"maximum":20,"description":"How many levels of Then and Else the outline goes into; 1 when left out (the macro's own actions)."},"path":{"type":"string","description":"A JSON Pointer into the item, e.g. /Actions/3 or /Actions/3/If/Then/0: only that part."}},"required":["id"],"additionalProperties":false}""",
                true, false, (a, app, _) => GetItem(a, app)),
            new("get_schema",
                "Gives the JSON Schema, with descriptions and defaults, of a loop, a macro, a remap or a Library component: every " +
                "field and allowed value.",
                """{"type":"object","properties":{"kind":{"type":"string","enum":["loop","macro","remap","component"]}},"required":["kind"],"additionalProperties":false}""",
                true, false, (a, app, _) => Task.FromResult(Schema(a))),
            new("get_settings",
                "Gives the settings an AI app can change with update_settings (theme, global hotkeys, overlay panel, sound volume).",
                none, true, false, (_, app, _) => Read(app, Phrase.Of("Ai_ReadSettings"),
                    () => JsonSerializer.Serialize(AiSettingsValues.From(_host.Settings), AnswerOptions))),
            new("check_hotkey",
                "Checks whether a key or combination can be the hotkey of a loop or macro, or the key of a remap, without clashing with what exists.",
                """
                {"type":"object","properties":{
                  "hotkey":{"type":"object","description":"A HotkeyBinding: {\"Vk\": 112} is F1, {\"Vk\": 65, \"Ctrl\": true} is Ctrl+A."},
                  "for":{"type":"string","enum":["hotkey","remap"],"description":"hotkey (default): a loop's or macro's hotkey; remap: a remap's key."},
                  "app":{"type":"string","description":"The .exe it works in; leave out for all apps."},
                  "mode":{"type":"string","enum":["Toggle","Hold"]},
                  "ignore_id":{"type":"string","description":"The Id of the item being changed, so its own hotkey does not count."}},
                 "required":["hotkey"],"additionalProperties":false}
                """, true, false, (a, app, _) => CheckHotkey(a, app)),
            new("list_apps",
                "Lists the .exe names of the apps with a window open, for an item that works only in one app (AppExe).",
                none, true, false, (_, app, _) => Read(app, Phrase.Of("Ai_ListedApps"), () => JsonSerializer.Serialize(_host.RunningApps(), AnswerOptions))),
            new("get_help",
                "Reads PuppyMacro's user guide. Without a topic: the list of topics.",
                """{"type":"object","properties":{"topic":{"type":"string","description":"A topic from the list, e.g. \"macros\"."}},"additionalProperties":false}""",
                true, false, (a, app, _) => Task.FromResult(Help(a, app))),

            new("save_loop", "Creates a loop, or rewrites the one with this id (or with the Id inside item, as get_item gives it). Saved at once (kept in its History)." + RewriteRule + NewItemRule + AfterNewItem,
                Save("loop"), false, true, (a, app, c) => SaveItem<LoopDefinition>(a, app, "loop", c)),
            new("save_macro", "Creates a macro, or rewrites the one with this id (or with the Id inside item, as get_item gives it). Saved at once (kept in its History)." + RewriteRule + NewItemRule + AfterNewItem,
                Save("macro"), false, true, (a, app, c) => SaveItem<MacroDefinition>(a, app, "macro", c)),
            new("save_remap", "Creates a remap, or rewrites the one with this id (or with the Id inside item, as get_item gives it). Saved at once (kept in its History)." + RewriteRule,
                Save("remap"), false, true, (a, app, c) => SaveItem<RemapDefinition>(a, app, "remap", c)),
            new("patch_item",
                "Changes part of a loop, macro or remap with a JSON Patch (RFC 6902): only what changes, so it is fast even for a " +
                "big macro (the same change on many actions: update_actions). Checked and saved like save_* (kept in " +
                "its History); the answer lists what changed.",
                """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The Id of the loop, macro or remap."},
                  "patch":{"type":"array","minItems":1,"description":"Operations applied in order, all or nothing. Paths are JSON Pointers into the item as get_item gives it, with list indexes from 0; a field get_item left out exists with its default, so replace works on it. E.g. [{\"op\":\"replace\",\"path\":\"/Actions/4/DelayMs\",\"value\":250}, {\"op\":\"add\",\"path\":\"/Actions/-\",\"value\":{\"Type\":\"PressKey\",\"Vk\":13}}, {\"op\":\"remove\",\"path\":\"/Actions/2\"}].",
                    "items":{"type":"object","properties":{
                      "op":{"type":"string","enum":["add","remove","replace","move","copy","test"]},
                      "path":{"type":"string"},
                      "from":{"type":"string","description":"move and copy: where the value comes from."},
                      "value":{"description":"add, replace and test: the value."}},
                     "required":["op","path"]}}},
                 "required":["id","patch"],"additionalProperties":false}
                """, false, true, (a, app, c) => PatchItem(a, app, c)),
            new("update_actions",
                "Changes the same fields of every action of a loop or macro that matches, in one short call: e.g. every delay of " +
                "800 ms to 1200 in a macro of 33 slots. No need to read the item or count indexes; prefer it to patch_item for " +
                "such changes. Checked and saved like save_* (kept in its History); the answer lists what changed.",
                """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The Id of the loop or macro."},
                  "where":{"type":"object","description":"Fields an action must have to change, with these values, e.g. {\"DelayMs\": 800} or {\"Type\": \"Click\", \"Vk\": 2}. Leave out to change every action."},
                  "group":{"type":"string","description":"Only the actions in the macro group with this name."},
                  "set":{"type":"object","description":"The fields to change and their new values, e.g. {\"DelayMs\": 1200}."}},
                 "required":["id","set"],"additionalProperties":false}
                """, false, true, (a, app, c) => UpdateActions(a, app, c)),
            new("delete_item", "Deletes a loop, macro or remap (the user may be asked to confirm; it can be restored in PuppyMacro).",
                idOnly, false, true, (a, app, c) => Delete(a, app, c)),
            new("set_enabled", "Enables or disables a loop, macro or remap.",
                """{"type":"object","properties":{"id":{"type":"string"},"enabled":{"type":"boolean"}},"required":["id","enabled"],"additionalProperties":false}""",
                false, true, (a, app, c) => SetEnabled(a, app, c)),
            new("update_settings", "Changes settings. Give only the values to change, in get_settings' format.",
                """{"type":"object","properties":{"settings":{"type":"object","description":"Values to change, e.g. {\"Theme\": \"Dark\", \"StopAllHotkey\": {\"Vk\": 123}}."}},"required":["settings"],"additionalProperties":false}""",
                false, true, (a, app, c) => UpdateSettings(a, app, c)),
            new("export_items",
                "Gives loops, macros and remaps as the text of a .puppyitems file, PuppyMacro's file to share them: the user saves " +
                "it as a .puppyitems file and opens it in PuppyMacro, also on another PC (Import), or gives it to an AI app " +
                "(import_items). When the user did not say which items, ask first (or all). open_export_window lets the user choose " +
                "and save the file in PuppyMacro instead.",
                """
                {"type":"object","properties":{
                  "ids":{"type":"array","items":{"type":"string"},"description":"The Ids of the loops, macros and remaps (get_overview lists them). Leave out for all of them."}},
                 "additionalProperties":false}
                """, true, false, (a, app, _) => ExportItems(a, app)),
            new("import_items",
                "Adds the loops, macros and remaps of a .puppyitems file's text (for example pasted by the user). Nothing is replaced: " +
                "each gets a new Id and comes disabled (the user turns it on), a name already used gets \" (2)\", a hotkey already " +
                "used is left out (a Hold item becomes Toggle), and so is a remap's hotkey (its Source) when already taken: that remap " +
                "can't be enabled until it gets a free one (check_hotkey, then patch_item). When the file has " +
                "several items and the user did not say which, ask first (or all) and give their indexes in only. The user may be " +
                "asked to confirm in PuppyMacro. open_import_window lets the user choose in PuppyMacro instead.",
                """
                {"type":"object","properties":{
                  "content":{"description":"The file's text, as a string, or its JSON object."},
                  "only":{"type":"array","items":{"type":"integer","minimum":0},"description":"The indexes, from 0, of the items to import in the file's Items. Leave out for all of them."}},
                 "required":["content"],"additionalProperties":false}
                """, false, false, (a, app, c) => ImportItems(a, app, c)),

            new("pick_points", "Shows the user your title, description and steps, waits until they are ready, then they click one or more points on the screen, in order (the order the loop or macro uses them): each one's position in physical pixels (X, Y)." + NewItemRule + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + confirmed + "\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":" + MaxPoints + "},\"labels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":" + MaxPoints + ",\"description\":\"One per point (as many as count), in order: what it is, shown next to the cursor while the user picks it, e.g. [\\\"The first item\\\", \\\"The second item\\\", \\\"The Confirm button\\\"].\"}," + asking + "},\"required\":[\"confirmed_steps\",\"count\",\"labels\"," + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickPoints(a, app, c)),
            new("pick_app", "Shows the user your title, description and steps, waits until they are ready, then they click a window of an app: its .exe name, for AppExe." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickApp(a, app, c)),
            new("pick_pixel",
                "For a Pixel color condition. The user clicks a pixel on a still picture of the screen, after the ready window with " +
                "your title, description and steps; the answer is X, Y and Color (#RRGGBB), as the condition takes them. With " +
                "snapshot_id (from look_at_screen) and x, y instead, it reads that pixel of the picture at once, without the user." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "," + snapshot +
                ",\"x\":{\"type\":\"integer\",\"description\":\"With snapshot_id: the pixel's screen X (physical pixels).\"},\"y\":{\"type\":\"integer\",\"description\":\"With snapshot_id: the pixel's screen Y.\"}},\"additionalProperties\":false}",
                true, false, (a, app, c) => PickOnScreen(a, app, AiScreenPickKind.Pixel, c)),
            new("take_image",
                "For an Image on screen condition. The user drags around the image on a still picture of the screen, after the ready " +
                "window with your title, description and steps. PuppyMacro keeps the image; the answer is its Id, X and Y (where it " +
                "was taken: an entry of the condition's Images), Width and Height. With snapshot_id and area instead, it cuts that " +
                "area of a look_at_screen picture at once, without the user. Small, distinctive images (a button, an icon) are found " +
                "fastest and most reliably; a flat area of one color can't be found (a Pixel color condition fits it)." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "," + snapshot + "," + areaArg + "},\"additionalProperties\":false}",
                true, false, (a, app, c) => PickOnScreen(a, app, AiScreenPickKind.Image, c)),
            new("draw_area",
                "For an Image or Text condition's search area (Area Drawn): shows the user your title, description and steps, waits " +
                "until they are ready, then they drag the area on a still picture of the screen. Answers AreaX, AreaY, AreaWidth and " +
                "AreaHeight." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickOnScreen(a, app, AiScreenPickKind.Area, c)),
            new("look_at_screen",
                "Takes the screen as PuppyMacro's conditions see it (PuppyMacro's own windows left out), after the ready window with " +
                "your title, description and steps, so the user can bring the right app in front first. The answer gives each " +
                "monitor's bounds and a snapshot_id, and links each monitor's picture (PNG at the screen's own size, physical " +
                "pixels, its size in the list of linked files): open the ones you need. take_image and pick_pixel read the same picture with " +
                "snapshot_id, without the user. Needs Let AI apps see the screen (on by default)." + LinksOpen + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => LookAtScreen(a, app, c)),
            new("check_condition",
                "Checks one condition on the screen now, as the condition window's Now on the screen does, after the ready window " +
                "with your title, description and steps (the user brings the screen the macro will see in front). The answer: True " +
                "(with Not applied), There (what it looks for is on the screen), where it was found, the image's similarity in " +
                "whole percent (100 = identical), the pixel's color seen, the text read, or why it could not look. Checking with the " +
                "right screen and with a wrong one shows how much Tolerance or Similarity leaves room. Needs Let AI apps see the screen." + UserWindow,
                "{\"type\":\"object\",\"properties\":{\"condition\":{\"type\":\"object\",\"description\":\"One condition as in a macro's If (get_schema(\\\"macro\\\"), MacroCondition).\"}," + asking + "},\"required\":[\"condition\"," + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => CheckCondition(a, app, c)),
            new("record_macro",
                "Records what the user does with PuppyMacro's recorder: after the ready window with your title, description and " +
                "steps, a short countdown, then every key and click until the user presses Stop on the recording bar. The answer is " +
                "the actions as PuppyMacro builds them (not saved: save_macro saves them, as they are or changed) and each click " +
                "with an image around it kept as a condition image (its Id), so an If can wait for what was clicked and click " +
                "where it is found. It links the recording's log (each key and click with its number and time) and the picture of " +
                "the screen at each of them, named by that number: open the ones you need. Without Let AI apps see the screen, no " +
                "pictures." + LinksOpen + UserWindow,
                "{\"type\":\"object\",\"properties\":{\"mouse_movement\":{\"type\":\"boolean\",\"description\":\"Also record the mouse's paths (Move along a path). Leave out for the user's own choice in PuppyMacro.\"}," + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => RecordMacro(a, app, c)),
            new("test_macro",
                "Plays a saved macro on the user's screen, as its Play button would, to see how it goes. First the ready window shows " +
                "your title, description and steps: the user brings the app the macro works in to the front and presses Start or " +
                "Enter, and the macro's keys and clicks go there. It ends by itself, at Stop all, or after max_seconds (20 by " +
                "default). The run is always logged, Debug or not. The answer says why it ended and links its log (each key, click " +
                "and If with its number and time, and what each If saw) and the picture of the screen just before each of them, " +
                "named by that number: open the ones you need. Needs Let AI apps test macros (on by default); pictures need Let AI " +
                "apps see the screen." + LinksOpen + UserWindow,
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"The macro's Id.\"},\"max_seconds\":{\"type\":\"number\",\"minimum\":1,\"maximum\":600,\"description\":\"The longest the test plays; 20 when left out.\"}," + confirmed + asking + "},\"required\":[\"id\",\"confirmed_steps\"," + askingRequired + "],\"additionalProperties\":false}",
                false, true, (a, app, c) => TestMacro(a, app, c)),
            new("get_last_run",
                "A macro's last run in PuppyMacro's debug data (a test_macro, or a run the user started with Debug on; kept 7 " +
                "days): how it ended, with its log and pictures linked, as test_macro gives them." + LinksOpen,
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"The macro's Id.\"}},\"required\":[\"id\"],\"additionalProperties\":false}",
                true, false, (a, app, _) => GetLastRun(a, app)),
            new("read_resource",
                "Reads a file another PuppyMacro tool linked (a resource link to PuppyMacro's debug data: a log, a picture of the " +
                "screen, JSON), the same file resources/read gives, for AI apps that cannot open resource links. Without options " +
                "it gives the whole file: a picture as an image, a text as text. An answer that links files lists each one with " +
                "what it holds, its size (a picture's width and height, a text's lines) and its weight. For a picture, area reads a part of it and scale a " +
                "smaller copy, width and height alike; a point (px, py) of what it gives is at (area X + px / scale, area Y + py / " +
                "scale) in the picture, and the answer repeats the numbers. For a text, offset and limit read some of its lines. " +
                "The files themselves never change. Pictures need Let AI apps see the screen.",
                """{"type":"object","properties":{"uri":{"type":"string","description":"The uri of a resource link another PuppyMacro tool gave (puppymacro://debug/…)."},"area":{"type":"object","description":"Pictures: the part to read, in the picture's own pixels (X and Y from 0 at its top left). Leave out for the whole picture.","properties":{"X":{"type":"integer","minimum":0},"Y":{"type":"integer","minimum":0},"Width":{"type":"integer","minimum":1},"Height":{"type":"integer","minimum":1}},"required":["X","Y","Width","Height"],"additionalProperties":false},"scale":{"type":"number","exclusiveMinimum":0,"maximum":1,"description":"Pictures: the size to read it at, width and height alike: 1 (when left out) its own size, 0.25 a quarter."},"offset":{"type":"integer","minimum":1,"description":"Texts (a log, JSON): the first line to read, from 1."},"limit":{"type":"integer","minimum":1,"description":"Texts: how many lines to read from offset; to the end when left out."}},"required":["uri"],"additionalProperties":false}""",
                true, false, (a, _, _) => ReadResource(a)),
            new("search_library",
                "Finds what already works in the user's apps, to link or extend instead of making it again: PuppyMacro never " +
                "duplicates what exists. The Library's components (a point, an If that finds something, a sequence) are kept to " +
                "be linked in macros; each is a function, with the parameters a call gives it " +
                "(their names and types are in the answer) and, when it can be a condition, Condition. Each has a structured name in English, " +
                "domain-app-purpose-concept (\"game-diablo_iv-find-rare_item_in_slot\"), a title in the user's words and maybe a " +
                "description. Filters narrow it (all optional, all must match): collection (an Id: its components), domain, app " +
                "(as in the name: the exe without .exe, lowercase, '_' for anything else, or any), purpose, and words found in " +
                "the name, title or description. The answer is short: up to limit components (20 when left out), each with its " +
                "Id, how many rows link it and its collection with the screen it was made on (its points are that screen's).",
                """{"type":"object","properties":{"collection":{"type":"string","description":"A collection's Id: only its components."},"domain":{"type":"string","enum":["game","office","browser","creative","dev","chat","system","other"]},"app":{"type":"string","description":"The app part of the name, e.g. diablo_iv, excel, any."},"purpose":{"type":"string","enum":["find","wait","read","state","point","act"],"description":"find: recognizes something; wait: waits until something shows or goes; read: reads a text; state: which screen the app is in; point: where to click or move; act: a sequence that does an operation."},"words":{"type":"string","description":"Words, in English as the names are, e.g. \"rare item\"."},"limit":{"type":"integer","minimum":1,"maximum":200}},"additionalProperties":false}""",
                true, false, (a, app, _) => SearchLibrary(a, app)),
            new("get_component",
                "Gives one Library component as compact JSON: its parameters, what it returns and its actions (the same fields as a " +
                "macro's, with Bind), its collection with the screen it was made on, the macros and components that call it, and a " +
                "call to copy (Call; AsCondition when it can be a condition) with each required parameter's example. " +
                ComponentsAreFunctions + " A linked row has no DelayMs or Name of its own.",
                componentId, true, false, (a, app, _) => GetComponent(a, app)),
            new("save_component",
                "Saves a Library component: something that works, to be linked in other macros (and shared with the collection " +
                "it is in). Without id it is new; with id its content is replaced (every macro and component that calls it plays " +
                "the new one). component, as get_schema(\"component\") describes it: Name (the user's words), Purpose (Find, Wait, " +
                "Read, State, Point or Act), Concept (English, lowercase letters, digits and _, as short as clear, e.g. " +
                "rare_item_in_slot), Description (short, English, optional), Parameters, Returns and Actions (as a macro's, " +
                "self-contained: a \"where … was found\" uses a condition inside it, what comes from outside comes as a parameter). " +
                ComponentsAreFunctions + " " + ReusableComponents + " When a new version changes what existing calls must give " +
                "(a parameter removed, renamed, a new one without a Default, another type), the answer lists the calls and asks " +
                "for call_values, one value for each parameter that every call without a fitting one gets, and renamed lets the " +
                "calls keep their values under a new name; the component and the calls are then saved together. collection, for " +
                "a new component: {\"id\"} of an existing one, or {\"name\", \"domain\", \"app\"} for a new one (made on the screen " +
                "its first point is on). The structured name is domain-app-purpose-concept; the concepts already in the Library " +
                "(search_library) are how later searches find it. Saved at once, kept in its History; the user's OK first when " +
                "they chose it.",
                """{"type":"object","properties":{"id":{"type":"string","description":"A component's Id, to change it; leave out for a new one."},"collection":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"domain":{"type":"string","enum":["game","office","browser","creative","dev","chat","system","other"]},"app":{"type":"string","description":"The app's .exe, e.g. Diablo IV.exe; leave out for any app."}},"additionalProperties":false},"component":{"type":"object","description":"Name, Purpose, Concept, Description, Parameters, Returns, Actions."},"call_values":{"type":"object","description":"Changing a component whose calls must change: for each parameter the answer named, the value every call without a fitting one gets.","additionalProperties":true},"renamed":{"type":"object","description":"Changing a component: parameters renamed, old name to new name, so the calls keep their values.","additionalProperties":{"type":"string"}}},"required":["component"],"additionalProperties":false}""",
                false, true, (a, app, c) => SaveComponent(a, app, c)),
            new("patch_component",
                "Changes part of a Library component with a JSON Patch (RFC 6902): only what changes, so it is fast even for a " +
                "big component. Then it is checked and saved as save_component saves it: kept in its History, and every macro " +
                "and component that calls it plays the new one. A change that existing calls can't follow (a parameter removed, " +
                "renamed, a new one without a Default, another type) gets the same answer as with save_component, which lists " +
                "the calls, and takes the same call_values and renamed. The user's OK first when they chose it.",
                """
                {"type":"object","properties":{
                  "id":{"type":"string","description":"The component's Id (search_library finds them)."},
                  "patch":{"type":"array","minItems":1,"description":"Operations applied in order, all or nothing. Paths are JSON Pointers into the component as get_component gives it in Component, with list indexes from 0; a field get_component left out exists with its default, so replace works on it. E.g. [{\"op\":\"replace\",\"path\":\"/Actions/0/If/Conditions/1/Tolerance\",\"value\":12}, {\"op\":\"replace\",\"path\":\"/Parameters/0/Default\",\"value\":400}].",
                    "items":{"type":"object","properties":{
                      "op":{"type":"string","enum":["add","remove","replace","move","copy","test"]},
                      "path":{"type":"string"},
                      "from":{"type":"string","description":"move and copy: where the value comes from."},
                      "value":{"description":"add, replace and test: the value."}},
                     "required":["op","path"]}},
                  "call_values":{"type":"object","description":"A change whose calls must change: for each parameter the answer named, the value every call without a fitting one gets.","additionalProperties":true},
                  "renamed":{"type":"object","description":"Parameters renamed, old name to new name, so the calls keep their values.","additionalProperties":{"type":"string"}}},
                 "required":["id","patch"],"additionalProperties":false}
                """, false, true, (a, app, c) => PatchComponent(a, app, c)),
            new("delete_component",
                "Deletes a Library component. One that a macro links or another component calls stays, and the answer names " +
                "them (get_component lists them too). The user may be asked to confirm; it can be restored in PuppyMacro.",
                componentId, false, true, (a, app, c) => DeleteComponent(a, app, c)),
            new("capture_hotkey", "Shows the user your title, description and steps and waits until they press a key, mouse button or combination: it as a HotkeyBinding." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => CaptureKeys(a, app, c)),
            new("place_floating_button",
                "Gives a loop or macro (Toggle mode only) a floating button for overlay mode, then shows the user your title, description and " +
                "steps and opens PuppyMacro's placement screen, where the user drags the button and presses Enter (Esc cancels). Saved at once." + UserWindow,
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"The Id of the loop or macro.\"}," + asking + "},\"required\":[\"id\"," + askingRequired + "],\"additionalProperties\":false}",
                false, true, (a, app, c) => PlaceFloatingButton(a, app, c)),
            new("open_item",
                "Opens a loop, macro or remap in PuppyMacro's editor on the user's screen, with your title and description at the top, " +
                "and waits until the user saves or cancels (the user's own changes)." + UserWindow,
                """{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string","description":"Short, e.g. \"Check the salvage macro\"."},"description":{"type":"string","description":"One or two short sentences: what to look at or change."}},"required":["id","title","description"],"additionalProperties":false}""",
                false, false, (a, app, c) => OpenItem(a, app, c)),
            new("open_import_window",
                "Opens PuppyMacro's Import window on the user's screen, with a .puppyitems file's text, or for the user to pick a " +
                "file, and waits until the user imports the items they check or cancels. The answer lists the items added." + UserWindow,
                """{"type":"object","properties":{"content":{"description":"The file's text, as a string, or its JSON object. Leave out for the user to pick a file."}},"additionalProperties":false}""",
                false, false, (a, app, c) => OpenImport(a, app, c)),
            new("open_export_window",
                "Opens PuppyMacro's Export window on the user's screen, with these items checked, and waits until the user saves " +
                "the .puppyitems file or cancels." + UserWindow,
                """{"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"},"description":"The Ids of the items to check at first. Leave out for none."}},"additionalProperties":false}""",
                false, false, (a, app, c) => OpenExport(a, app, c)),
            new("wait_for_user", "Waits again for the user's answer to a request that answered \"waiting\" with this request_id.",
                """{"type":"object","properties":{"request_id":{"type":"string"}},"required":["request_id"],"additionalProperties":false}""",
                true, false, (a, app, c) => WaitAgain(a, app, c)),

            new("stop_all", "Stops every running loop and macro, like PuppyMacro's Stop all. " + NoStart,
                none, false, false, (_, app, _) => StopAll(app)),
        };
    }

    // ================= Reads =================

    private async Task<AiToolResult> Read(string app, Phrase? logText, Func<string> read)
    {
        // Key names in the answer in English (i18n.md): the AI app's texts.
        string text = await _host.Ui(() => Task.FromResult(Loc.English(read))).ConfigureAwait(false);
        if (logText != null)
            _log.Add(app, AiActivityKind.Read, logText);
        return new AiToolResult(text);
    }

    /// <summary>Default of get_overview's limit, per list.</summary>
    public const int DefaultOverviewLimit = 100;

    private string Overview(JsonElement args)
    {
        AppSettings s = _host.Settings;
        var words = (Str(args, "words") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int offset = IntArg(args, "offset", 0) ?? 0;
        int limit = IntArg(args, "limit", 1, 1000) ?? DefaultOverviewLimit;
        bool Has(string name, string? appExe) =>
            words.All(w => name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || (appExe?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
        var loops = s.Loops.Where(l => Has(l.Name, l.AppExe)).ToList();
        var macros = _host.Macros.Macros.Where(m => Has(m.Name, m.AppExe)).ToList();
        var remaps = s.Remaps.Where(r => Has(r.Note, r.AppExe)).ToList();
        var collections = _host.Macros.Library.Collections.Where(c => Has(c.Name, c.AppExe)).ToList();
        string? open = _host.UserHasOpen;
        var overview = new
        {
            Total = new { Loops = loops.Count, Macros = macros.Count, Remaps = remaps.Count },
            Loops = loops.Skip(offset).Take(limit).Select(l => new
            {
                l.Id, l.Name, l.Enabled, Running = _host.IsRunning(l.Id), Hotkey = Keys(l.Hotkey), l.Mode,
                App = l.AppExe, Actions = l.Actions.Count, Characters = Characters(l),
            }),
            Macros = macros.Skip(offset).Take(limit).Select(m => new
            {
                m.Id, m.Name, m.Enabled, Running = _host.IsRunning(m.Id), Hotkey = Keys(m.Hotkey), m.Mode,
                App = m.AppExe, Actions = m.Actions.Count, Characters = Characters(m),
            }),
            Remaps = remaps.Skip(offset).Take(limit).Select(r => new
            {
                r.Id, Source = Keys(r.Source), Target = Keys(r.Target), App = r.AppExe, r.Enabled, r.Note, Characters = Characters(r),
            }),
            Library = new
            {
                Collections = collections.Count,
                List = collections.Skip(offset).Take(limit).Select(c => new { c.Id, c.Name, Components = c.Components.Count }),
            },
            GlobalHotkeys = new
            {
                OpenMain = Keys(s.OpenMainHotkey), OverlayMode = Keys(s.OverlayModeHotkey), StopAll = Keys(s.StopAllHotkey),
                Record = Keys(s.RecordHotkey),
            },
            OverlayModeOn = _host.OverlayModeActive,
            WaitingForUser = Waiting()?.Id,
            UserIsEditing = open != null,
            UserHasOpen = open,
        };
        return JsonSerializer.Serialize(overview, AnswerOptions);
    }

    /// <summary>How long the item's compact JSON (what get_item gives) is, in characters.</summary>
    private static int Characters(object item) => Loc.English(() => AiJson.Compact(item)).Length;

    private static string? Keys(HotkeyBinding? binding) => binding is { IsSet: true } ? KeyNames.Format(binding) : null;

    /// <summary>Keys named in the language of the moment, or "none".</summary>
    private static PhraseArg KeyArg(HotkeyBinding? binding) =>
        binding is { IsSet: true } ? PhraseArg.Of(binding.Clone()) : Phrase.Of("History_None");


    private async Task<AiToolResult> GetItem(JsonElement args, string app)
    {
        Guid id = IdArg(args, "id");
        bool outline = BoolArg(args, "outline") ?? false;
        int? depth = IntArg(args, "outline_depth", 1, 20);
        string? path = Str(args, "path");
        if (outline && path != null)
            throw new FormatException("\"outline\" and \"path\" are two ways to read an item: the outline lists every action's path, path reads one part. Give one of them.");
        if (depth != null && !outline)
            throw new FormatException("\"outline_depth\" is how deep the outline goes, so it needs \"outline\": true.");
        return await _host.Ui(() =>
        {
            object? item = Find(id);
            if (item == null)
                return Task.FromResult(AiToolResult.Error(NotAnItem(id, "loop, macro or remap", "get_component reads it.", " get_overview lists them.")));
            _log.Add(app, AiActivityKind.Read, About("Ai_Read", item));
            if (outline)
                return Task.FromResult(item is MacroDefinition outlined
                    ? new AiToolResult(Outline(outlined, depth ?? 1) + LinkedList(outlined))
                    : AiToolResult.Error("Only a macro has an outline: a loop or remap is small, read it whole."));
            string compact = Loc.English(() => AiJson.Compact(item));
            if (path is { Length: > 0 })
                return Task.FromResult(AtPath(compact, Serialize(item), path, out string? problem) is string part
                    ? new AiToolResult(part)
                    : AiToolResult.Error(problem!));
            return Task.FromResult(new AiToolResult(compact + (item is MacroDefinition macro ? LinkedList(macro) : "")));
        }).ConfigureAwait(false);
    }

    private static AiToolResult Schema(JsonElement args) => OneOf(args, "kind", SchemaKinds) switch
    {
        "loop" => new AiToolResult(WithoutId(CodeSchema.ForLoop(Guid.Empty, withDefaults: true))),
        "macro" => new AiToolResult(WithTextLanguages(WithoutId(CodeSchema.ForMacro(Guid.Empty, withDefaults: true)))),
        "remap" => new AiToolResult(WithoutId(CodeSchema.ForRemap(Guid.Empty, withDefaults: true))),
        "component" => new AiToolResult(WithTextLanguages(WithoutId(CodeSchema.ForComponent(Guid.Empty, withDefaults: true)))),
        _ => AiToolResult.Error("Give \"kind\": loop, macro, remap or component."),
    };

    private static readonly string[] SchemaKinds = { "loop", "macro", "remap", "component" };

    /// <summary>A Text condition's Language: the languages Windows can read on this PC, named in the macro's schema.</summary>
    private static string WithTextLanguages(string schema)
    {
        IReadOnlyList<string> languages = Screen.TextReader.Languages;
        return schema.Replace("(one of the languages Windows can read on this PC)",
            languages.Count == 0
                ? "(Windows can read no language on this PC: the user adds one in Windows Settings, Time & language, Language & region)"
                : $"(Windows can read these on this PC: {string.Join(", ", languages)})", StringComparison.Ordinal);
    }

    /// <summary>The Code view's schema without the Id: PuppyMacro gives it (a new one, or the one of the item changed).</summary>
    private static string WithoutId(string schema)
    {
        JsonObject node = JsonNode.Parse(schema)!.AsObject();
        (node["properties"] as JsonObject)?.Remove("Id");
        if (node["required"] is JsonArray required && required.FirstOrDefault(r => r?.GetValue<string>() == "Id") is JsonNode id)
            required.Remove(id);
        return node.ToJsonString();
    }

    private AiToolResult Help(JsonElement args, string app)
    {
        string? topic = Str(args, "topic");
        if (string.IsNullOrWhiteSpace(topic))
            return new AiToolResult(AiHelp.Index());
        string? text = AiHelp.Text(topic);
        if (text == null)
            return AiToolResult.Error($"Unknown topic \"{topic}\".\n\n{AiHelp.Index()}");
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadGuide",
            AiHelp.Sections.First(s => s.Id.Equals(topic.Trim(), StringComparison.OrdinalIgnoreCase)).Title));
        return new AiToolResult(text);
    }

    private async Task<AiToolResult> CheckHotkey(JsonElement args, string app)
    {
        HotkeyBinding binding = Arg<HotkeyBinding>(args, "hotkey");
        bool remap = OneOf(args, "for", new[] { "hotkey", "remap" }, "hotkey") == "remap";
        string? appExe = AppScope.Normalize(Str(args, "app"));
        bool hold = OneOf(args, "mode", new[] { "Toggle", "Hold" }, "Toggle") == "Hold";
        Guid? ignore = OptionalIdArg(args, "ignore_id", "the Id of the loop, macro or remap being changed (get_overview lists them)");
        if (!binding.IsSet)
            return AiToolResult.Error("Set Vk: the key's virtual-key code.");

        string? problem = await _host.Ui(() => Task.FromResult(Loc.English(() => remap
            ? HotkeyConflicts.RemapClash(binding, appExe, _host.Settings, _host.Macros, ignore ?? Guid.Empty)
            : HotkeyRules.Problem(binding, hold) ?? HotkeyConflicts.Find(binding, _host.Settings, _host.Macros, ignore, appExe: appExe))))
            .ConfigureAwait(false);
        string keys = Loc.English(() => KeyNames.Format(binding));
        return new AiToolResult(problem == null ? $"{keys} is free." : $"{keys} can't be used: {problem}");
    }

    // ================= Changes =================

    private async Task<AiToolResult> SaveItem<T>(JsonElement args, string app, string kind, CancellationToken cancel) where T : class
    {
        if (!args.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            return AiToolResult.Error("Give the " + kind + " in \"item\", as a JSON object.");
        Guid? editId = OptionalIdArg(args, "id", "the Id of the " + kind + " to rewrite (get_overview lists them); leave it out for a new one");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        // get_item's JSON sent back as it is: the Id inside names the item it rewrites, never a copy of it.
        if (editId == null && item.TryGetProperty("Id", out JsonElement own) && own.ValueKind == JsonValueKind.String
            && Guid.TryParse(own.GetString(), out Guid ownId))
            editId = await _host.Ui(() => Task.FromResult(Find(ownId) is T ? (Guid?)ownId : null)).ConfigureAwait(false);

        // The checks of the editor's Code view, with the Id the item has (a new one for a new item).
        // The checks' problems in English: they are the AI app's answer (and the activity's detail).
        (T? parsed, string? problems, string? before) = await _host.Ui(() => Task.FromResult(Loc.English(() => ParseItem<T>(item, editId))))
            .ConfigureAwait(false);
        if (parsed == null)
        {
            _log.Add(app, AiActivityKind.Proposal, Phrase.Of($"Ai_{(editId == null ? "NewProposal" : "ChangeProposal")}_{kind}"),
                AiActivityResult.Error, problems ?? "", item.GetRawText());
            return AiToolResult.Error($"The {kind} has problems, nothing was saved:\n{problems}");
        }

        string json = Serialize(parsed);
        List<Phrase> diff = before == null ? new List<Phrase>() : ItemHistory.Diff(before, json);
        List<string> changes = diff.Select(c => c.English).ToList();
        Phrase label = before == null ? About("Ai_New", parsed)
            : Phrase.Of($"Ai_Changed_{kind}", TitleArg(parsed), diff.Count == 1 ? diff[0] : Phrase.Plural("History_Changes", diff.Count));
        if (before != null && before == json)
            return new AiToolResult($"Nothing to change: the {kind} is already like this.");
        Guid id = Id(parsed);

        AiToolResult Apply()
        {
            // Checked again on the UI thread: meanwhile the user may have deleted it or opened a PuppyMacro window.
            if (BusyNow() is AiToolResult busy)
                return busy;
            if (before != null && Find(id) == null)
                return AiToolResult.Error($"The {kind} was deleted meanwhile: nothing was saved.");
            bool wasRunning = _host.IsRunning(id);
            var called = CalledNow();
            _host.Save(parsed, app);
            _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", json, id, kind, before);
            return new AiToolResult((before == null ? $"Saved the new {kind}. Its Id: {id}." : $"Saved the changes to the {kind}:\n" + ChangeList(changes))
                + (wasRunning ? "\nIt was running: it stopped." : "") + LeftUnused(called));
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);

        return await StartForUser(app, async () =>
        {
            // The user's window, in the user's language.
            AiAnswer answer = await _host.Confirm(app, Loc.Text($"Ai_Confirm{(before == null ? "New" : "Changes")}_{kind}"),
                before == null ? new List<string> { About("Ai_Item", parsed).ToString() } : diff.Select(c => c.ToString()).ToList(),
                Strings.Ai_Save);
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Proposal, label, answer, "", json);
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>On the UI thread: the item with the Id it must have, read with the editor's checks, and the JSON it replaces.</summary>
    private (T? Item, string? Problems, string? Before) ParseItem<T>(JsonElement item, Guid? editId) where T : class
    {
        object? existing = editId is Guid id ? Find(id) : null;
        if (editId != null && existing is not T)
            return (null, NotAnItem(editId.Value, typeof(T).Name.Replace("Definition", "").ToLowerInvariant(), "save_component or patch_component changes it."), null);
        Guid itemId = editId ?? Guid.NewGuid();

        JsonObject node = JsonNode.Parse(item.GetRawText()) as JsonObject ?? new JsonObject();
        node["Id"] = itemId.ToString();
        string text = node.ToJsonString(CodeJson.Options);

        List<CodeProblem> problems;
        object? parsed = typeof(T) == typeof(LoopDefinition)
            ? LoopJson.Parse(text, itemId, (b, a) => HotkeyConflicts.Find(b, _host.Settings, _host.Macros, itemId, appExe: a), out problems)
            : typeof(T) == typeof(MacroDefinition)
                ? MacroJson.Parse(text, itemId, (b, a) => HotkeyConflicts.Find(b, _host.Settings, _host.Macros, itemId, appExe: a), out problems,
                    _host.Macros.Images.Has, _host.Macros.Library.Signatures())
                : RemapJson.Parse(text, itemId, (b, a) => HotkeyConflicts.RemapClash(b, a, _host.Settings, _host.Macros, itemId), out problems);
        if (parsed is not T ok)
            return (null, string.Join("\n", problems.Select(p => "- " + p.Message)), null);
        // Debug is the user's: the item keeps what it has before anything is compared, shown or saved.
        KeepUsersDebug(ok, _host.Macros);
        return (ok, null, existing == null ? null : Serialize(existing));
    }

    /// <summary>The patch on the item's whole JSON (every field there), then save_*'s checks, permissions and History.</summary>
    private async Task<AiToolResult> PatchItem(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        if (!args.TryGetProperty("patch", out JsonElement patch))
            throw new FormatException("Give the \"patch\": the operations that change the item.");
        (string? kind, string? json, string? missing) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Kind(item), (string?)Serialize(item), (string?)null)
            : (null, null, NotAnItem(id, "loop, macro or remap", "patch_component changes it.", " get_overview lists them.")))).ConfigureAwait(false);
        if (kind == null || json == null)
            return AiToolResult.Error(missing!);

        JsonNode patched = AiJson.Patch(JsonNode.Parse(json)!, patch);
        JsonElement save = JsonSerializer.SerializeToElement(new JsonObject { ["item"] = patched, ["id"] = id.ToString() });
        return kind switch
        {
            "loop" => await SaveItem<LoopDefinition>(save, app, kind, cancel).ConfigureAwait(false),
            "macro" => await SaveItem<MacroDefinition>(save, app, kind, cancel).ConfigureAwait(false),
            _ => await SaveItem<RemapDefinition>(save, app, kind, cancel).ConfigureAwait(false),
        };
    }

    /// <summary>update_actions: <see cref="AiJson.SetWhere"/> on the item's whole JSON, then save_*'s checks, permissions and History.</summary>
    private async Task<AiToolResult> UpdateActions(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        if (!args.TryGetProperty("set", out JsonElement set))
            throw new FormatException("Give \"set\": the fields to change, e.g. {\"DelayMs\": 1200}.");
        args.TryGetProperty("where", out JsonElement where);
        string? group = Str(args, "group");
        (string? kind, string? json, string? missing) = await _host.Ui(() => Task.FromResult(Find(id) is { } item and (LoopDefinition or MacroDefinition)
            ? ((string?)Kind(item), (string?)Serialize(item), (string?)null)
            : (null, null, NotAnItem(id, "loop or macro", "patch_component changes it.", " get_overview lists them.")))).ConfigureAwait(false);
        if (kind == null || json == null)
            return AiToolResult.Error(missing!);

        (JsonNode changed, _) = AiJson.SetWhere(JsonNode.Parse(json)!, where, set, group);
        JsonElement save = JsonSerializer.SerializeToElement(new JsonObject { ["item"] = changed, ["id"] = id.ToString() });
        return kind == "loop"
            ? await SaveItem<LoopDefinition>(save, app, kind, cancel).ConfigureAwait(false)
            : await SaveItem<MacroDefinition>(save, app, kind, cancel).ConfigureAwait(false);
    }

    /// <summary>What changed, one per line, at most 30 (an AI app reads every line).</summary>
    private static string ChangeList(List<string> changes) =>
        string.Join("\n", changes.Take(30).Select(c => "- " + c)) + (changes.Count > 30 ? $"\n- and {changes.Count - 30} more" : "");

    private async Task<AiToolResult> Delete(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? kind, string? before, object? found) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Loc.English(() => Describe(item)), (string?)Kind(item), (string?)Serialize(item), (object?)item)
            : (null, null, NotAnItem(id, "loop, macro or remap", "delete_component deletes it."), null))).ConfigureAwait(false);
        if (what == null || found == null)
            return AiToolResult.Error(before!);

        Phrase label = About("Ai_Deleted", found);
        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busy)
                return busy;
            if (Find(id) == null)
                return AiToolResult.Error($"The {kind} is already gone.");
            var called = CalledNow();
            _host.Delete(id, app);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied, "", null, id, kind, before);
            return new AiToolResult($"Deleted {what}. The user can restore it in PuppyMacro." + LeftUnused(called));
        }

        if (!_host.Settings.AiAskBeforeDeleting)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, Loc.Text($"Ai_ConfirmDelete_{kind}"),
                new[] { About("Ai_Item", found).ToString() }, Strings.Ai_Delete);
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Confirmation, label, answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> SetEnabled(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        bool enabled = BoolArg(args, "enabled") ?? throw new FormatException("\"enabled\" must be true (to enable it) or false (to disable it).");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? kind, string? before, bool already, object? found) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Loc.English(() => Describe(item)), (string?)Kind(item), (string?)Serialize(item), IsEnabled(item) == enabled, (object?)item)
            : (null, null, NotAnItem(id, "loop, macro or remap", "A component has no on or off: it plays where a macro links it."), false, null)))
            .ConfigureAwait(false);
        if (what == null || found == null)
            return AiToolResult.Error(before!);
        if (already)
            return new AiToolResult($"{Capital(what)} is already {(enabled ? "enabled" : "disabled")}.");
        if (enabled && found is RemapDefinition { Source: not { IsSet: true } })
            return AiToolResult.Error($"{Capital(what)} has no hotkey (Import left it out, as it was taken), so it can't be " +
                                      "enabled. Give it a free one first (check_hotkey, then patch_item on its Source).");

        string verb = enabled ? "Enable" : "Disable";
        Phrase label = About(enabled ? "Ai_Enabled" : "Ai_Disabled", found);
        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busy)
                return busy;
            if (Find(id) == null)
                return AiToolResult.Error($"The {kind} was deleted meanwhile.");
            _host.SetEnabled(id, enabled, app);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied, "", null, id, kind, before);
            return new AiToolResult($"{(enabled ? "Enabled" : "Disabled")} {what}.");
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, Loc.Text($"Ai_Confirm{verb}_{kind}"),
                new[] { About("Ai_Item", found).ToString() }, Loc.Text($"Ai_{verb}"));
            if (answer == AiAnswer.Done && Find(id) != null)
                return Apply();
            Log(app, AiActivityKind.Confirmation, label, answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> UpdateSettings(JsonElement args, string app, CancellationToken cancel)
    {
        if (!args.TryGetProperty("settings", out JsonElement json) || json.ValueKind != JsonValueKind.Object)
            return AiToolResult.Error("Give the values to change in \"settings\", as a JSON object.");
        AiSettingsValues wanted = Arg<AiSettingsValues>(args, "settings");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);

        // The problems in English: the AI app's answer.
        (List<AiSettingChange> changes, string? problem) = await _host.Ui(() => Task.FromResult(Loc.English(() => SettingChanges(wanted))))
            .ConfigureAwait(false);
        if (problem != null)
            return AiToolResult.Error(problem);
        if (changes.Count == 0)
            return new AiToolResult("Nothing to change: the settings already have these values.");

        Phrase label = Phrase.Of("Ai_ChangedSettings", Phrase.List(changes.Select(c => (PhraseArg)c.Name)));
        AiToolResult Apply()
        {
            // Checked again: the user may have changed something meanwhile.
            (_, string? stillProblem) = Loc.English(() => SettingChanges(wanted));
            if (stillProblem != null)
            {
                _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Error, stillProblem);
                return AiToolResult.Error(stillProblem);
            }
            // Restore puts back only what this changes (the user's later changes of other settings stay).
            string before = JsonSerializer.Serialize(Previous(wanted), CodeJson.Options);
            _host.ApplySettings(wanted);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied,
                string.Join("; ", changes.Select(c => c.Line.ToString())), null, null, "settings", before);
            return new AiToolResult("Settings changed: " + string.Join("; ", changes.Select(c => $"{c.Label} {c.ToEnglish}")) + ".");
        }

        if (!_host.Settings.AiAskBeforeSettings)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, Strings.Ai_ConfirmSettings,
                changes.Select(c => c.Line.ToString()).ToList(), Strings.Ai_Apply);
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Confirmation, label, answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>On the UI thread: the current values of the settings <paramref name="wanted"/> sets, the others null (kept).</summary>
    private AiSettingsValues Previous(AiSettingsValues wanted)
    {
        AiSettingsValues now = AiSettingsValues.From(_host.Settings);
        return new AiSettingsValues
        {
            Theme = wanted.Theme != null ? now.Theme : null,
            CloseToTray = wanted.CloseToTray != null ? now.CloseToTray : null,
            StopAllHotkey = wanted.StopAllHotkey != null ? now.StopAllHotkey : null,
            OverlayModeHotkey = wanted.OverlayModeHotkey != null ? now.OverlayModeHotkey : null,
            RecordHotkey = wanted.RecordHotkey != null ? now.RecordHotkey : null,
            // Open PuppyMacro is the global hotkey the user can clear: "none" is kept as an unset binding, so Restore clears it
            // (null would mean "leave it as it is").
            OpenMainHotkey = wanted.OpenMainHotkey != null ? now.OpenMainHotkey ?? new HotkeyBinding() : null,
            ShowOverlayPanel = wanted.ShowOverlayPanel != null ? now.ShowOverlayPanel : null,
            ClickItemsInPanel = wanted.ClickItemsInPanel != null ? now.ClickItemsInPanel : null,
            OverlayPanelOpacity = wanted.OverlayPanelOpacity != null ? now.OverlayPanelOpacity : null,
            SoundVolume = wanted.SoundVolume != null ? now.SoundVolume : null,
            RecognitionDevice = wanted.RecognitionDevice != null ? now.RecognitionDevice : null,
        };
    }

    /// <summary>
    /// Restores settings an AI app changed (<see cref="AiActivity.Before"/>): checked as a change would be (a global hotkey
    /// may clash with what was made since). Null when done, otherwise why not. On the UI thread.
    /// </summary>
    public string? RestoreSettings(AiSettingsValues values)
    {
        // "No Open PuppyMacro hotkey", as the user had it before (Previous): only Restore clears it, so the checks of a change
        // (which refuse an unset hotkey from an AI app) see the others.
        bool clearOpenMain = values.OpenMainHotkey is { IsSet: false };
        AiSettingsValues checkedValues = values;
        if (clearOpenMain)
        {
            checkedValues = JsonSerializer.Deserialize<AiSettingsValues>(JsonSerializer.Serialize(values, CodeJson.Options), CodeJson.Options)!;
            checkedValues.OpenMainHotkey = null;
        }
        // Shown to the user (Restore on the AI page): in the user's language.
        (_, string? problem) = SettingChanges(checkedValues);
        if (problem != null)
            return string.Format(CultureInfo.CurrentCulture, Strings.Ai_CantRestoreSettings, problem);
        _host.ApplySettings(values);
        return null;
    }

    /// <summary>On the UI thread, while a request starts: the user opened a PuppyMacro window meanwhile.</summary>
    private AiToolResult? BusyNow() => _host.UserHasOpen is string open ? AiToolResult.Error(UserHasOpen(open)) : null;

    /// <summary>Why nothing happens while the user has a PuppyMacro window open, naming it.</summary>
    private static string UserHasOpen(string open) =>
        $"The user has {open} open in PuppyMacro. While a PuppyMacro editor or dialog is open, PuppyMacro changes nothing and " +
        "opens no window for an AI app: once the user closes it, the same call works.";

    /// <summary>On the UI thread: what would change, or why it can't.</summary>
    private (List<AiSettingChange> Changes, string? Problem) SettingChanges(AiSettingsValues wanted)
    {
        AppSettings s = _host.Settings;
        var changes = new List<AiSettingChange>();
        static PhraseArg OnOff(bool value) => Phrase.Of(value ? "History_On" : "History_Off");
        static PhraseArg Percent(int value) => Phrase.Of("Ai_Percent", value.ToString(CultureInfo.InvariantCulture));
        static PhraseArg Theme(AppTheme value) => Phrase.Of($"Theme_{value}");

        static PhraseArg Device(RecognitionDevice value) => Phrase.Of(value switch
        {
            RecognitionDevice.Gpu => "Settings_RecognitionGpu",
            RecognitionDevice.Cpu => "Settings_RecognitionCpu",
            _ => "Settings_RecognitionAutomatic",
        });

        if (wanted.RecognitionDevice is RecognitionDevice device && device != s.RecognitionDevice)
            changes.Add(new(Phrase.Of("Settings_RecognitionDevice"), Device(s.RecognitionDevice), Device(device)));
        if (wanted.Theme is AppTheme theme && theme != s.Theme)
            changes.Add(new(Phrase.Of("AiSetting_Theme"), Theme(s.Theme), Theme(theme)));
        if (wanted.CloseToTray is bool tray && tray != s.CloseToTray)
            changes.Add(new(Phrase.Of("AiSetting_CloseToTray"), OnOff(s.CloseToTray), OnOff(tray)));
        if (wanted.ShowOverlayPanel is bool panel && panel != s.ShowOverlayPanel)
            changes.Add(new(Phrase.Of("AiSetting_OverlayPanel"), OnOff(s.ShowOverlayPanel), OnOff(panel)));
        if (wanted.ClickItemsInPanel is bool click && click != s.ClickItemsInPanel)
            changes.Add(new(Phrase.Of("AiSetting_ClickItemsInPanel"), OnOff(s.ClickItemsInPanel), OnOff(click)));
        if (wanted.OverlayPanelOpacity is int opacity)
        {
            if (opacity is < AppSettings.MinOpacity or > AppSettings.MaxOpacity)
                return (changes, $"OverlayPanelOpacity must be between {AppSettings.MinOpacity} and {AppSettings.MaxOpacity}.");
            if (opacity != s.OverlayPanelOpacity)
                changes.Add(new(Phrase.Of("AiSetting_OverlayPanelOpacity"), Percent(s.OverlayPanelOpacity), Percent(opacity)));
        }
        if (wanted.SoundVolume is int volume)
        {
            if (volume is < 0 or > 100)
                return (changes, "SoundVolume must be between 0 and 100.");
            if (volume != s.SoundVolume)
                changes.Add(new(Phrase.Of("AiSetting_SoundVolume"), Percent(s.SoundVolume), Percent(volume)));
        }

        var newHotkeys = new List<(Phrase Name, HotkeyBinding Value)>();
        foreach (var (name, which, value, current) in new[]
                 {
                     (Phrase.Of("AiSetting_OpenMainHotkey"), "OpenMain", wanted.OpenMainHotkey, s.OpenMainHotkey),
                     (Phrase.Of("AiSetting_StopAllHotkey"), "StopAll", wanted.StopAllHotkey, s.StopAllHotkey),
                     (Phrase.Of("AiSetting_OverlayModeHotkey"), "OverlayMode", wanted.OverlayModeHotkey, s.OverlayModeHotkey),
                     (Phrase.Of("AiSetting_RecordHotkey"), "Record", wanted.RecordHotkey, s.RecordHotkey),
                 })
        {
            if (value == null || value.SameAs(current))
                continue;
            // In this thread's language: English for the AI app (update_settings), the user's for Restore.
            string label = name.ToString();
            if (!value.IsSet)
                return (changes, $"{label}: set Vk, the key's virtual-key code.");
            if ((HotkeyRules.Problem(value, hold: false) ?? HotkeyConflicts.Find(value, s, _host.Macros, ignoreGlobal: which)) is string problem)
                return (changes, $"{label}: {problem}");
            // Two of the new global hotkeys must differ too (each was checked only against the current ones).
            if (newHotkeys.FirstOrDefault(n => n.Value.Overlaps(value)) is { Name: not null } same)
                return (changes, $"{label}: {KeyNames.Format(value)} would also be the {same.Name}.");
            newHotkeys.Add((name, value));
            changes.Add(new(name, current is { IsSet: true } ? PhraseArg.Of(current.Clone()) : Phrase.Of("Ai_NoneValue"),
                PhraseArg.Of(value.Clone())));
        }
        return (changes, null);
    }

    // ================= Interactions =================

    /// <param name="count">1 for pick_point; null: the "count" argument (pick_points).</param>
    private async Task<AiToolResult> PickPoints(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        RequireConfirmedSteps(args);
        List<string> labels = StringList(args, "labels", "what its point is") ?? new List<string>();
        int points = IntArg(args, "count", 1, MaxPoints) ?? throw new FormatException($"Give \"count\": how many points, from 1 to {MaxPoints}.");
        if (labels.Count != points)
            return AiToolResult.Error($"Give one label per point: {points} points, {labels.Count} labels.");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = points == 1 ? Phrase.Of("Ai_AskedPoint", ask.Title) : Phrase.Plural("Ai_AskedPoints", points, ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            if (BusyNow() is AiToolResult busy)
                return busy;
            var (answer, picked) = await _host.PickPoints(app, ask.Title, labels);
            string text = JsonSerializer.Serialize(picked.Select(p => new { X = p.X, Y = p.Y }));
            Log(app, AiActivityKind.Interaction, label, answer, answer == AiAnswer.Done ? string.Join("; ", picked.Select(p => $"{p.X}, {p.Y}")) : "");
            return Answered(answer, text);
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> PickApp(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_AskedApp", ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            if (BusyNow() is AiToolResult busy)
                return busy;
            var (answer, exe) = await _host.PickApp(app, ask.Title);
            Log(app, AiActivityKind.Interaction, label, answer, exe ?? "");
            return Answered(answer, JsonSerializer.Serialize(new { AppExe = exe }, AnswerOptions));
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>The ready window, then the pick on a still picture of the screen (pick_pixel, take_image, draw_area).</summary>
    private async Task<AiToolResult> PickOnScreen(JsonElement args, string app, AiScreenPickKind kind, CancellationToken cancel)
    {
        // From a look_at_screen picture: the AI app chose the place itself, nothing to ask the user.
        if (kind != AiScreenPickKind.Area && Str(args, "snapshot_id") is string snapshot)
            return await FromSnapshotPick(args, app, kind, snapshot).ConfigureAwait(false);
        // x and y, area: a place in a look_at_screen picture, read only with its snapshot_id.
        if (kind == AiScreenPickKind.Pixel && (Given(args, "x", out _) || Given(args, "y", out _)))
            throw new FormatException("\"x\" and \"y\" are a pixel of a look_at_screen picture, so they go with its \"snapshot_id\". Without them the user clicks the pixel.");
        if (kind == AiScreenPickKind.Image && Given(args, "area", out _))
            throw new FormatException("\"area\" is a part of a look_at_screen picture, so it goes with its \"snapshot_id\". Without them the user drags around the image.");
        AiAsk ask = Asking(args);
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of(kind switch
        {
            AiScreenPickKind.Image => "Ai_AskedImage",
            AiScreenPickKind.Area => "Ai_AskedArea",
            _ => "Ai_AskedPixel",
        }, ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            if (BusyNow() is AiToolResult busyNow)
                return busyNow;
            var (answer, pick) = await _host.PickOnScreen(app, ask.Title, kind);
            string text = pick == null ? "" : kind switch
            {
                AiScreenPickKind.Pixel => JsonSerializer.Serialize(new { pick.X, pick.Y, pick.Color }),
                AiScreenPickKind.Image => JsonSerializer.Serialize(new { Id = pick.ImageId, pick.X, pick.Y, pick.Width, pick.Height }),
                _ => JsonSerializer.Serialize(new { AreaX = pick.X, AreaY = pick.Y, AreaWidth = pick.Width, AreaHeight = pick.Height }),
            };
            Log(app, AiActivityKind.Interaction, label, answer,
                pick == null ? "" : kind == AiScreenPickKind.Pixel ? $"{pick.X}, {pick.Y} {pick.Color}" : $"{pick.X}, {pick.Y}, {pick.Width} × {pick.Height}");
            return Answered(answer, text);
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>No separate ready step: the key window itself shows the title, description and steps and waits.</summary>
    private async Task<AiToolResult> CaptureKeys(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_AskedKey", ask.Title);
        return await StartForUser(app, async () =>
        {
            var (answer, keys) = await _host.CaptureKeys(app, ask);
            Log(app, AiActivityKind.Interaction, label, answer, Keys(keys) ?? "");
            return Answered(answer, keys == null ? "" :
                $"{Loc.English(() => KeyNames.Format(keys))}: {JsonSerializer.Serialize(keys, AnswerOptions)}");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>The item's floating button on (and shown in overlay), then the ready window and the placement screen.</summary>
    private async Task<AiToolResult> PlaceFloatingButton(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        AiAsk ask = Asking(args);
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? problem, object? found) = await _host.Ui(() => Task.FromResult(Find(id) switch
        {
            LoopDefinition { Mode: ActivationMode.Hold } or MacroDefinition { Mode: ActivationMode.Hold } =>
                (null, "Floating buttons work only for Toggle items: change its Mode to Toggle first (save_loop or save_macro).", null),
            { } item when item is LoopDefinition or MacroDefinition => ((string?)Loc.English(() => Describe(item)), (string?)null, (object?)item),
            null => (null, $"No loop or macro has the Id {id}.", null),
            _ => (null, "Only loops and macros have floating buttons.", null),
        })).ConfigureAwait(false);
        if (problem != null || what == null || found == null)
            return AiToolResult.Error(problem ?? "");

        Phrase label = About("Ai_PlacedButton", found);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            if (BusyNow() is AiToolResult busy)
                return busy;
            if (Find(id) is not { } item)
                return AiToolResult.Error("It was deleted meanwhile.");
            string before = Serialize(item);
            AiAnswer answer = await _host.PlaceFloatingButton(id, app);
            if (answer == AiAnswer.Done)
            {
                _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", Serialize(Find(id)!), id, Kind(item), before);
                return new AiToolResult($"The floating button of {what} is on and placed. It shows in overlay mode.");
            }
            Log(app, AiActivityKind.Interaction, label, answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>The item's editor, with the AI's title and description: the user's own changes, saved or not.</summary>
    private async Task<AiToolResult> OpenItem(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        string title = (Str(args, "title") ?? "").Trim();
        string description = (Str(args, "description") ?? "").Trim();
        if (title.Length == 0)
            throw new FormatException("Give a short \"title\": it is shown to the user.");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? problem, object? found) = await _host.Ui(() => Task.FromResult(Find(id) switch
        {
            null => (null, NotAnItem(id, "loop, macro or remap", "open_item opens loops, macros and remaps; get_component reads a component."), null),
            LoopDefinition or MacroDefinition when _host.AnyRunning =>
                (null, "Loops or macros are running: their editors open only when nothing runs. Ask the user to stop them (or call stop_all).", null),
            { } item => ((string?)Loc.English(() => Describe(item)), (string?)null, (object?)item),
        })).ConfigureAwait(false);
        if (problem != null || what == null || found == null)
            return AiToolResult.Error(problem ?? "");

        Phrase label = About("Ai_Opened", found);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.OpenItem(id, app, title, description);
            _log.Add(app, AiActivityKind.Interaction, label, answer switch
            {
                AiAnswer.Done => AiActivityResult.Saved,
                AiAnswer.TimedOut => AiActivityResult.TimedOut,
                _ => AiActivityResult.Cancelled,
            });
            return answer switch
            {
                AiAnswer.Done => new AiToolResult($"The user saved {what}:\n{(Find(id) is { } saved ? Serialize(saved) : "")}"), // JSON: no texts
                AiAnswer.TimedOut => new AiToolResult("Nobody used PuppyMacro for a while: the editor closed without saving."),
                _ => new AiToolResult("The user closed the editor without saving."),
            };
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> StopAll(string app)
    {
        await _host.Ui(() =>
        {
            _host.StopAll();
            return Task.FromResult(true);
        }).ConfigureAwait(false);
        _log.Add(app, AiActivityKind.Interaction, Phrase.Of("Ai_StoppedAll"), AiActivityResult.Applied);
        return new AiToolResult("Stopped every running loop and macro.");
    }

    // ================= Sharing (.puppyitems files) =================

    /// <summary>The items with these Ids, in the lists' order (all when <paramref name="ids"/> is null); the first unknown Id otherwise.</summary>
    private (List<object> Items, string? Unknown) ItemsByIds(List<Guid>? ids)
    {
        var all = _host.Settings.Loops.Cast<object>().Concat(_host.Macros.Macros).Concat(_host.Settings.Remaps).ToList();
        if (ids == null)
            return (all, null);
        if (ids.Where(id => Find(id) == null).Select(id => (Guid?)id).FirstOrDefault() is Guid unknown)
            return (new List<object>(), unknown.ToString());
        return (all.Where(i => ids.Contains(Id(i))).ToList(), null);
    }

    private static List<Guid>? IdsArg(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("ids", out JsonElement list) || list.ValueKind == JsonValueKind.Null)
            return null;
        if (list.ValueKind != JsonValueKind.Array)
            throw new FormatException("\"ids\" must be a list of Ids (get_overview lists them).");
        return list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out Guid id) ? id
            : throw new FormatException("\"ids\" must be a list of Ids (get_overview lists them).")).ToList();
    }

    /// <summary>A .puppyitems file's text: a string, or its JSON object.</summary>
    private static string? ContentArg(JsonElement args, bool required)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null)
            return required ? throw new FormatException("Give the file's text in \"content\".") : null;
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Object => content.GetRawText(),
            _ => throw new FormatException("\"content\" must be the file's text, or its JSON object."),
        };
    }

    private async Task<AiToolResult> ExportItems(JsonElement args, string app)
    {
        List<Guid>? ids = IdsArg(args);
        if (ids is { Count: 0 })
            return AiToolResult.Error("Give at least one Id in \"ids\", or leave \"ids\" out for every item.");
        (string? text, string? unknown, int count) = await _host.Ui(() =>
        {
            (List<object> items, string? missing) = ItemsByIds(ids);
            return Task.FromResult(missing != null ? (null, missing, 0) : ((string?)ItemFile.Write(items, ItemFile.AppVersion, _host.Macros.Images, _host.Macros.Library), (string?)null, items.Count));
        }).ConfigureAwait(false);
        if (unknown != null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {unknown}. get_overview lists them.");
        if (count == 0)
            return AiToolResult.Error("There is nothing to export: the user has no loops, macros or remaps.");
        _log.Add(app, AiActivityKind.Read, Phrase.Plural("Ai_Exported", count));
        return new AiToolResult(text!);
    }

    private async Task<AiToolResult> ImportItems(JsonElement args, string app, CancellationToken cancel)
    {
        string text = ContentArg(args, required: true)!;
        List<int>? only = null;
        if (Given(args, "only", out JsonElement list))
            only = list.ValueKind != JsonValueKind.Array
                ? throw new FormatException("\"only\" must be a list of indexes, from 0; leave it out to import every item.")
                : list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int i) ? i
                    : throw new FormatException("\"only\" must be a list of indexes (whole numbers), from 0.")).ToList();
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);

        // In English: its problems and notes are the AI app's answer (the user's window reads the file in their language).
        ItemFileContent content = await _host.Ui(() => Task.FromResult(Loc.English(() =>
                ItemFile.Read(text, _host.Settings, _host.Macros, ItemFile.AppVersion, only))))
            .ConfigureAwait(false);
        if (content.Error != null)
            return AiToolResult.Error(content.Error);
        if (only?.Where(i => i < 0 || i >= content.Entries.Count).Select(i => (int?)i).FirstOrDefault() is int wrong)
            return AiToolResult.Error($"The file has no item {wrong}: its Items go from 0 to {content.Entries.Count - 1}.");
        List<ItemFileEntry> chosen = content.Entries.Where(e => e.Selected).ToList();
        string skipped = string.Join("", content.Entries.Where(e => e.Problem != null && (only == null || only.Contains(e.Index)))
            .Select(e => $"\n- Not imported, {e.Kind} {e.Index} \"{e.Title}\": {e.Problem}"));
        if (chosen.Count == 0 && content.NewCollections.Count == 0)
            return AiToolResult.Error("Nothing was imported." + skipped);

        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busyNow)
                return busyNow;
            // Read again: meanwhile the user may have taken a name or a hotkey.
            ItemFileContent now = Loc.English(() => ItemFile.Read(text, _host.Settings, _host.Macros, ItemFile.AppVersion, only));
            ItemFile.AddImages(now, _host.Macros);
            // The Library collections first: the macros may link them.
            if (_host.AddCollections(now.NewCollections, app) is string notAdded)
                return AiToolResult.Error(notAdded);
            var lines = now.NewCollections.Select(c => $"- Library collection \"{c.Name}\", Id {c.Id}").ToList();
            int imported = 0;
            foreach (ItemFileEntry entry in now.Entries.Where(e => e.Selected))
            {
                object item = entry.Item!;
                _host.Save(item, app);
                _log.Add(app, AiActivityKind.Proposal, About("Ai_Imported", item), AiActivityResult.Saved, "", Serialize(item), Id(item), Kind(item));
                lines.Add($"- {Capital(Loc.English(() => Describe(item)))}, Id {Id(item)}" + string.Concat(entry.Notes.Select(n => " " + n)));
                imported++;
            }
            return new AiToolResult($"Imported {imported} item{(imported == 1 ? "" : "s")}, all disabled: the user turns them on " +
                                    $"({Name("set_enabled")}, or the switch on their card).\n{string.Join("\n", lines)}{skipped}");
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);

        return await StartForUser(app, async () =>
        {
            // The user's window: the file read again in the user's language (its notes), each item named as the cards do,
            // and the Library collections it adds with them.
            ItemFileContent read = ItemFile.Read(text, _host.Settings, _host.Macros, ItemFile.AppVersion, only);
            List<string> shown = read.Entries
                .Where(e => e.Selected)
                .Select(e => About("Ai_Item", e.Item!).ToString() + string.Concat(e.Notes.Select(n => " " + n)))
                .Concat(read.NewCollections.Select(c => string.Format(CultureInfo.CurrentCulture, Strings.Ai_ImportCollection, c.Name)))
                .ToList();
            AiAnswer answer = await _host.Confirm(app, Plurals.Format("Ai_ConfirmImport", chosen.Count), shown, Strings.Ai_Import);
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Proposal, Phrase.Plural("Ai_ImportCount", chosen.Count), answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> OpenImport(JsonElement args, string app, CancellationToken cancel)
    {
        string? text = ContentArg(args, required: false);
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        return await StartForUser(app, async () =>
        {
            (AiAnswer answer, List<object> items) = await _host.ShowImport(app, text);
            if (answer == AiAnswer.Done)
            {
                foreach (object item in items)
                    _log.Add(app, AiActivityKind.Proposal, About("Ai_Imported", item), AiActivityResult.Saved, "", Serialize(item), Id(item), Kind(item));
                return new AiToolResult(items.Count == 0 ? "The user imported nothing."
                    : $"The user imported {items.Count} item{(items.Count == 1 ? "" : "s")}, all disabled:\n" +
                      string.Join("\n", items.Select(i => $"- {Capital(Loc.English(() => Describe(i)))}, Id {Id(i)}")));
            }
            Log(app, AiActivityKind.Interaction, Phrase.Of("Ai_OpenedImport"), answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> OpenExport(JsonElement args, string app, CancellationToken cancel)
    {
        List<Guid> ids = IdsArg(args) ?? new List<Guid>();
        string? unknown = await _host.Ui(() => Task.FromResult(ItemsByIds(ids).Unknown)).ConfigureAwait(false);
        if (unknown != null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {unknown}. get_overview lists them.");
        if (await Refusal(app).ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        return await StartForUser(app, async () =>
        {
            (AiAnswer answer, List<object> items, string? file) = await _host.ShowExport(app, ids);
            Log(app, AiActivityKind.Interaction, Phrase.Of("Ai_OpenedExport"), answer, "");
            return answer == AiAnswer.Done
                ? new AiToolResult($"The user exported {items.Count} item{(items.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(file)}.")
                : Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    // ================= Waiting for the user =================

    /// <summary>Why a request can't run now, or null.</summary>
    private async Task<string?> Refusal(string app)
    {
        if (Waiting() is { } waiting)
            return Busy(waiting, app);
        return await _host.Ui(() => Task.FromResult(_host.UserHasOpen is string open ? UserHasOpen(open) : null))
            .ConfigureAwait(false);
    }

    private Pending? Waiting()
    {
        lock (_sync)
            return _pending is { Answer.IsCompleted: false } p ? p : null;
    }

    /// <summary>
    /// Why a request can't start while another waits for the user: the app that made it waits with wait_for_user; another app
    /// can't (wait_for_user answers only the app that asked), so it is told to try again.
    /// </summary>
    private static string Busy(Pending waiting, string app) => waiting.App == app
        ? $"Another request is waiting for the user (request_id \"{waiting.Id}\"). Call wait_for_user with it first."
        : "The user is answering another AI app's request in PuppyMacro: one request waits for the user at a time. Try again " +
          "once it is over.";

    /// <summary>Opens the user's window (on the UI thread) and waits for the answer, at most <see cref="WaitStep"/>.</summary>
    private async Task<AiToolResult> StartForUser(string app, Func<Task<AiToolResult>> ask, CancellationToken cancel)
    {
        Pending pending;
        lock (_sync)
        {
            if (_pending is { Answer.IsCompleted: false } other)
                return AiToolResult.Error(Busy(other, app));
            if (_pending != null)
                Keep(_pending); // answered, not given yet: kept for wait_for_user
            pending = new Pending(Guid.NewGuid().ToString("N")[..8], app, _host.Ui(ask));
            _pending = pending;
        }
        return await Wait(pending, cancel).ConfigureAwait(false);
    }

    /// <summary>wait_for_user: only the app that asked gets the answer.</summary>
    private async Task<AiToolResult> WaitAgain(JsonElement args, string app, CancellationToken cancel)
    {
        string id = RequiredStr(args, "request_id", "the request_id of the request that answered \"waiting\"");
        Pending? pending;
        lock (_sync)
        {
            if (_answered.TryGetValue(id, out Pending? answered) && answered.App == app)
                return Give(answered) ?? AiToolResult.Error($"The answer of request \"{id}\" was already given.");
            pending = _pending is { } current && current.Id == id && current.App == app ? current : null;
        }
        if (pending == null)
            return AiToolResult.Error($"No request \"{id}\" of yours is waiting, or its answer was already given.");
        return await Wait(pending, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> Wait(Pending pending, CancellationToken cancel)
    {
        try
        {
            await Task.WhenAny(pending.Answer, Task.Delay(WaitStep, cancel)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (!pending.Answer.IsCompleted)
        {
            // Kept for wait_for_user also if this call was cancelled: the user may still answer.
            _ = pending.Answer.ContinueWith(_ =>
            {
                lock (_sync)
                    Keep(pending);
            }, TaskScheduler.Default);
            return new AiToolResult(
                $"Waiting: the PuppyMacro window is open and the user has not answered yet (request_id \"{pending.Id}\"). " +
                "Call wait_for_user with this request_id to get the answer.");
        }
        lock (_sync)
            return Give(pending) ?? AiToolResult.Error($"The answer of request \"{pending.Id}\" was already given.");
    }

    /// <summary>Under <see cref="_sync"/>: an answered request no call has given yet, kept until wait_for_user takes it.</summary>
    private void Keep(Pending pending)
    {
        if (pending.Given)
            return;
        if (_pending == pending)
            _pending = null;
        _answered[pending.Id] = pending;
    }

    /// <summary>Under <see cref="_sync"/>: the answer, once; null when a call already gave it.</summary>
    private AiToolResult? Give(Pending pending)
    {
        if (pending.Given)
            return null;
        pending.Given = true;
        if (_pending == pending)
            _pending = null;
        _answered.Remove(pending.Id);
        return Result(pending.Answer);
    }

    private static AiToolResult Result(Task<AiToolResult> answer) =>
        answer.IsCompletedSuccessfully ? answer.Result
        : AiToolResult.Error("PuppyMacro could not finish the request: " + (answer.Exception?.InnerException?.Message ?? "it was cancelled."));

    private static AiToolResult Answered(AiAnswer answer, string done) => answer switch
    {
        AiAnswer.Done => new AiToolResult(done),
        AiAnswer.TimedOut => new AiToolResult($"Nobody used PuppyMacro for {AnswerTimeout.TotalMinutes:0} minutes: the window closed and nothing changed."),
        _ => new AiToolResult("The user cancelled: nothing changed."),
    };

    private void Log(string app, AiActivityKind kind, Phrase label, AiAnswer answer, string detail, string? data = null) =>
        _log.Add(app, kind, label, answer switch
        {
            AiAnswer.Done => kind == AiActivityKind.Proposal ? AiActivityResult.Saved : AiActivityResult.Applied,
            AiAnswer.TimedOut => AiActivityResult.TimedOut,
            _ => AiActivityResult.Cancelled,
        }, detail, data);

    // ================= Helpers =================

    /// <summary>On the UI thread: the loop, macro or remap with this Id, or null.</summary>
    private object? Find(Guid id) =>
        (object?)_host.Settings.Loops.FirstOrDefault(l => l.Id == id)
        ?? (object?)_host.Macros.Find(id)
        ?? _host.Settings.Remaps.FirstOrDefault(r => r.Id == id);

    private static string Kind(object item) => item switch
    {
        LoopDefinition => "loop",
        MacroDefinition => "macro",
        _ => "remap",
    };

    private static Guid Id(object item) => item switch
    {
        LoopDefinition loop => loop.Id,
        MacroDefinition macro => macro.Id,
        RemapDefinition remap => remap.Id,
        _ => Guid.Empty,
    };

    private static bool IsEnabled(object item) => item switch
    {
        LoopDefinition loop => loop.Enabled,
        MacroDefinition macro => macro.Enabled,
        RemapDefinition remap => remap.Enabled,
        _ => false,
    };

    /// <summary>"loop \"Spam\"", "remap Ctrl + X → A": for the AI app (made in English with <see cref="Loc.English{T}"/>).</summary>
    private static string Describe(object item) => $"{Kind(item)} {Title(item)}";

    /// <summary>A text about an item, kept in the activity and shown in the language of the moment: the key's
    /// "_loop", "_macro" or "_remap" text (each language says "deleted loop" and "deleted macro" its own way) with the
    /// item's title as {0}.</summary>
    private static Phrase About(string key, object item) => Phrase.Of($"{key}_{Kind(item)}", TitleArg(item));

    /// <summary>A loop's or macro's name in quotes, or a remap's keys, named in the language of the moment.</summary>
    private static PhraseArg TitleArg(object item) => item switch
    {
        LoopDefinition loop => $"\"{loop.Name}\"",
        MacroDefinition macro => $"\"{macro.Name}\"",
        RemapDefinition remap => remap.AppExe == null
            ? Phrase.Of("Ai_RemapTitle", KeyArg(remap.Source), KeyArg(remap.Target))
            : Phrase.Of("Ai_RemapTitleInApp", KeyArg(remap.Source), KeyArg(remap.Target), remap.AppExe),
        _ => "",
    };

    private static string Title(object item) => item switch
    {
        LoopDefinition loop => $"\"{loop.Name}\"",
        MacroDefinition macro => $"\"{macro.Name}\"",
        RemapDefinition remap => $"{KeyNames.Format(remap.Source)} → {KeyNames.Format(remap.Target)}" +
            (remap.AppExe == null ? "" : $" in {remap.AppExe}"),
        _ => "",
    };

    private static string Serialize(object item) => item switch
    {
        LoopDefinition loop => LoopJson.Serialize(loop),
        MacroDefinition macro => MacroJson.Serialize(macro),
        RemapDefinition remap => RemapJson.Serialize(remap),
        _ => JsonSerializer.Serialize(item, CodeJson.Options),
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A text argument: null when left out (or null). Any other value than a text is an input error naming it
    /// (<paramref name="label"/>, "collection.app", for one inside an object), never read as left out.
    /// </summary>
    private static string? Str(JsonElement args, string name, string? label = null) =>
        !Given(args, name, out JsonElement value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : throw new FormatException($"\"{label ?? name}\" must be a text (a JSON string), not {JsonKind(value)}.");

    /// <summary>A required text argument: missing, null or blank is an input error saying what to give.</summary>
    private static string RequiredStr(JsonElement args, string name, string what) =>
        Str(args, name) is { } text && text.Trim().Length > 0 ? text : throw new FormatException($"Give \"{name}\": {what}.");

    /// <summary>
    /// One of <paramref name="allowed"/> (in any case), or null when left out; any other value is an input error naming
    /// the argument and its values (<paramref name="whenLeftOut"/>: what leaving it out means), never read as left out.
    /// </summary>
    private static string? OneOf(JsonElement args, string name, IReadOnlyList<string> allowed, string? whenLeftOut = null)
    {
        string? text = Str(args, name);
        if (text == null)
            return null;
        return allowed.FirstOrDefault(a => a.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException($"\"{name}\" must be {Or(allowed)}" + (whenLeftOut == null ? "." : $"; left out, it is {whenLeftOut}."));
    }

    /// <summary>"a, b or c".</summary>
    private static string Or(IReadOnlyList<string> values) =>
        values.Count == 1 ? values[0] : string.Join(", ", values.Take(values.Count - 1)) + " or " + values[^1];

    /// <summary>
    /// A list of texts, each with something in it, or null when left out (or null). Anything else (not a list, an entry that
    /// is not a text or is blank) is an input error naming the argument and the entry.
    /// </summary>
    private static List<string>? StringList(JsonElement args, string name, string what)
    {
        if (!Given(args, name, out JsonElement list))
            return null;
        if (list.ValueKind != JsonValueKind.Array)
            throw new FormatException($"\"{name}\" must be a list of texts ({what}), not {JsonKind(list)}.");
        var texts = new List<string>();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                throw new FormatException($"Every one of the \"{name}\" must be a text ({what}); the one at index {texts.Count} (from 0) is {JsonKind(entry)}.");
            if (entry.GetString()!.Trim() is not { Length: > 0 } text)
                throw new FormatException($"Every one of the \"{name}\" must say something ({what}); the one at index {texts.Count} (from 0) is blank.");
            texts.Add(text);
        }
        return texts;
    }

    /// <summary>What a JSON value is, for an input error: "a number", "a list".</summary>
    private static string JsonKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        _ => "null",
    };

    private static Guid IdArg(JsonElement args, string name, string what = "an Id (get_overview lists them)") =>
        Given(args, name, out JsonElement value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid id)
            ? id
            : throw new FormatException($"\"{name}\" must be {what}.");

    /// <summary>An Id that may be left out (also null or ""); anything else that is not an Id is an input error naming it.</summary>
    private static Guid? OptionalIdArg(JsonElement args, string name, string what)
    {
        if (!Given(args, name, out JsonElement value) || value.ValueKind == JsonValueKind.String && value.GetString() is "")
            return null;
        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid id)
            ? id
            : throw new FormatException($"\"{name}\" must be {what}.");
    }

    /// <summary>An object argument read as <typeparamref name="T"/>; missing, null or unreadable is an input error naming it.</summary>
    private static T Arg<T>(JsonElement args, string name) where T : class
    {
        if (!Given(args, name, out JsonElement value))
            throw new JsonException($"\"{name}\" is missing.");
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"\"{name}\" must be an object, not {JsonKind(value)}.");
        try
        {
            // In English: a wrong value (a name a list doesn't have) is said by a converter in the language of the moment.
            return Loc.English(() => JsonSerializer.Deserialize<T>(value.GetRawText(), ReadOptions)) ?? throw new JsonException($"\"{name}\" is empty.");
        }
        catch (JsonException ex) when (!ex.Message.StartsWith($"\"{name}\"", StringComparison.Ordinal))
        {
            throw new JsonException($"\"{name}\" can't be read: {ex.Message}");
        }
    }

    /// <summary>The title (shown big), the description (what and why) and the numbered steps the AI writes for the user.</summary>
    private static AiAsk Asking(JsonElement args)
    {
        string title = (Str(args, "title") ?? "").Trim();
        string description = (Str(args, "description") ?? "").Trim();
        if (title.Length == 0)
            throw new FormatException("Give a short \"title\": it is shown big to the user.");
        if (description.Length == 0)
            throw new FormatException("Give a \"description\": one or two short sentences on what you are making and why.");
        List<string> steps = StringList(args, "steps", "what the user does, one short step each") ?? new List<string>();
        if (steps.Count == 0)
            throw new FormatException("Give the \"steps\": what the user does, in order, one short step each.");
        return new AiAsk(title, description, steps);
    }

    /// <summary>
    /// confirmed_steps (pick_points, test_macro): the steps the item will play, as the user confirmed them; missing or empty
    /// is an input error.
    /// </summary>
    private static void RequireConfirmedSteps(JsonElement args)
    {
        if (StringList(args, "confirmed_steps", "one step the item will play") is not { Count: > 0 })
            throw new FormatException("Give \"confirmed_steps\": the steps the loop or macro will play. For a new one, write them " +
                "in the chat first and wait for the user's OK.");
    }
}
