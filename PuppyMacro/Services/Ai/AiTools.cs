using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
}

/// <summary>One MCP tool: its name, what it does (for the AI), its input JSON Schema and what runs it.</summary>
internal sealed record AiTool(string Name, string Description, string InputSchema, bool ReadOnly, bool Destructive,
    Func<JsonElement, string, CancellationToken, Task<AiToolResult>> Run);

/// <summary>
/// The tools PuppyMacro gives AI apps (MCP), without the protocol (<see cref="AiServer"/> adds it). Reads answer at once.
/// Changes are saved at once, as the user's Save would, and kept in the item's History; they wait for the user's OK only
/// where the AI page's Ask before … says so. Interactions show what the AI asks and why, and start when the user is ready.
/// A request that waits for the user answers "waiting" after <see cref="WaitStep"/> (AI apps cut long calls) and the AI
/// calls wait_for_user. One such request at a time. Everything is refused while Allow AI access is off.
/// </summary>
internal sealed class AiTools
{
    /// <summary>How long a window opened for the AI stays open while nobody uses PuppyMacro.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long one call waits for the user before answering "waiting" (AI apps cut calls after a minute or more).</summary>
    public static TimeSpan WaitStep { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>The most points pick_points asks for at once.</summary>
    public const int MaxPoints = 20;

    public const string Instructions =
        "PuppyMacro automates keyboard and mouse input on Windows: loops (press keys or click at intervals), macros " +
        "(recorded or built step by step) and remaps (a key or combination sends another one). These tools read and change " +
        "the user's loops, macros, remaps and settings.\n\n" +
        "How requests work:\n" +
        "- Read tools (get_overview, get_item, get_schema, get_settings, check_hotkey, get_status, list_apps, get_help, export_items) answer at once.\n" +
        "- save_loop, save_macro and save_remap create or change an item; delete_item, set_enabled and update_settings do " +
        "what they say. They are saved at once and kept in the item's History in PuppyMacro, where the user can restore " +
        "any version. Some may wait for the user's OK in PuppyMacro, as the user chose in PuppyMacro's AI settings: the " +
        "answer then says whether the user confirmed or cancelled. After a change, tell the user to try it.\n" +
        "- pick_point, pick_points, pick_app and capture_hotkey ask the user to click points on the screen, click a window " +
        "of an app, or press keys. Use them instead of guessing coordinates, app names or key codes. First explain in the " +
        "chat what the user will have to do; then call the tool. PuppyMacro shows a window in the middle of the screen, on " +
        "top of everything, and waits until the user is ready (Enter or Start) before the user picks, so they can first open " +
        "the right screen (a game's inventory, a window). Give it a title (short, e.g. \"Pick the 3 items to destroy\"), a " +
        "description (one or two short sentences: what you are making and why) and the steps (one short instruction each, " +
        "in order; PuppyMacro numbers them). For PuppyMacro's own screens, take the steps from the user guide (get_help). " +
        "For each point, also give what it is (label, or labels for pick_points): it is shown next to the cursor while the " +
        "user picks it. Write the title, description, steps and labels in the language the user writes to you in.\n" +
        "- place_floating_button gives a Toggle loop or macro a floating button for overlay mode and opens the screen where " +
        "the user places it, with your title, description and steps. open_item opens an item in PuppyMacro's editor, for the " +
        "user to look at or change it themselves.\n" +
        "- Loops, macros and remaps are shared as .puppyitems files (JSON text). export_items gives a file's text, for the user " +
        "to save or send; import_items adds the items of a file's text the user gives you (new Ids, disabled, nothing replaced). " +
        "When there are several items, ask the user which ones (or all) first. open_import_window and open_export_window show " +
        "PuppyMacro's own Import and Export windows instead: offer them when the user would rather choose there.\n" +
        "- A request that waits for the user answers after about 40 seconds at most. If the user has not answered yet, the " +
        "answer says so with a request_id: call wait_for_user with it (again if needed) until the user answers. Only one " +
        "such request at a time.\n\n" +
        "Creating a new loop or macro (not when changing one):\n" +
        "- Before building it, ask only the questions that matter for this item and that the user's words leave open, " +
        "for example how fast it should go, or which keys or items it covers. Skip questions that do not apply: a macro " +
        "that sells the inventory once needs no repeat count. Use sensible defaults for the rest, and say which.\n" +
        "- When you have no more questions, and before any pick or save, write in the chat the exact steps the loop or " +
        "macro will play, in order, one per line: each key or click and what it does there (\"1. Click the first item: " +
        "selects it\", \"2. Press Space: destroys it\"). Ask the user to confirm them, and change them until they do. Only " +
        "then ask for the points, in that same order, and save it.\n" +
        "- Once it is saved, offer in one short message only the extras it does not have yet and that make sense for it: " +
        "a hotkey, the overlay (Show in overlay) and a floating button (place_floating_button; Toggle items only), a sound " +
        "when it starts and stops, a specific app (if you already set AppExe, do not offer it). Do what the user picks.\n\n" +
        "Not available: starting, stopping or testing loops and macros (only stop_all, which stops everything). Their keys " +
        "and clicks would go to the window in front, which is usually this chat. Ask the user to start them in " +
        "PuppyMacro: the Play button on the item's card, its hotkey, or overlay mode. A running item that you change stops.\n\n" +
        "Formats: items are JSON exactly as PuppyMacro saves them (get_schema gives the JSON Schema, get_item real " +
        "examples). Leave out the Id of a new item. Keys are Windows virtual-key codes (A = 65, 0 = 48, F1 = 112, " +
        "Enter = 13, Space = 32; mouse: 1 left, 2 right, 4 middle, 5 and 6 side buttons). Points are physical screen " +
        "pixels. For how PuppyMacro works, read the user guide with get_help.";

    /// <summary>In the descriptions of the tools that make a new loop or macro: AI apps may not pass <see cref="Instructions"/> to the model.</summary>
    private const string NewItemRule =
        " For a NEW loop or macro, before calling this: ask only the questions that matter for it and that the user's words " +
        "leave open (how fast, which keys or items; none that do not apply), then write in the chat the exact steps it will " +
        "play, in order, one per line, each key or click and what it does there, and wait for the user's OK. Only then pick " +
        "the points (in that order) and save. A change to an existing item needs no such confirmation.";

    private const string AfterNewItem =
        " After saving a NEW one, offer in one short message only the extras it does not have yet and that fit it: a hotkey, " +
        "Show in overlay and a floating button (place_floating_button, Toggle only), a sound, a specific app (not when AppExe " +
        "is set). Then tell the user to try it: you can't start it.";

    private const string UserWindow =
        " Write every text shown to the user in the language the user writes to you in. If the user takes long, the answer " +
        "is \"waiting\" with a request_id: call wait_for_user with it until the user answers.";

    private const string RewriteRule =
        " To change an existing item, use patch_item or update_actions instead: they send only what changes (rewriting a big " +
        "macro here takes minutes).";

    private const string NoStart =
        "Starting and testing loops and macros is not available to AI apps: their keys and clicks would go to the window in " +
        "front, usually your chat. Ask the user to start it in PuppyMacro (the Play button on its card, its hotkey, or overlay mode).";

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
        All = Build();
    }

