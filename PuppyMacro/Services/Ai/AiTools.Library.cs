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

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The Library for AI apps: search_library (by the structured name's parts and words, short answers), get_component,
/// save_component (checked as the Library checks it, confirmed as a save is), and the parts of get_item that read a big
/// macro in pieces (an outline, one path). A macro links a component with a row {"Type":"Component","ComponentId":…},
/// written with save_macro or patch_item like any other row.
/// </summary>
internal sealed partial class AiTools
{
    public const int DefaultSearchLimit = 20;

    private static readonly JsonSerializerOptions CompactJson = new(CodeJson.Options) { WriteIndented = false };

    // ================= search_library =================

    private Task<AiToolResult> SearchLibrary(JsonElement args, string app) => _host.Ui(() =>
    {
        string? domain = Str(args, "domain")?.Trim().ToLowerInvariant();
        string? appSlug = Str(args, "app")?.Trim().ToLowerInvariant();
        string? purpose = Str(args, "purpose")?.Trim().ToLowerInvariant();
        var words = (Str(args, "words") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int limit = Math.Clamp(IntArg(args, "limit") ?? DefaultSearchLimit, 1, 200);
        var links = ComponentLibrary.Links(_host.Macros.Macros);

        var found = _host.Macros.Library.Collections
            .SelectMany(c => c.Components.Select(k => (Collection: c, Component: k)))
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
                LinkedRows = links.GetValueOrDefault(p.Component.Id),
                Collection = new { p.Collection.Id, p.Collection.Name, Screen = ScreenText(p.Collection.Screen) },
            }),
            Note = found.Count > limit ? $"Only the first {limit}: narrow the search, or raise limit." : null,
        };
        return Task.FromResult(new AiToolResult(JsonSerializer.Serialize(answer, CompactAnswer)));
    });

    private static readonly JsonSerializerOptions CompactAnswer = new(AnswerOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>"5120x1440 at 0,0, 100%": the monitor a collection's points were taken on.</summary>
    private static string? ScreenText(LibraryScreen? screen) => screen == null ? null
        : FormattableString.Invariant($"{screen.Width}x{screen.Height} at {screen.X},{screen.Y}, {screen.ScalePercent}%");

    // ================= get_component =================

    private Task<AiToolResult> GetComponent(JsonElement args, string app) => _host.Ui(() =>
    {
        Guid id = IdArg(args, "id");
        if (_host.Macros.Library.Find(id) is not { } found)
            return Task.FromResult(AiToolResult.Error($"No component has the Id {id}. search_library finds them."));
        var (collection, component) = found;
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadComponent", component.Name));
        var usedIn = _host.Macros.Macros
            .Where(m => m.AllActions.Any(a => a.Type == MacroActionType.Component && a.ComponentId == id))
            .Select(m => new { m.Id, m.Name });
        var answer = new
        {
            Name = ComponentLibrary.StructuredName(collection, component),
            Component = JsonNode.Parse(JsonSerializer.Serialize(component, CompactJson)),
            Collection = new
            {
                collection.Id, collection.Name, Domain = collection.Domain.ToString(), App = collection.AppExe,
                Screen = ScreenText(collection.Screen),
            },
            UsedIn = usedIn,
        };
        return Task.FromResult(new AiToolResult(JsonSerializer.Serialize(answer, CompactAnswer)));
    });

    // ================= save_component =================

    private async Task<AiToolResult> SaveComponent(JsonElement args, string app, CancellationToken cancel)
    {
        if (!args.TryGetProperty("component", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            return AiToolResult.Error("Give the component in \"component\": Name, Purpose, Concept, Description and Actions.");
        Guid? editId = Str(args, "id") is { Length: > 0 } idText ? Guid.Parse(idText) : null;
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

        var (problem, before, collectionName) = await _host.Ui(() => Task.FromResult(Loc.English(() =>
        {
            var library = _host.Macros.Library;
            LibraryComponent? existing = editId is Guid id ? library.Find(id)?.Component : null;
            if (editId != null && existing == null)
                return ((string?)$"No component has the Id {editId}. search_library finds them.", (string?)null, "");
            component.Id = editId ?? Guid.NewGuid();
            string? name = editId is Guid known ? library.Find(known)!.Value.Collection.Name : CollectionArg(args, component, out string? bad)?.Name;
            if (editId == null && name == null)
                return (CollectionProblem(args), (string?)null, "");
            string? check = ComponentLibrary.CheckComponent(component, _host.Macros.Images.Has);
            return (check, existing == null ? null : JsonSerializer.Serialize(existing, CodeJson.Options), name ?? "");
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
            string? notSaved = _host.SaveComponent(component, created, app);
            if (notSaved != null)
                return AiToolResult.Error(Loc.English(() => notSaved));
            _log.Add(app, AiActivityKind.Proposal, label, AiActivityResult.Saved, "", json, component.Id, "component", before);
            var collection = _host.Macros.Library.Find(component.Id)!.Value.Collection;
            return new AiToolResult($"Saved the {(before == null ? "new " : "")}component " +
                                    $"\"{ComponentLibrary.StructuredName(collection, component)}\" in the collection \"{collection.Name}\". Its Id: {component.Id}. " +
                                    "A macro links it with the row {\"Type\":\"Component\",\"ComponentId\":\"" + component.Id + "\"}.");
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
    /// The collection a new component goes in: an existing one ({"id":…}), or a new one ({"name", "domain", "app"}) made on
    /// the screen the actions' first point is on. Null when "collection" doesn't say one.
    /// </summary>
    private LibraryCollection? CollectionArg(JsonElement args, LibraryComponent component, out string? problem)
    {
        problem = null;
        if (!args.TryGetProperty("collection", out JsonElement c) || c.ValueKind != JsonValueKind.Object)
            return null;
        if (Str(c, "id") is { Length: > 0 } idText)
            return Guid.TryParse(idText, out Guid id) ? _host.Macros.Library.FindCollection(id) : null;
        if (Str(c, "name") is not { Length: > 0 } name)
            return null;
        LibraryDomain domain = Enum.TryParse(Str(c, "domain"), ignoreCase: true, out LibraryDomain d) ? d : LibraryDomain.Other;
        return new LibraryCollection
        {
            Name = name.Trim(),
            Domain = domain,
            AppExe = AppScope.Normalize(Str(c, "app")),
            Screen = LibraryScreens.Of(component.Actions),
        };
    }

    private static string CollectionProblem(JsonElement args) =>
        args.TryGetProperty("collection", out JsonElement c) && Str(c, "id") is { Length: > 0 } id
            ? $"No collection has the Id {id}. search_library gives the collections of the components it finds."
            : "Give the collection in \"collection\": {\"id\": an existing collection's Id}, or {\"name\", \"domain\", \"app\"} for a new one.";

    // ================= get_item in pieces =================

    /// <summary>One part of an item's compact JSON, at a JSON Pointer ("/Actions/3/If/Then/0"), or why not.</summary>
    internal static string? AtPath(string compact, string path, out string? problem)
    {
        problem = null;
        JsonNode? node = JsonNode.Parse(compact);
        foreach (string raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o when o.TryGetPropertyValue(part, out JsonNode? child) => child,
                JsonArray a when int.TryParse(part, out int i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node == null)
            {
                problem = $"Nothing is at \"{path}\" (\"{part}\" is not there). The outline gives each action's path.";
                return null;
            }
        }
        return node?.ToJsonString(CompactJson);
    }

    /// <summary>
    /// A macro's outline: one line per action with its JSON Pointer, type, name and, for an If, its conditions' kinds and
    /// names; down to <paramref name="depth"/> levels of Then and Else. A linked row names its component.
    /// </summary>
    internal string Outline(MacroDefinition macro, int depth)
    {
        var text = new StringBuilder();
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
                    text.Append(' ').Append(_host.Macros.Library.Find(id) is { } f ? ComponentLibrary.StructuredName(f.Collection, f.Component) : "missing");
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
        var ids = macro.AllActions.Where(a => a.Type == MacroActionType.Component && a.ComponentId != null)
            .Select(a => a.ComponentId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return "";
        return "\nLinked components (get_component reads one):\n" + string.Join("\n", ids.Select(id =>
            _host.Macros.Library.Find(id) is { } f ? $"- {id}: {ComponentLibrary.StructuredName(f.Collection, f.Component)}" : $"- {id}: missing from the Library"));
    }
}
