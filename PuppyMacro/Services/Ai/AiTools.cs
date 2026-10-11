using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PuppyMacro.Models;

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
        "- Read tools (get_overview, get_item, get_schema, get_settings, check_hotkey, get_status, list_apps, get_help) answer at once.\n" +
        "- save_loop, save_macro and save_remap create or change an item; delete_item, set_enabled and update_settings do " +
        "what they say. They are saved at once and kept in the item's History in PuppyMacro, where the user can restore " +
        "any version. Some may wait for the user's OK in PuppyMacro, as the user chose in PuppyMacro's AI settings: the " +
        "answer then says whether the user confirmed or cancelled. After a change, tell the user to try it.\n" +
        "- pick_point, pick_points, pick_app and capture_hotkey ask the user to click points on the screen, click a window " +
        "of an app, or press keys. Use them instead of guessing coordinates, app names or key codes. First explain in the " +
        "chat what the user will have to do; then call the tool. PuppyMacro shows your message and context in a window on " +
        "top of everything and waits until the user is ready (Enter or Start) before the user picks, so they can first open " +
        "the right screen (a game's inventory, a window). Write the message as one short instruction (\"Click the 3 " +
        "items to destroy, in order\") and the context as 1 to 3 sentences: what you are making, why you need this, and " +
        "what to open first.\n" +
        "- A request that waits for the user answers after about 40 seconds at most. If the user has not answered yet, the " +
        "answer says so with a request_id: call wait_for_user with it (again if needed) until the user answers. Only one " +
        "such request at a time.\n\n" +
        "Not available: starting, stopping or testing loops and macros (only stop_all, which stops everything). Their keys " +
        "and clicks would go to the window in front, which is usually this chat. Ask the user to start them in " +
        "PuppyMacro: the Play button on the item's card, its hotkey, or overlay mode. A running item that you change stops.\n\n" +
        "Formats: items are JSON exactly as PuppyMacro saves them (get_schema gives the JSON Schema, get_item real " +
        "examples). Leave out the Id of a new item. Keys are Windows virtual-key codes (A = 65, 0 = 48, F1 = 112, " +
        "Enter = 13, Space = 32; mouse: 1 left, 2 right, 4 middle, 5 and 6 side buttons). Points are physical screen " +
        "pixels. For how PuppyMacro works, read the user guide with get_help.";

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
            _log.Add(app, AiActivityKind.Connection, $"Called {name} while AI access was off", AiActivityResult.Refused);
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
            "message":{"type":"string","description":"What the user should do, one short instruction shown big, e.g. \"Click the Send button\"."},
            "context":{"type":"string","description":"1 to 3 sentences shown under it: what you are making, why you need this, and what to open first."}
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
                none, true, false, (_, app, _) => Read(app, "Read your loops, macros and remaps", Overview)),
            new("get_item",
                "Gives one loop, macro or remap as JSON, exactly as PuppyMacro saves it (the format save_* takes).",
                idOnly, true, false, (a, app, _) => GetItem(a, app)),
            new("get_schema",
                "Gives the JSON Schema, with descriptions, of a loop, a macro or a remap: every field and allowed value.",
                """{"type":"object","properties":{"kind":{"type":"string","enum":["loop","macro","remap"]}},"required":["kind"],"additionalProperties":false}""",
                true, false, (a, app, _) => Task.FromResult(Schema(a))),
            new("get_settings",
                "Gives the settings an AI app can change with update_settings (theme, global hotkeys, overlay panel, sound volume).",
                none, true, false, (_, app, _) => Read(app, "Read your settings",
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
                none, true, false, (_, app, _) => Read(app, "Listed the open apps", () => JsonSerializer.Serialize(_host.RunningApps(), AnswerOptions))),
            new("get_help",
                "Reads PuppyMacro's user guide. Without a topic: the list of topics.",
                """{"type":"object","properties":{"topic":{"type":"string","description":"A topic from the list, e.g. \"macros\"."}},"additionalProperties":false}""",
                true, false, (a, app, _) => Task.FromResult(Help(a, app))),

            new("save_loop", "Creates a loop, or changes the one with this id. Saved at once (kept in its History).",
                Save("loop"), false, false, (a, app, c) => SaveItem<LoopDefinition>(a, app, "loop", c)),
            new("save_macro", "Creates a macro, or changes the one with this id. Saved at once (kept in its History).",
                Save("macro"), false, false, (a, app, c) => SaveItem<MacroDefinition>(a, app, "macro", c)),
            new("save_remap", "Creates a remap, or changes the one with this id. Saved at once (kept in its History).",
                Save("remap"), false, false, (a, app, c) => SaveItem<RemapDefinition>(a, app, "remap", c)),
            new("delete_item", "Deletes a loop, macro or remap (the user may be asked to confirm; it can be restored in PuppyMacro).",
                idOnly, false, true, (a, app, c) => Delete(a, app, c)),
            new("set_enabled", "Enables or disables a loop, macro or remap.",
                """{"type":"object","properties":{"id":{"type":"string"},"enabled":{"type":"boolean"}},"required":["id","enabled"],"additionalProperties":false}""",
                false, false, (a, app, c) => SetEnabled(a, app, c)),
            new("update_settings", "Changes settings. Give only the values to change, in get_settings' format.",
                """{"type":"object","properties":{"settings":{"type":"object","description":"Values to change, e.g. {\"Theme\": \"Dark\", \"StopAllHotkey\": {\"Vk\": 123}}."}},"required":["settings"],"additionalProperties":false}""",
                false, false, (a, app, c) => UpdateSettings(a, app, c)),

            new("pick_point", "Shows the user your message and context, waits until they are ready, then they click a point on the screen: its position in physical pixels (X, Y).",
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[\"message\",\"context\"],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickPoints(a, app, 1, c)),
            new("pick_points", "Like pick_point for several points in a row (for example the 3 items to click): the user clicks them in order.",
                "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":" + MaxPoints + "}," + asking + "},\"required\":[\"count\",\"message\",\"context\"],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickPoints(a, app, null, c)),
            new("pick_app", "Shows the user your message and context, waits until they are ready, then they click a window of an app: its .exe name, for AppExe.",
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[\"message\",\"context\"],\"additionalProperties\":false}",
                true, false, (a, app, c) => PickApp(a, app, c)),
            new("capture_hotkey", "Shows the user your message and context and waits until they press a key, mouse button or combination: it as a HotkeyBinding.",
                "{\"type\":\"object\",\"properties\":{" + asking + "},\"required\":[\"message\",\"context\"],\"additionalProperties\":false}",
                true, false, (a, app, c) => CaptureKeys(a, app, c)),
            new("wait_for_user", "Waits again for the user's answer to a request that answered \"waiting\" with this request_id.",
                """{"type":"object","properties":{"request_id":{"type":"string"}},"required":["request_id"],"additionalProperties":false}""",
                true, false, (a, app, c) => WaitAgain(a, app, c)),

            new("stop_all", "Stops every running loop and macro, like PuppyMacro's Stop all. " + NoStart,
                none, false, false, (_, app, _) => StopAll(app)),
        };
    }

    // ================= Reads =================

    private async Task<AiToolResult> Read(string app, string? logText, Func<string> read)
    {
        string text = await _host.Ui(() => Task.FromResult(read())).ConfigureAwait(false);
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
                StopAll = Keys(s.StopAllHotkey), OverlayMode = Keys(s.OverlayModeHotkey), Record = Keys(s.RecordHotkey),
            },
        };
        return JsonSerializer.Serialize(overview, AnswerOptions);
    }

    private static string? Keys(HotkeyBinding? binding) => binding is { IsSet: true } ? KeyNames.Format(binding) : null;

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
            _log.Add(app, AiActivityKind.Read, $"Read {Describe(item)}");
            return Task.FromResult(new AiToolResult(Serialize(item)));
        }).ConfigureAwait(false);
    }

    private static AiToolResult Schema(JsonElement args) => Str(args, "kind") switch
    {
        "loop" => new AiToolResult(WithoutId(CodeSchema.ForLoop(Guid.Empty))),
        "macro" => new AiToolResult(WithoutId(CodeSchema.ForMacro(Guid.Empty))),
        "remap" => new AiToolResult(WithoutId(CodeSchema.ForRemap(Guid.Empty))),
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
        _log.Add(app, AiActivityKind.Read, $"Read the guide: {AiHelp.Sections.First(s => s.Id.Equals(topic.Trim(), StringComparison.OrdinalIgnoreCase)).Title}");
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

        string? problem = await _host.Ui(() => Task.FromResult(remap
            ? HotkeyConflicts.RemapClash(binding, appExe, _host.Settings, _host.Macros, ignore ?? Guid.Empty)
            : HotkeyRules.Problem(binding, hold) ?? HotkeyConflicts.Find(binding, _host.Settings, _host.Macros, ignore, appExe: appExe)))
            .ConfigureAwait(false);
        return new AiToolResult(problem == null ? $"{KeyNames.Format(binding)} is free." : $"{KeyNames.Format(binding)} can't be used: {problem}");
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
        (T? parsed, string? problems, string? before) = await _host.Ui(() => Task.FromResult(ParseItem<T>(item, editId)))
            .ConfigureAwait(false);
        if (parsed == null)
        {
            _log.Add(app, AiActivityKind.Proposal, $"{(editId == null ? "New" : "Change to")} {kind}", AiActivityResult.Error,
                problems ?? "", item.GetRawText());
            return AiToolResult.Error($"The {kind} has problems, nothing was saved:\n{problems}");
        }

        string json = Serialize(parsed);
        List<string> changes = before == null ? new List<string>() : ItemHistory.Changes(before, json);
        string label = before == null ? $"New {kind} {Title(parsed)}"
            : $"Changed {kind} {Title(parsed)}: {(changes.Count == 1 ? changes[0] : $"{changes.Count} changes")}";
        if (before != null && changes.Count == 0)
            return new AiToolResult($"Nothing to change: the {kind} is already like this.");
        Guid id = Id(parsed);

        AiToolResult Apply()
        {
            bool wasRunning = _host.IsRunning(id);
            _host.Save(parsed, app);
            _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", json, id, kind, before);
            return new AiToolResult((before == null ? $"Saved the new {kind}." : $"Saved the changes to the {kind}.")
                + (wasRunning ? " It was running: it stopped." : "") + $"\n{json}");
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);

        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, before == null ? $"Save this new {kind}?" : $"Save these changes to the {kind}?",
                before == null ? new List<string> { $"{Capital(kind)} {Title(parsed)}" } : changes, "Save");
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

    private async Task<AiToolResult> Delete(JsonElement args, string app, CancellationToken cancel)
    {
        Guid id = IdArg(args, "id");
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        (string? what, string? kind, string? before) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Describe(item), (string?)Kind(item), (string?)Serialize(item))
            : (null, null, null))).ConfigureAwait(false);
        if (what == null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {id}.");

        string label = $"Deleted {what}";
        AiToolResult Apply()
        {
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
            AiAnswer answer = await _host.Confirm(app, $"Delete this {kind}?", new[] { Capital(what) }, "Delete");
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
        (string? what, string? kind, string? before, bool already) = await _host.Ui(() => Task.FromResult(Find(id) is { } item
            ? ((string?)Describe(item), (string?)Kind(item), (string?)Serialize(item), IsEnabled(item) == enabled)
            : (null, null, null, false))).ConfigureAwait(false);
        if (what == null)
            return AiToolResult.Error($"No loop, macro or remap has the Id {id}.");
        if (already)
            return new AiToolResult($"{Capital(what)} is already {(enabled ? "enabled" : "disabled")}.");

        string verb = enabled ? "Enable" : "Disable";
        string label = $"{(enabled ? "Enabled" : "Disabled")} {what}";
        AiToolResult Apply()
        {
            _host.SetEnabled(id, enabled, app);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied, "", null, id, kind, before);
            return new AiToolResult($"{(enabled ? "Enabled" : "Disabled")} {what}.");
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, $"{verb} this {kind}?", new[] { Capital(what) }, verb);
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

        (List<AiSettingChange> changes, string? problem) = await _host.Ui(() => Task.FromResult(SettingChanges(wanted))).ConfigureAwait(false);
        if (problem != null)
            return AiToolResult.Error(problem);
        if (changes.Count == 0)
            return new AiToolResult("Nothing to change: the settings already have these values.");

        string label = "Changed settings: " + string.Join(", ", changes.Select(c => c.Label));
        AiToolResult Apply()
        {
            // Checked again: the user may have changed something meanwhile.
            (_, string? stillProblem) = SettingChanges(wanted);
            if (stillProblem != null)
            {
                _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Error, stillProblem);
                return AiToolResult.Error(stillProblem);
            }
            string before = JsonSerializer.Serialize(AiSettingsValues.From(_host.Settings), CodeJson.Options);
            _host.ApplySettings(wanted);
            _log.Add(app, AiActivityKind.Confirmation, label, AiActivityResult.Applied,
                string.Join("; ", changes.Select(c => $"{c.Label}: {c.From} → {c.To}")), null, null, "settings", before);
            return new AiToolResult("Settings changed: " + string.Join("; ", changes.Select(c => $"{c.Label} {c.To}")) + ".");
        }

        if (!_host.Settings.AiAskBeforeSettings)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);
        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, "Change these settings?",
                changes.Select(c => $"{c.Label}: {c.From} → {c.To}").ToList(), "Apply");
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Confirmation, label, answer, "");
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>On the UI thread: what would change, or why it can't.</summary>
    private (List<AiSettingChange> Changes, string? Problem) SettingChanges(AiSettingsValues wanted)
    {
        AppSettings s = _host.Settings;
        var changes = new List<AiSettingChange>();
        static string OnOff(bool value) => value ? "On" : "Off";

        if (wanted.Theme is AppTheme theme && theme != s.Theme)
            changes.Add(new("Theme", s.Theme.ToString(), theme.ToString()));
        if (wanted.CloseToTray is bool tray && tray != s.CloseToTray)
            changes.Add(new("Close to system tray", OnOff(s.CloseToTray), OnOff(tray)));
        if (wanted.ShowOverlayPanel is bool panel && panel != s.ShowOverlayPanel)
            changes.Add(new("Overlay panel", OnOff(s.ShowOverlayPanel), OnOff(panel)));
        if (wanted.ClickItemsInPanel is bool click && click != s.ClickItemsInPanel)
            changes.Add(new("Click items in the panel", OnOff(s.ClickItemsInPanel), OnOff(click)));
        if (wanted.OverlayPanelOpacity is int opacity)
        {
            if (opacity is < AppSettings.MinOpacity or > AppSettings.MaxOpacity)
                return (changes, $"OverlayPanelOpacity must be between {AppSettings.MinOpacity} and {AppSettings.MaxOpacity}.");
            if (opacity != s.OverlayPanelOpacity)
                changes.Add(new("Overlay panel opacity", $"{s.OverlayPanelOpacity}%", $"{opacity}%"));
        }
        if (wanted.SoundVolume is int volume)
        {
            if (volume is < 0 or > 100)
                return (changes, "SoundVolume must be between 0 and 100.");
            if (volume != s.SoundVolume)
                changes.Add(new("Sound volume", $"{s.SoundVolume}%", $"{volume}%"));
        }

        var newHotkeys = new List<(string Label, HotkeyBinding Value)>();
        foreach (var (label, which, value, current) in new[]
                 {
                     ("Stop all hotkey", "StopAll", wanted.StopAllHotkey, s.StopAllHotkey),
                     ("Overlay mode hotkey", "OverlayMode", wanted.OverlayModeHotkey, s.OverlayModeHotkey),
                     ("Record hotkey", "Record", wanted.RecordHotkey, s.RecordHotkey),
                 })
        {
            if (value == null || value.SameAs(current))
                continue;
            if (!value.IsSet)
                return (changes, $"{label}: set Vk, the key's virtual-key code.");
            if ((HotkeyRules.Problem(value, hold: false) ?? HotkeyConflicts.Find(value, s, _host.Macros, ignoreGlobal: which)) is string problem)
                return (changes, $"{label}: {problem}");
            // Two of the new global hotkeys must differ too (each was checked only against the current ones).
            if (newHotkeys.FirstOrDefault(n => n.Value.Overlaps(value)) is { Label: not null } same)
                return (changes, $"{label}: {KeyNames.Format(value)} would also be the {same.Label}.");
            newHotkeys.Add((label, value));
            changes.Add(new(label, Keys(current) ?? "None", KeyNames.Format(value)));
        }
        return (changes, null);
    }

    // ================= Interactions =================

    /// <param name="count">1 for pick_point; null: the "count" argument (pick_points).</param>
    private async Task<AiToolResult> PickPoints(JsonElement args, string app, int? count, CancellationToken cancel)
    {
        var (message, context) = Asking(args);
        int points = count ?? (args.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int n) ? n : 0);
        if (count == null && (points < 2 || points > MaxPoints))
            return AiToolResult.Error($"count must be from 2 to {MaxPoints} (for one point, use pick_point).");
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        string label = (points == 1 ? "Asked for a point: " : $"Asked for {points} points: ") + message;
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, message, context);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            var (answer, picked) = await _host.PickPoints(app, message, points);
            string text = points == 1 && picked.Count == 1
                ? JsonSerializer.Serialize(new { X = picked[0].X, Y = picked[0].Y })
                : JsonSerializer.Serialize(picked.Select(p => new { X = p.X, Y = p.Y }));
            Log(app, AiActivityKind.Interaction, label, answer, answer == AiAnswer.Done ? string.Join("; ", picked.Select(p => $"{p.X}, {p.Y}")) : "");
            return Answered(answer, text);
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> PickApp(JsonElement args, string app, CancellationToken cancel)
    {
        var (message, context) = Asking(args);
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        string label = "Asked for an app: " + message;
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, message, context);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            var (answer, exe) = await _host.PickApp(app, message);
            Log(app, AiActivityKind.Interaction, label, answer, exe ?? "");
            return Answered(answer, JsonSerializer.Serialize(new { AppExe = exe }, AnswerOptions));
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>No separate ready step: the key window itself shows the message and context and waits.</summary>
    private async Task<AiToolResult> CaptureKeys(JsonElement args, string app, CancellationToken cancel)
    {
        var (message, context) = Asking(args);
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        string label = "Asked for a key: " + message;
        return await StartForUser(app, async () =>
        {
            var (answer, keys) = await _host.CaptureKeys(app, message, context);
            Log(app, AiActivityKind.Interaction, label, answer, Keys(keys) ?? "");
            return Answered(answer, keys == null ? "" :
                $"{KeyNames.Format(keys)}: {JsonSerializer.Serialize(keys, AnswerOptions)}");
        }, cancel).ConfigureAwait(false);
    }

    private async Task<AiToolResult> StopAll(string app)
    {
        await _host.Ui(() =>
        {
            _host.StopAll();
            return Task.FromResult(true);
        }).ConfigureAwait(false);
        _log.Add(app, AiActivityKind.Interaction, "Stopped all loops and macros", AiActivityResult.Applied);
        return new AiToolResult("Stopped every running loop and macro.");
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

    private void Log(string app, AiActivityKind kind, string label, AiAnswer answer, string detail, string? data = null) =>
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

    /// <summary>"loop \"Spam\"", "remap Ctrl + X → A".</summary>
    private static string Describe(object item) => $"{Kind(item)} {Title(item)}";

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

    /// <summary>The message (one line, shown big) and the context (a few sentences) the AI writes for the user.</summary>
    private static (string Message, string Context) Asking(JsonElement args)
    {
        string message = (Str(args, "message") ?? "").Trim();
        string context = (Str(args, "context") ?? "").Trim();
        if (message.Length == 0)
            throw new FormatException("Give a short \"message\": what the user should do. It is shown to them.");
        if (context.Length == 0)
            throw new FormatException("Give a \"context\": 1 to 3 sentences on what you are making and why. It is shown to them.");
        return (Cut(message, 140), Cut(context, 600));
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}