    public IReadOnlyList<AiTool> All { get; }

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
            return AiToolResult.Error("AI access is off in PuppyMacro. Ask the user to turn on Allow AI access on PuppyMacro's AI page.");
        }
        try
        {
            return await tool.Run(args, app, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return AiToolResult.Error(ex.Message);
        }
    }

    // ================= Tool list =================

    private List<AiTool> Build()
    {
        const string none = """{"type":"object","properties":{},"additionalProperties":false}""";
        const string idOnly = """{"type":"object","properties":{"id":{"type":"string","description":"The Id of a loop, macro or remap (get_overview lists them)."}},"required":["id"],"additionalProperties":false}""";
        const string asking = """
            "title":{"type":"string","description":"Short title shown big, e.g. \"Pick the 3 items to destroy\"."},
            "description":{"type":"string","description":"One or two short sentences: what you are making together and why the user does this step."},
            "steps":{"type":"array","items":{"type":"string"},"minItems":1,"description":"What to do, in order, one short step each (shown numbered), e.g. [\"Open the game's inventory\", \"Press Enter\", \"Click the 3 items, in order\"]. For PuppyMacro's own screens take them from the user guide (get_help)."}
            """;
        const string askingRequired = "\"title\",\"description\",\"steps\"";
        const string confirmed = """
            "confirmed_steps":{"type":"array","items":{"type":"string"},"minItems":1,"description":"The steps the loop or macro will play, one per item, each key or click and what it does: for a NEW item, exactly as you wrote them in the chat and the user confirmed (do not call this tool before); for a change to an existing one, the steps as they will be after it."},
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
                "Lists the user's loops, macros and remaps (Id, name, hotkey, app, enabled, running) and the global hotkeys.",
                none, true, false, (_, app, _) => Read(app, Phrase.Of("Ai_ReadOverview"), Overview)),
            new("get_item",
                "Gives one loop, macro or remap as compact JSON: one line, and the fields that have their default value left out " +
                "(get_schema gives the defaults). save_* takes it as it is; patch_item's paths point into it.",
                idOnly, true, false, (a, app, _) => GetItem(a, app)),
            new("get_schema",
                "Gives the JSON Schema, with descriptions and defaults, of a loop, a macro or a remap: every field and allowed value.",
                """{"type":"object","properties":{"kind":{"type":"string","enum":["loop","macro","remap"]}},"required":["kind"],"additionalProperties":false}""",
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
            new("get_status",
                "Tells what is running, whether overlay mode is on, and whether a request is waiting for the user.",
                none, true, false, (_, app, _) => Read(app, null, Status)),
            new("list_apps",
                "Lists the .exe names of the apps with a window open, for an item that works only in one app (AppExe).",
                none, true, false, (_, app, _) => Read(app, Phrase.Of("Ai_ListedApps"), () => JsonSerializer.Serialize(_host.RunningApps(), AnswerOptions))),
            new("get_help",
                "Reads PuppyMacro's user guide. Without a topic: the list of topics.",
                """{"type":"object","properties":{"topic":{"type":"string","description":"A topic from the list, e.g. \"macros\"."}},"additionalProperties":false}""",
                true, false, (a, app, _) => Task.FromResult(Help(a, app))),

            new("save_loop", "Creates a loop, or rewrites the one with this id. Saved at once (kept in its History)." + RewriteRule + NewItemRule + AfterNewItem,
                Save("loop"), false, false, (a, app, c) => SaveItem<LoopDefinition>(a, app, "loop", c)),
            new("save_macro", "Creates a macro, or rewrites the one with this id. Saved at once (kept in its History)." + RewriteRule + NewItemRule + AfterNewItem,
                Save("macro"), false, false, (a, app, c) => SaveItem<MacroDefinition>(a, app, "macro", c)),
            new("save_remap", "Creates a remap, or rewrites the one with this id. Saved at once (kept in its History)." + RewriteRule,
                Save("remap"), false, false, (a, app, c) => SaveItem<RemapDefinition>(a, app, "remap", c)),
            new("patch_item",
                "Changes part of a loop, macro or remap with a JSON Patch (RFC 6902): only what changes, so it is fast even for a " +
                "big macro. Use it for changes to an existing item (the same change on many actions: update_actions). Checked and saved like save_* (kept in " +
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
                """, false, false, (a, app, c) => PatchItem(a, app, c)),
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
                """, false, false, (a, app, c) => UpdateActions(a, app, c)),
            new("delete_item", "Deletes a loop, macro or remap (the user may be asked to confirm; it can be restored in PuppyMacro).",
                idOnly, false, true, (a, app, c) => Delete(a, app, c)),
            new("set_enabled", "Enables or disables a loop, macro or remap.",
                """{"type":"object","properties":{"id":{"type":"string"},"enabled":{"type":"boolean"}},"required":["id","enabled"],"additionalProperties":false}""",
                false, false, (a, app, c) => SetEnabled(a, app, c)),
            new("update_settings", "Changes settings. Give only the values to change, in get_settings' format.",
                """{"type":"object","properties":{"settings":{"type":"object","description":"Values to change, e.g. {\"Theme\": \"Dark\", \"StopAllHotkey\": {\"Vk\": 123}}."}},"required":["settings"],"additionalProperties":false}""",
                false, false, (a, app, c) => UpdateSettings(a, app, c)),
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

            new("pick_point", "Shows the user your title, description and steps, waits until they are ready, then they click a point on the screen: its position in physical pixels (X, Y)." + NewItemRule + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + confirmed + "\"label\":{\"type\":\"string\",\"description\":\"What this point is, shown next to the cursor while the user picks, e.g. \\\"The Salvage button\\\".\"}," + asking + "},\"required\":[\"confirmed_steps\",\"label\"," + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickPoints(a, app, single: true, c)),
            new("pick_points", "Like pick_point for several points in a row (for example the 3 items to click): the user clicks them in order, the order the loop or macro uses them." + NewItemRule + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + confirmed + "\"count\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":" + MaxPoints + "},\"labels\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":2,\"maxItems\":" + MaxPoints + ",\"description\":\"One per point (as many as count), in order: what it is, shown next to the cursor while the user picks it, e.g. [\\\"The first item\\\", \\\"The second item\\\", \\\"The Confirm button\\\"].\"}," + asking + "},\"required\":[\"confirmed_steps\",\"count\",\"labels\"," + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickPoints(a, app, single: false, c)),
            new("pick_app", "Shows the user your title, description and steps, waits until they are ready, then they click a window of an app: its .exe name, for AppExe." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickApp(a, app, c)),
            new("capture_hotkey", "Shows the user your title, description and steps and waits until they press a key, mouse button or combination: it as a HotkeyBinding." + UserWindow,
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[" + askingRequired + "],\"additionalProperties\":false}",
                true, false, (a, app, c) => CaptureKeys(a, app, c)),
            new("place_floating_button",
                "Gives a loop or macro (Toggle mode only) a floating button for overlay mode, then shows the user your title, description and " +
                "steps and opens PuppyMacro's placement screen, where the user drags the button and presses Enter (Esc cancels). Saved at once." + UserWindow,
                "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"The Id of the loop or macro.\"}," + asking + "},\"required\":[\"id\"," + askingRequired + "],\"additionalProperties\":false}",
                false, false, (a, app, c) => PlaceFloatingButton(a, app, c)),
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

    private string Overview()
    {
        AppSettings s = _host.Settings;
        var overview = new
        {
            Loops = s.Loops.Select(l => new
            {
                l.Id, l.Name, l.Enabled, Running = _host.IsRunning(l.Id), Hotkey = Keys(l.Hotkey), l.Mode,
                App = l.AppExe, Actions = l.Actions.Count,
            }),
            Macros = _host.Macros.Macros.Select(m => new
            {
                m.Id, m.Name, m.Enabled, Running = _host.IsRunning(m.Id), Hotkey = Keys(m.Hotkey), m.Mode,
                App = m.AppExe, Actions = m.Actions.Count,
            }),
            Remaps = s.Remaps.Select(r => new
            {
                r.Id, Source = Keys(r.Source), Target = Keys(r.Target), App = r.AppExe, r.Enabled, r.Note,
            }),
            GlobalHotkeys = new
            {
                OpenMain = Keys(s.OpenMainHotkey), OverlayMode = Keys(s.OverlayModeHotkey), StopAll = Keys(s.StopAllHotkey),
                Record = Keys(s.RecordHotkey),
            },
        };
        return JsonSerializer.Serialize(overview, AnswerOptions);
    }

    private static string? Keys(HotkeyBinding? binding) => binding is { IsSet: true } ? KeyNames.Format(binding) : null;

    /// <summary>Keys named in the language of the moment, or "none".</summary>
    private static PhraseArg KeyArg(HotkeyBinding? binding) =>
        binding is { IsSet: true } ? PhraseArg.Of(binding.Clone()) : Phrase.Of("History_None");

    private string Status()
    {
        var status = new
        {
            Running = _host.Settings.Loops.Where(l => _host.IsRunning(l.Id)).Select(l => l.Name)
                .Concat(_host.Macros.Macros.Where(m => _host.IsRunning(m.Id)).Select(m => m.Name)).ToList(),
            OverlayMode = _host.OverlayModeActive,
            WaitingForUser = WaitingId(),
            UserIsEditing = _host.UserIsEditing,
        };
        return JsonSerializer.Serialize(status, AnswerOptions);
    }

    private async Task<AiToolResult> GetItem(JsonElement args, string app)
    {
        Guid id = IdArg(args, "id");
        return await _host.Ui(() =>
        {
            object? item = Find(id);
            if (item == null)
                return Task.FromResult(AiToolResult.Error($"No loop, macro or remap has the Id {id}. get_overview lists them."));
            _log.Add(app, AiActivityKind.Read, About("Ai_Read", item));
            return Task.FromResult(new AiToolResult(Loc.English(() => AiJson.Compact(item))));
        }).ConfigureAwait(false);
    }

    private static AiToolResult Schema(JsonElement args) => Str(args, "kind") switch
    {
        "loop" => new AiToolResult(WithoutId(CodeSchema.ForLoop(Guid.Empty, withDefaults: true))),
        "macro" => new AiToolResult(WithoutId(CodeSchema.ForMacro(Guid.Empty, withDefaults: true))),
        "remap" => new AiToolResult(WithoutId(CodeSchema.ForRemap(Guid.Empty, withDefaults: true))),
        _ => AiToolResult.Error("kind must be loop, macro or remap."),
    };

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
        bool remap = Str(args, "for") == "remap";
        string? appExe = AppScope.Normalize(Str(args, "app"));
        bool hold = Str(args, "mode") == "Hold";
        Guid? ignore = Str(args, "ignore_id") is { Length: > 0 } text ? Guid.Parse(text) : null;
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
        Guid? editId = Str(args, "id") is { Length: > 0 } idText ? Guid.Parse(idText) : null;
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);

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
        if (before != null && changes.Count == 0)
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
            _host.Save(parsed, app);
            _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", json, id, kind, before);
            return new AiToolResult((before == null ? $"Saved the new {kind}. Its Id: {id}." : $"Saved the changes to the {kind}:\n" + ChangeList(changes))
                + (wasRunning ? "\nIt was running: it stopped." : ""));
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
            return (null, $"No {typeof(T).Name.Replace("Definition", "").ToLowerInvariant()} has the Id {editId}.", null);
        Guid itemId = editId ?? Guid.NewGuid();

        JsonObject node = JsonNode.Parse(item.GetRawText()) as JsonObject ?? new JsonObject();
        node["Id"] = itemId.ToString();
        string text = node.ToJsonString(CodeJson.Options);

        List<CodeProblem> problems;
        object? parsed = typeof(T) == typeof(LoopDefinition)
            ? LoopJson.Parse(text, itemId, (b, a) => HotkeyConflicts.Find(b, _host.Settings, _host.Macros, itemId, appExe: a), out problems)
            : typeof(T) == typeof(MacroDefinition)
                ? MacroJson.Parse(text, itemId, (b, a) => HotkeyConflicts.Find(b, _host.Settings, _host.Macros, itemId, appExe: a), out problems)
                : RemapJson.Parse(text, itemId, (b, a) => HotkeyConflicts.RemapClash(b, a, _host.Settings, _host.Macros, itemId), out problems);
        return parsed is T ok
            ? (ok, null, existing == null ? null : Serialize(existing))
            : (null, string.Join("\n", problems.Select(p => "- " + p.Message)), null);
    }

    /// <summary>The patch on the item's whole JSON (every field there), then save_*'s checks, permissions and History.</summary>
    private async Task<AiToolResult> PatchItem(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        if (!args.TryGetProperty("patch", out JsonElement patch))
            throw new FormatException("Give the \"patch\": the operations that change the item.");
        (string? kind, string? json) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Kind(item), (string?)Serialize(item))
            : (null, null))).ConfigureAwait(false);
        if (kind == null || json == null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {id}. get_overview lists them.");

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
        (string? kind, string? json) = await _host.Ui(() => Task.FromResult(Find(id) is { } item and (LoopDefinition or MacroDefinition)
            ? ((string?)Kind(item), (string?)Serialize(item))
            : (null, null))).ConfigureAwait(false);
        if (kind == null || json == null)
            return AiToolResult.Error($"No loop or macro has the Id {id}. get_overview lists them.");

        (JsonNode changed, _) = AiJson.SetWhere(JsonNode.Parse(json)!, where, set, Str(args, "group"));
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
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? kind, string? before, object? found) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Loc.English(() => Describe(item)), (string?)Kind(item), (string?)Serialize(item), (object?)item)
            : (null, null, null, null))).ConfigureAwait(false);
        if (what == null || found == null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {id}.");

        Phrase label = About("Ai_Deleted", found);
        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busy)
                return busy;
            if (Find(id) == null)
                return AiToolResult.Error($"The {kind} is already gone.");
            _host.Delete(id, app);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied, "", null, id, kind, before);
            return new AiToolResult($"Deleted {what}. The user can restore it in PuppyMacro.");
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
        bool enabled = args.TryGetProperty("enabled", out JsonElement e) && e.ValueKind == JsonValueKind.True;
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? kind, string? before, bool already, object? found) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Loc.English(() => Describe(item)), (string?)Kind(item), (string?)Serialize(item), IsEnabled(item) == enabled, (object?)item)
            : (null, null, null, false, null))).ConfigureAwait(false);
        if (what == null || found == null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {id}.");
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
        AiSettingsValues wanted = JsonSerializer.Deserialize<AiSettingsValues>(json.GetRawText(), ReadOptions)
            ?? throw new JsonException("settings must be an object.");
        if (await Refusal().ConfigureAwait(false) is string busy)
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
            OpenMainHotkey = wanted.OpenMainHotkey != null ? now.OpenMainHotkey : null,
            ShowOverlayPanel = wanted.ShowOverlayPanel != null ? now.ShowOverlayPanel : null,
            ClickItemsInPanel = wanted.ClickItemsInPanel != null ? now.ClickItemsInPanel : null,
            OverlayPanelOpacity = wanted.OverlayPanelOpacity != null ? now.OverlayPanelOpacity : null,
            SoundVolume = wanted.SoundVolume != null ? now.SoundVolume : null,
        };
    }

    /// <summary>
    /// Restores settings an AI app changed (<see cref="AiActivity.Before"/>): checked as a change would be (a global hotkey
    /// may clash with what was made since). Null when done, otherwise why not. On the UI thread.
    /// </summary>
    public string? RestoreSettings(AiSettingsValues values)
    {
        // Shown to the user (Restore on the AI page): in the user's language.
        (_, string? problem) = SettingChanges(values);
        if (problem != null)
            return string.Format(CultureInfo.CurrentCulture, Strings.Ai_CantRestoreSettings, problem);
        _host.ApplySettings(values);
        return null;
    }

    /// <summary>On the UI thread, while a request starts: the user opened a PuppyMacro window meanwhile.</summary>
    private AiToolResult? BusyNow() => _host.UserIsEditing
        ? AiToolResult.Error("The user has a PuppyMacro window open (an editor or a dialog). Ask them to close it, then try again.")
        : null;

    /// <summary>On the UI thread: what would change, or why it can't.</summary>
    private (List<AiSettingChange> Changes, string? Problem) SettingChanges(AiSettingsValues wanted)
    {
        AppSettings s = _host.Settings;
        var changes = new List<AiSettingChange>();
        static PhraseArg OnOff(bool value) => Phrase.Of(value ? "History_On" : "History_Off");
        static PhraseArg Percent(int value) => Phrase.Of("Ai_Percent", value.ToString(CultureInfo.InvariantCulture));
        static PhraseArg Theme(AppTheme value) => Phrase.Of($"Theme_{value}");

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
    private async Task<AiToolResult> PickPoints(JsonElement args, string app, bool single, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        if (!(args.TryGetProperty("confirmed_steps", out JsonElement plan) && plan.ValueKind == JsonValueKind.Array
              && plan.EnumerateArray().Any(step => step.ValueKind == JsonValueKind.String && step.GetString()!.Trim().Length > 0)))
            throw new FormatException("Give \"confirmed_steps\": the steps the loop or macro will play. For a new one, write them " +
                "in the chat first and wait for the user's OK.");
        List<string> labels = single
            ? new List<string> { (Str(args, "label") ?? "").Trim() }
            : args.TryGetProperty("labels", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "").ToList()
                : new List<string>();
        if (labels.Any(l => l.Length == 0))
            throw new FormatException(single ? "Give a \"label\": what the point is." : "Every one of the \"labels\" must say what its point is.");
        int points = single ? 1 : args.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int n) ? n : 0;
        if (!single && (points < 2 || points > MaxPoints))
            return AiToolResult.Error($"count must be from 2 to {MaxPoints} (for one point, use pick_point).");
        if (labels.Count != points)
            return AiToolResult.Error($"Give one label per point: {points} points, {labels.Count} labels.");
        if (await Refusal().ConfigureAwait(false) is string busy)
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
            string text = points == 1 && picked.Count == 1
                ? JsonSerializer.Serialize(new { X = picked[0].X, Y = picked[0].Y })
                : JsonSerializer.Serialize(picked.Select(p => new { X = p.X, Y = p.Y }));
            Log(app, AiActivityKind.Interaction, label, answer, answer == AiAnswer.Done ? string.Join("; ", picked.Select(p => $"{p.X}, {p.Y}")) : "");
            return Answered(answer, text);
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> PickApp(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        if (await Refusal().ConfigureAwait(false) is string busy)
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

    /// <summary>No separate ready step: the key window itself shows the title, description and steps and waits.</summary>
    private async Task<AiToolResult> CaptureKeys(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        if (await Refusal().ConfigureAwait(false) is string busy)
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
        if (await Refusal().ConfigureAwait(false) is string busy)
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
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? problem, object? found) = await _host.Ui(() => Task.FromResult(Find(id) switch
        {
            null => (null, $"No loop, macro or remap has the Id {id}.", null),
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
        if (ids.FirstOrDefault(id => Find(id) == null) is var unknown && unknown != Guid.Empty)
            return (new List<object>(), unknown.ToString());
        return (all.Where(i => ids.Contains(Id(i))).ToList(), null);
    }

    private static List<Guid>? IdsArg(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("ids", out JsonElement list) || list.ValueKind == JsonValueKind.Null)
            return null;
        if (list.ValueKind != JsonValueKind.Array)
            throw new FormatException("\"ids\" must be a list of Ids (get_overview lists them).");
        return list.EnumerateArray().Select(e => Guid.TryParse(e.GetString(), out Guid id) ? id
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
            return Task.FromResult(missing != null ? (null, missing, 0) : ((string?)ItemFile.Write(items, ItemFile.AppVersion, _host.Macros.Images), (string?)null, items.Count));
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
        if (args.TryGetProperty("only", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            only = list.EnumerateArray().Select(e => e.TryGetInt32(out int i) ? i : throw new FormatException("\"only\" must be a list of indexes, from 0.")).ToList();
        if (await Refusal().ConfigureAwait(false) is string busy)
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
        if (chosen.Count == 0)
            return AiToolResult.Error("Nothing was imported." + skipped);

        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busyNow)
                return busyNow;
            // Read again: meanwhile the user may have taken a name or a hotkey.
            ItemFileContent now = Loc.English(() => ItemFile.Read(text, _host.Settings, _host.Macros, ItemFile.AppVersion, only));
            ItemFile.AddImages(now, _host.Macros);
            var lines = new List<string>();
            foreach (ItemFileEntry entry in now.Entries.Where(e => e.Selected))
            {
                object item = entry.Item!;
                _host.Save(item, app);
                _log.Add(app, AiActivityKind.Proposal, About("Ai_Imported", item), AiActivityResult.Saved, "", Serialize(item), Id(item), Kind(item));
                lines.Add($"- {Capital(Loc.English(() => Describe(item)))}, Id {Id(item)}" + string.Concat(entry.Notes.Select(n => " " + n)));
            }
            return new AiToolResult($"Imported {lines.Count} item{(lines.Count == 1 ? "" : "s")}, all disabled: the user turns them on " +
                                    $"(set_enabled, or the switch on their card).\n{string.Join("\n", lines)}{skipped}");
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);

        return await StartForUser(app, async () =>
        {
            // The user's window: the file read again in the user's language (its notes), each item named as the cards do.
            List<string> shown = ItemFile.Read(text, _host.Settings, _host.Macros, ItemFile.AppVersion, only).Entries
                .Where(e => e.Selected)
                .Select(e => About("Ai_Item", e.Item!).ToString() + string.Concat(e.Notes.Select(n => " " + n))).ToList();
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
        if (await Refusal().ConfigureAwait(false) is string busy)
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
        if (await Refusal().ConfigureAwait(false) is string busy)
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
    private async Task<string?> Refusal()
    {
        if (WaitingId() is string waiting)
            return $"Another request is waiting for the user (request_id \"{waiting}\"). Call wait_for_user with it first.";
        return await _host.Ui(() => Task.FromResult<string?>(_host.UserIsEditing
                ? "The user has a PuppyMacro window open (an editor or a dialog). Ask them to close it, then try again."
                : null))
            .ConfigureAwait(false);
    }

    private string? WaitingId()
    {
        lock (_sync)
            return _pending is { Answer.IsCompleted: false } p ? p.Id : null;
    }

    /// <summary>Opens the user's window (on the UI thread) and waits for the answer, at most <see cref="WaitStep"/>.</summary>
    private async Task<AiToolResult> StartForUser(string app, Func<Task<AiToolResult>> ask, CancellationToken cancel)
    {
        Pending pending;
        lock (_sync)
        {
            if (_pending is { Answer.IsCompleted: false } other)
                return AiToolResult.Error($"Another request is waiting for the user (request_id \"{other.Id}\"). Call wait_for_user with it first.");
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
        string id = Str(args, "request_id") ?? "";
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

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid IdArg(JsonElement args, string name) =>
        Guid.TryParse(Str(args, name), out Guid id) ? id : throw new FormatException($"\"{name}\" must be an Id (get_overview lists them).");

    private static T Arg<T>(JsonElement args, string name) where T : class =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value)
            ? JsonSerializer.Deserialize<T>(value.GetRawText(), ReadOptions) ?? throw new JsonException($"\"{name}\" is empty.")
            : throw new JsonException($"\"{name}\" is missing.");

    /// <summary>The title (shown big), the description (what and why) and the numbered steps the AI writes for the user.</summary>
    private static AiAsk Asking(JsonElement args)
    {
        string title = (Str(args, "title") ?? "").Trim();
        string description = (Str(args, "description") ?? "").Trim();
        if (title.Length == 0)
            throw new FormatException("Give a short \"title\": it is shown big to the user.");
        if (description.Length == 0)
            throw new FormatException("Give a \"description\": one or two short sentences on what you are making and why.");
        var steps = new List<string>();
        if (args.TryGetProperty("steps", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            foreach (JsonElement step in list.EnumerateArray())
                if (step.ValueKind == JsonValueKind.String && step.GetString()!.Trim() is { Length: > 0 } text)
                    steps.Add(text);
        if (steps.Count == 0)
            throw new FormatException("Give the \"steps\": what the user does, in order, one short step each.");
        return new AiAsk(title, description, steps);
    }
}
