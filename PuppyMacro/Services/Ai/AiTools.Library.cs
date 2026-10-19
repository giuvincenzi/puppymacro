using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The Library for AI apps: search_library (by the structured name's parts and words, short answers with each component's
/// signature), get_component (with a call to copy), save_component (checked as the Library checks it, the guided update of
/// the calls when its signature changes, confirmed as a save is), and the parts of get_item that read a big macro in pieces
/// (an outline with the calls' values, one path). A macro calls a component with a row {"Type":"Component","ComponentId":…,
/// "Arguments":…} or a condition {"Kind":"Component",…}, written with save_macro or patch_item like any other.
/// </summary>
internal sealed partial class AiTools
{
    public const int DefaultSearchLimit = 20;

    private static readonly JsonSerializerOptions CompactJson = new(CodeJson.Options) { WriteIndented = false };

    // ================= search_library =================

    private Task<AiToolResult> SearchLibrary(JsonElement args, string app) => _host.Ui(() =>
    {
        Guid? collectionId = Str(args, "collection") is { Length: > 0 } idText
            ? Guid.TryParse(idText, out Guid parsed) ? parsed : throw new FormatException("\"collection\" must be a collection's Id.")
            : null;
        string? domain = OneOf(args, "domain", Domains, "every domain");
        string? appSlug = Str(args, "app")?.Trim().ToLowerInvariant();
        string? purpose = OneOf(args, "purpose", Enum.GetNames<ComponentPurpose>().Select(n => n.ToLowerInvariant()).ToList(), "every purpose");
        var words = (Str(args, "words") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int limit = IntArg(args, "limit", 1, 200) ?? DefaultSearchLimit;
        var links = ComponentLibrary.Links(_host.Macros.Macros);

        var found = _host.Macros.Library.Collections
            .SelectMany(c => c.Components.Select(k => (Collection: c, Component: k)))
            .Where(p => collectionId == null || p.Collection.Id == collectionId)
            .Where(p => domain == null || p.Collection.Domain.ToString().Equals(domain, StringComparison.OrdinalIgnoreCase))
            .Where(p => appSlug == null || ComponentLibrary.AppSlug(p.Collection.AppExe) == appSlug)
            .Where(p => purpose == null || p.Component.Purpose.ToString().Equals(purpose, StringComparison.OrdinalIgnoreCase))
            .Where(p => words.All(w =>
                ComponentLibrary.StructuredName(p.Collection, p.Component).Contains(w, StringComparison.OrdinalIgnoreCase)
                || p.Component.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || p.Component.Description.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_SearchedLibrary"));
        var answer = new
        {
            Found = found.Count,
            Components = found.Take(limit).Select(p => new
            {
                p.Component.Id,
                Name = ComponentLibrary.StructuredName(p.Collection, p.Component),
                Title = p.Component.Name,
                Description = string.IsNullOrWhiteSpace(p.Component.Description) ? null : p.Component.Description,
                Actions = MacroDefinition.Walk(p.Component.Actions).Count(),
                Parameters = Signature(p.Component),
                Condition = p.Component.Returns != null ? true : (bool?)null,
                LinkedRows = links.GetValueOrDefault(p.Component.Id),
                Collection = new { p.Collection.Id, p.Collection.Name, Screen = ScreenText(p.Collection.Screen) },
            }),
            Note = found.Count > limit ? $"Only the first {limit}: narrow the search, or raise limit." : null,
        };
        return Task.FromResult(new AiToolResult(JsonSerializer.Serialize(answer, CompactAnswer)));
    });

    /// <summary>The Library's domains as the AI tools write them: "game", "office"…</summary>
    private static readonly string[] Domains = Enum.GetNames<LibraryDomain>().Select(n => n.ToLowerInvariant()).ToArray();

    private static readonly JsonSerializerOptions CompactAnswer = new(AnswerOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>"Slot: Point; Hold: Number (Ms) = 30": a component's parameters in one line, or null when it takes none.</summary>
    private static string? Signature(LibraryComponent component) => component.Parameters.Count == 0 ? null
        : string.Join("; ", component.Parameters.Select(p =>
            $"{p.Name}: {p.Type}{(p.Unit is NumberUnit u ? $" ({u})" : "")}{(p.Default is { } d ? " = " + d.ToJsonString() : "")}"));

    /// <summary>A call to copy: each required parameter with its example (the others take their defaults).</summary>
    private static Dictionary<string, JsonNode?>? ExampleArguments(LibraryComponent component)
    {
        var arguments = component.Parameters.Where(p => p.Required).ToDictionary(p => p.Name, p => p.Example?.DeepClone());
        return arguments.Count == 0 ? null : arguments;
    }

    /// <summary>"5120x1440 at 0,0, 100%": the monitor a collection's points were taken on.</summary>
    private static string? ScreenText(LibraryScreen? screen) => screen == null ? null
        : FormattableString.Invariant($"{screen.Width}x{screen.Height} at {screen.X},{screen.Y}, {screen.ScalePercent}%");

    // ================= get_component =================

    private Task<AiToolResult> GetComponent(JsonElement args, string app) => _host.Ui(() =>
    {
        Guid id = IdArg(args, "id", "a component's Id (search_library finds them)");
        if (_host.Macros.Library.Find(id) is not { } found)
            return Task.FromResult(AiToolResult.Error($"No component has the Id {id}. search_library finds them."));
        var (collection, component) = found;
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadComponent", component.Name));
        var usedIn = _host.Macros.Macros
            .Where(m => ComponentSignatures.Callees(m.Actions).Contains(id))
            .Select(m => new { m.Id, m.Name });
        var calledBy = _host.Macros.Library.AllComponents
            .Where(c => c.Id != id && ComponentSignatures.Callees(c.Actions).Contains(id))
            .Select(c => new { c.Id, c.Name });
        var arguments = ExampleArguments(component);
        var answer = new
        {
            Name = ComponentLibrary.StructuredName(collection, component),
            Component = JsonNode.Parse(AiJson.Compact(component)),
            Collection = new
            {
                collection.Id, collection.Name, Domain = collection.Domain.ToString(), App = collection.AppExe,
                Screen = ScreenText(collection.Screen),
            },
            Call = new { Type = "Component", ComponentId = id, Arguments = arguments },
            AsCondition = component.Returns == null ? null : new { Kind = "Component", Name = component.Name, ComponentId = id, Arguments = arguments },
            UsedIn = usedIn,
            CalledBy = calledBy,
        };
        return Task.FromResult(new AiToolResult(JsonSerializer.Serialize(answer, CompactAnswer)));
    });

    // ================= save_component =================

    private async Task<AiToolResult> SaveComponent(JsonElement args, string app, CancellationToken cancel)
    {
        if (!args.TryGetProperty("component", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            return AiToolResult.Error("Give the component in \"component\": Name, Purpose, Concept, Description and Actions.");
        Guid? editId = OptionalIdArg(args, "id", "the Id of the component to change (search_library finds them); leave it out for a new one");
        RenamedArg(args);
        ObjectArg(args, "call_values", "for each parameter the answer named, the value every call without a fitting one gets");
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);

        LibraryComponent component;
        try
        {
            component = JsonSerializer.Deserialize<LibraryComponent>(item.GetRawText(), CodeJson.StrictOptions)
                ?? throw new JsonException("\"component\" is empty.");
        }
        catch (JsonException ex)
        {
            return AiToolResult.Error($"\"component\" can't be read: {ex.Message}");
        }

        List<MacroDefinition> updatedMacros = new();
        List<LibraryComponent> updatedComponents = new();
        string updatedText = "";
        var (problem, before, collectionName) = await _host.Ui(() => Task.FromResult(Loc.English(() =>
        {
            var library = _host.Macros.Library;
            LibraryComponent? existing = editId is Guid id ? library.Find(id)?.Component : null;
            if (editId != null && existing == null)
                return ((string?)$"No component has the Id {editId}. search_library finds them.", (string?)null, "");
            component.Id = editId ?? Guid.NewGuid();
            // A new component's collection is checked here, before the user is asked, as the Library will check it.
            string? bad = null;
            string? name = editId is Guid known ? KeptCollection(args, library.Find(known)!.Value.Collection, out bad)
                : CollectionArg(args, component, out bad)?.Name;
            if (name == null)
                return (bad, (string?)null, "");
            // A bound field holds its parameter's Example.
            ComponentEdits.WriteExamples(component);
            var others = library.AllComponents.Where(c => c.Id != component.Id).ToList();
            var problems = ComponentLibrary.Problems(component, _host.Macros.Images.Has, others.Append(component));
            if (problems.Count > 0)
                return (string.Join(" ", problems.Select(p => p.Message)), (string?)null, "");
            if (existing != null && UpdateCalls(args, existing, component, others, out updatedMacros, out updatedComponents, out updatedText) is string calls)
                return (calls, (string?)null, "");
            return ((string?)null, existing == null ? null : JsonSerializer.Serialize(existing, CodeJson.Options), name ?? "");
        }))).ConfigureAwait(false);
        if (problem != null)
            return AiToolResult.Error($"The component has problems, nothing was saved: {problem}");

        string json = JsonSerializer.Serialize(component, CodeJson.Options);
        Phrase label = Phrase.Of(before == null ? "Ai_NewComponent" : "Ai_ChangedComponent", component.Name);

        AiToolResult Apply()
        {
            if (BusyNow() is AiToolResult busyNow)
                return busyNow;
            LibraryCollection? created = editId == null ? CollectionArg(args, component, out _) : null;
            // The host answers in English (IAiHost.SaveComponent), while the user's own windows stay in their language.
            string? notSaved = _host.SaveComponent(component, created, app, updatedMacros, updatedComponents);
            if (notSaved != null)
                return AiToolResult.Error(notSaved);
            _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", json, component.Id, "component", before);
            var collection = _host.Macros.Library.Find(component.Id)!.Value.Collection;
            string call = JsonSerializer.Serialize(new { Type = "Component", ComponentId = component.Id, Arguments = ExampleArguments(component) }, CompactAnswer);
            return new AiToolResult($"Saved the {(before == null ? "new " : "")}component " +
                                    $"\"{ComponentLibrary.StructuredName(collection, component)}\" in the collection \"{collection.Name}\". Its Id: {component.Id}. " +
                                    $"A macro calls it with the row {call}." + updatedText);
        }

        if (!_host.Settings.AiAskBeforeChanging)
            return await _host.Ui(() => Task.FromResult(Apply())).ConfigureAwait(false);

        return await StartForUser(app, async () =>
        {
            AiAnswer answer = await _host.Confirm(app, before == null ? Strings.Ai_ConfirmNewComponent : Strings.Ai_ConfirmChangedComponent,
                new List<string> { label.ToString() }, Strings.Ai_Save);
            if (answer == AiAnswer.Done)
                return Apply();
            Log(app, AiActivityKind.Proposal, label, answer, "", json);
            return Answered(answer, "");
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The guided update of the calls when a component's signature changes (SignatureUpdate): the macros and components whose
    /// calls change, with call_values given to those that need a value and renamed followed. Why not (the calls and the values
    /// still needed, said for the AI app), or null; <paramref name="text"/> says what changed, for the answer.
    /// </summary>
    private string? UpdateCalls(JsonElement args, LibraryComponent existing, LibraryComponent after, List<LibraryComponent> others,
        out List<MacroDefinition> macros, out List<LibraryComponent> components, out string text)
    {
        macros = new List<MacroDefinition>();
        components = new List<LibraryComponent>();
        text = "";
        Dictionary<string, string> renamed = RenamedArg(args);
        var update = SignatureUpdate.Compare(existing, after, _host.Macros.Macros, others, renamed);
        if (!update.Needed)
            return null;
        var signatures = new ComponentSignatures(others.Append(after));
        var values = new Dictionary<string, JsonNode>();
        var missing = new List<string>();
        JsonElement given = Given(args, "call_values", out JsonElement v) ? v : default;
        foreach (var parameter in update.NeedValue)
        {
            JsonNode? value = given.ValueKind == JsonValueKind.Object && given.TryGetProperty(parameter.Name, out JsonElement e)
                ? JsonNode.Parse(e.GetRawText()) : null;
            if (value == null)
            {
                missing.Add($"{parameter.Name} ({parameter.Type}{(parameter.Unit is NumberUnit u ? ", " + u : "")})");
                continue;
            }
            ComponentValues.Read(value, parameter.Type, signatures.RuleOf(after.Id, parameter.Name), out string? bad, _host.Macros.Images.Has);
            if (bad != null)
                return $"call_values.{parameter.Name}: {bad}";
            values[parameter.Name] = value;
        }
        string sites = string.Join(", ", update.Sites.Select(s => $"\"{s.OwnerName}\" ({(s.IsMacro ? "macro" : "component")}, {s.Calls})"));
        if (missing.Count > 0)
            return $"This change updates {update.Calls} existing call(s): {sites}. " +
                   $"Give call_values with the value every call without a fitting one gets, for: {string.Join(", ", missing)}" +
                   (update.Removed.Count > 0 ? $". Taken out of every call: {string.Join(", ", update.Removed)}" : "") +
                   " (if a parameter was renamed, renamed {\"old\": \"new\"} keeps the calls' values).";
        var (newMacros, newComponents) = update.Apply(_host.Macros.Macros, others, values, after);
        (macros, components) = (newMacros, newComponents);
        var changed = newMacros.ToDictionary(m => m.Id);
        var updatedOthers = others.Select(c => newComponents.FirstOrDefault(k => k.Id == c.Id) ?? c).Append(after);
        var problems = ComponentChecks.CheckAll(new ComponentSignatures(updatedOthers),
                _host.Macros.Macros.Select(m => changed.GetValueOrDefault(m.Id) ?? m), _host.Macros.Images.Has)
            .Where(p => update.Sites.Any(s => s.OwnerId == p.ItemId))
            .ToList();
        if (problems.Count > 0)
            return "After updating the calls some can't work, nothing was saved: " +
                   string.Join(" ", problems.Select(p => $"{p.Where}, {p.Path}: {p.Message}"));
        text = $" Updated {update.Calls} call(s): {sites}.";
        return null;
    }

    /// <summary>
    /// The collection a new component goes in: an existing one ({"id":…}), or a new one ({"name", "domain", "app"}) made on
    /// the screen the actions' first point is on and checked as the Library checks a collection (in the language of the
    /// moment: English for the AI app). Null, with why in <paramref name="problem"/>, when "collection" doesn't say one.
    /// </summary>
    private LibraryCollection? CollectionArg(JsonElement args, LibraryComponent component, out string? problem)
    {
        problem = "Give the collection in \"collection\": {\"id\": an existing collection's Id}, or {\"name\", \"domain\", \"app\"} for a new one.";
        if (!Given(args, "collection", out JsonElement c))
            return null;
        if (c.ValueKind != JsonValueKind.Object)
        {
            problem = $"\"collection\" must be an object, not {JsonKind(c)}: " + problem;
            return null;
        }
        if (Given(c, "id", out JsonElement idValue))
        {
            if (idValue.ValueKind == JsonValueKind.String && Guid.TryParse(idValue.GetString(), out Guid id)
                && _host.Macros.Library.FindCollection(id) is { } existing)
            {
                problem = null;
                return existing;
            }
            problem = $"No collection has the Id {idValue.GetRawText()} (\"collection.id\"). search_library gives the collections of the components it finds.";
            return null;
        }
        if (!Given(c, "name", out JsonElement nameValue))
            return null;
        if (nameValue.ValueKind != JsonValueKind.String)
        {
            problem = "\"collection.name\" must be a text: the new collection's name.";
            return null;
        }
        LibraryDomain domain = LibraryDomain.Other;
        if (Given(c, "domain", out JsonElement domainValue))
        {
            // Only a domain's name: Enum.TryParse would also take any number ("42"), a domain that does not exist.
            string? named = domainValue.ValueKind == JsonValueKind.String
                ? Enum.GetNames<LibraryDomain>().FirstOrDefault(n => n.Equals(domainValue.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (named == null)
            {
                problem = "\"collection.domain\" must be one of " +
                          string.Join(", ", Enum.GetNames<LibraryDomain>().Select(n => n.ToLowerInvariant())) + ".";
                return null;
            }
            domain = Enum.Parse<LibraryDomain>(named);
        }
        var collection = new LibraryCollection
        {
            Name = nameValue.GetString()!.Trim(),
            Domain = domain,
            AppExe = AppScope.Normalize(Str(c, "app", "collection.app")),
            Screen = LibraryScreens.Of(component.Actions),
        };
        if (ComponentLibrary.CheckCollection(collection) is string bad)
        {
            problem = $"The new collection in \"collection\" can't be made as it is. {bad}";
            return null;
        }
        problem = null;
        return collection;
    }

    /// <summary>
    /// A changed component stays in its collection: its name, or null with why when "collection" names another one or a new
    /// one (left out, or {"id"} of its own, is fine).
    /// </summary>
    private static string? KeptCollection(JsonElement args, LibraryCollection own, out string? problem)
    {
        problem = null;
        if (!Given(args, "collection", out JsonElement c)
            || c.ValueKind == JsonValueKind.Object && c.EnumerateObject().All(p => p.Name == "id")
               && c.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
               && Guid.TryParse(id.GetString(), out Guid given) && given == own.Id)
            return own.Name;
        problem = $"A component never moves to another collection: this one stays in \"{own.Name}\" (Id {own.Id}). " +
                  "\"collection\" is for a new component; with \"id\" it is left out.";
        return null;
    }

    /// <summary>save_component's renamed: each old parameter name with its new one; anything else is an input error naming it.</summary>
    private static Dictionary<string, string> RenamedArg(JsonElement args) =>
        ObjectArg(args, "renamed", "each old name with its new name, {\"Old\": \"New\"}").ToDictionary(p => p.Name, p =>
            p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Trim().Length > 0
                ? p.Value.GetString()!
                : throw new FormatException($"\"renamed.{p.Name}\" must be the parameter's new name, a text with something in it."));

    /// <summary>An object argument's fields, none when left out (or null); anything else is an input error naming it.</summary>
    private static IEnumerable<JsonProperty> ObjectArg(JsonElement args, string name, string what) =>
        !Given(args, name, out JsonElement value) ? Enumerable.Empty<JsonProperty>()
        : value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().ToList()
        : throw new FormatException($"\"{name}\" must be an object ({what}), not {JsonKind(value)}.");

    // ================= get_item in pieces =================

    /// <summary>
    /// One part of an item at a JSON Pointer ("/Actions/3/If/Then/0"), or why not: from its compact JSON (get_item's), or,
    /// for a field the compact JSON leaves out at its default, from the item's whole JSON (<paramref name="full"/>, the one
    /// patch_item changes), so a path patch_item works on is a path get_item reads.
    /// </summary>
    internal static string? AtPath(string compact, string full, string path, out string? problem)
    {
        problem = null;
        JsonNode? part = Find(JsonNode.Parse(compact), path, out string? missing);
        if (missing != null)
            part = Find(JsonNode.Parse(full), path, out missing);
        if (missing == null)
            return part?.ToJsonString(CompactJson) ?? "null";
        problem = $"Nothing is at \"{path}\" (\"{missing}\" is not there). The outline gives each action's path.";
        return null;

        // The node at the path (null also for a JSON null), or the first part that is not there.
        static JsonNode? Find(JsonNode? node, string path, out string? missing)
        {
            missing = null;
            foreach (string raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Replace("~1", "/").Replace("~0", "~");
                if (node is JsonObject o && o.TryGetPropertyValue(part, out JsonNode? child))
                    node = child;
                else if (node is JsonArray a && int.TryParse(part, out int i) && i >= 0 && i < a.Count)
                    node = a[i];
                else
                {
                    missing = part;
                    return null;
                }
            }
            return node;
        }
    }

    /// <summary>
    /// "Settings: Mode Hold, Hotkey F5, Repeat Loop, ChecksPerSecond 60, Debug on": the item's fields that differ from their
    /// defaults (the ones get_item's compact JSON keeps), without its Id, name and actions, on one line, in English.
    /// </summary>
    internal static string SettingsLine(object item) => Loc.English(() =>
    {
        var node = (JsonObject)JsonNode.Parse(AiJson.Compact(item))!;
        var info = CodeJson.Options.GetTypeInfo(item.GetType());
        var parts = new List<string>();
        foreach (var (name, value) in node)
        {
            if (name is "Id" or "Name" or "Actions")
                continue;
            object? field = info.Properties.FirstOrDefault(p => p.Name == name)?.Get?.Invoke(item);
            parts.Add(name + " " + (field, value) switch
            {
                (HotkeyBinding keys, _) => KeyNames.Format(keys),
                (_, JsonValue v) when v.GetValueKind() == JsonValueKind.True => "on",
                (_, JsonValue v) when v.GetValueKind() == JsonValueKind.False => "off",
                (_, JsonValue v) when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
                (_, JsonArray list) => list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => value?.ToJsonString(CompactJson) ?? "null",
            });
        }
        return "Settings: " + (parts.Count == 0 ? "all at their defaults" : string.Join(", ", parts));
    });

    /// <summary>
    /// A macro's outline: first its settings that differ from their defaults (<see cref="SettingsLine"/>), so a model that
    /// reads only the outline of a big macro sees them too; then one line per action with its JSON Pointer, type, name and,
    /// for an If, its conditions' kinds and names; down to <paramref name="depth"/> levels of Then and Else. A linked row
    /// names its component.
    /// </summary>
    internal string Outline(MacroDefinition macro, int depth)
    {
        var text = new StringBuilder(SettingsLine(macro)).Append('\n');
        void Walk(List<MacroAction> actions, string path, int level)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                string at = $"{path}/{i}";
                text.Append(new string(' ', level * 2)).Append(at).Append(' ').Append(a.Type);
                if (!string.IsNullOrWhiteSpace(a.Name))
                    text.Append(" \"").Append(a.Name).Append('"');
                if (a.Type == MacroActionType.Component && a.ComponentId is Guid id)
                {
                    text.Append(' ').Append(_host.Macros.Library.Find(id) is { } f ? ComponentLibrary.StructuredName(f.Collection, f.Component) : "missing");
                    if (a.Arguments is { Count: > 0 } arguments)
                        text.Append(' ').Append(new JsonObject(arguments.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString(CompactJson));
                }
                if (a.If is { } block)
                {
                    text.Append(" [").Append(string.Join(", ", block.Conditions.Select(c => c.Kind + (string.IsNullOrWhiteSpace(c.Name) ? "" : $" \"{c.Name}\""))))
                        .Append($"] Then {block.Then.Count}, Else {block.Else.Count}");
                }
                text.Append('\n');
                if (a.If is { } inner && level + 1 < depth)
                {
                    Walk(inner.Then, at + "/If/Then", level + 1);
                    Walk(inner.Else, at + "/If/Else", level + 1);
                }
            }
        }
        Walk(macro.Actions, "/Actions", 0);
        return text.ToString();
    }

    /// <summary>After a macro's JSON: the components it links, so the AI app reads them only if it needs to.</summary>
    private string LinkedList(MacroDefinition macro)
    {
        var ids = ComponentSignatures.Callees(macro.Actions).Distinct().ToList();
        if (ids.Count == 0)
            return "";
        return "\nLinked components (get_component reads one):\n" + string.Join("\n", ids.Select(id =>
            _host.Macros.Library.Find(id) is { } f ? $"- {id}: {ComponentLibrary.StructuredName(f.Collection, f.Component)}" : $"- {id}: missing from the Library"));
    }
}
