using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>One saved version of a loop, macro or remap: who saved it, what changed, and the item's JSON then.</summary>
public sealed class HistoryVersion
{
    public DateTime Time { get; set; }

    /// <summary>"You", or the AI app that saved it ("Claude Code"). Empty for the version before the history started.</summary>
    public string By { get; set; } = "";

    /// <summary>One line in English: "Created", "Hotkey: F6 → F7", "3 changes", "Deleted". Kept for older versions;
    /// shown when there is no <see cref="SummaryPhrase"/> (versions saved before 1.17).</summary>
    public string Summary { get; set; } = "";

    /// <summary>What changed from the version before, one line each, in English (as <see cref="Summary"/>).</summary>
    public List<string> Changes { get; set; } = new();

    /// <summary><see cref="Summary"/> in the language of the moment (i18n.md).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Phrase? SummaryPhrase { get; set; }

    /// <summary><see cref="Changes"/> in the language of the moment.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Phrase>? ChangePhrases { get; set; }

    /// <summary>The summary to show: in the user's language, or as it was saved by an older version.</summary>
    [JsonIgnore]
    public string ShownSummary => SummaryPhrase?.TryRender(CultureInfo.CurrentUICulture) ?? Summary;

    /// <summary>The changes to show, as <see cref="ShownSummary"/>.</summary>
    [JsonIgnore]
    public List<string> ShownChanges => ChangePhrases?.Select(p => p.TryRender(CultureInfo.CurrentUICulture)).ToList() is { } shown
        && shown.All(s => s != null) ? shown! : Changes;

    /// <summary>A version whose texts are <paramref name="summary"/> and <paramref name="changes"/>, kept in English too.</summary>
    internal static HistoryVersion With(Phrase summary, List<Phrase>? changes = null) => new()
    {
        Summary = summary.English,
        SummaryPhrase = summary,
        Changes = changes?.Select(c => c.English).ToList() ?? new(),
        ChangePhrases = changes is { Count: > 0 } ? changes : null,
    };

    /// <summary>The item as saved (for a deletion: as it was before).</summary>
    public string Json { get; set; } = "";

    public bool Deleted { get; set; }
}

/// <summary>
/// The History of every loop, macro and remap: the newest versions of each that fit in <see cref="MaxBytes"/>, in
/// <c>history\{id}.json</c> in the data folder, recorded on every save (by the user or an AI app). A version equal to the
/// one before is not kept. The first change of an item saved before the history existed keeps its earlier version too.
/// </summary>
internal sealed class ItemHistory
{
    /// <summary>Per item: a big macro keeps fewer versions than a small loop, never less than the newest one.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;
    public const string You = "You";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly long _maxBytes;
    private readonly Dictionary<Guid, string> _current = new();

    public ItemHistory(string folder, Func<DateTime>? now = null, long maxBytes = MaxBytes)
    {
        _folder = folder;
        _now = now ?? (() => DateTime.Now);
        _maxBytes = maxBytes;
    }

    /// <summary>The ids whose current JSON is known (seeded or recorded).</summary>
    public IReadOnlyCollection<Guid> Known => _current.Keys;

    /// <summary>The item as it is now, at start: no version is written until it changes.</summary>
    public void Seed(Guid id, string json) => _current[id] = json;

    /// <summary>Records a save. Nothing when the item did not change.</summary>
    public void Record(Guid id, string json, string by)
    {
        _current.TryGetValue(id, out string? before);
        if (before == json)
            return;
        List<HistoryVersion> versions = Versions(id);
        versions.Reverse(); // oldest first
        bool restored = versions.Count > 0 && versions[^1].Deleted;
        if (versions.Count == 0 && before != null)
            versions.Add(Version(Phrase.Of("History_BeforeHistory"), before));

        // Compared with the last version kept (a deleted item: with what it was when deleted).
        string? baseJson = versions.Count > 0 ? versions[^1].Json : before;
        List<Phrase> changes = baseJson != null ? Diff(baseJson, json) : new List<Phrase>();
        Phrase summary = restored ? Phrase.Of("History_Restored")
            : baseJson == null ? Phrase.Of("History_Created")
            : changes.Count == 1 ? changes[0]
            : changes.Count == 0 ? Phrase.Of("History_Saved")
            : Phrase.Plural("History_Changes", changes.Count);
        HistoryVersion version = Version(summary, json, changes);
        version.By = by;
        versions.Add(version);
        Write(id, versions);
        _current[id] = json;
    }

    /// <summary>Records that an item was deleted (its last JSON kept, so it can be restored).</summary>
    public void RecordDeleted(Guid id, string by)
    {
        if (!_current.Remove(id, out string? json))
            return;
        List<HistoryVersion> versions = Versions(id);
        versions.Reverse();
        if (versions.Count == 0)
            versions.Add(Version(Phrase.Of("History_BeforeHistory"), json));
        HistoryVersion deleted = Version(Phrase.Of("History_Deleted"), json);
        deleted.By = by;
        deleted.Deleted = true;
        versions.Add(deleted);
        Write(id, versions);
    }

    private HistoryVersion Version(Phrase summary, string json, List<Phrase>? changes = null)
    {
        HistoryVersion version = HistoryVersion.With(summary, changes);
        version.Time = _now();
        version.Json = json;
        return version;
    }

    /// <summary>
    /// The versions of an item, newest first (empty when it has none). The order is the file's, the order they were saved
    /// in, not their times: the clock can go back (daylight saving time ends, the user sets it).
    /// </summary>
    public List<HistoryVersion> Versions(Guid id)
    {
        string path = PathOf(id);
        try
        {
            if (!File.Exists(path))
                return new List<HistoryVersion>();
            List<HistoryVersion> versions = JsonSerializer.Deserialize<List<HistoryVersion>>(File.ReadAllText(path), FileOptions) ?? new();
            versions.Reverse();
            return versions;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<HistoryVersion>();
        }
    }

    private string PathOf(Guid id) => Path.Combine(_folder, id.ToString("D") + ".json");

    /// <summary>Oldest first; keeps the newest that fit in the limit. A history that can't be written is only lost, never an error.</summary>
    private void Write(Guid id, List<HistoryVersion> versions)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string path = PathOf(id);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Newest(versions), FileOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The bytes an object's indented JSON takes as an item of an indented list: each of its lines indented 2 more, and the
    /// comma and line break after it.
    /// </summary>
    internal static long SizeInList(string json) =>
        System.Text.Encoding.UTF8.GetByteCount(json) + 2L * (json.Count(c => c == '\n') + 1) + 3;

    /// <summary>The newest versions whose JSON fits in the limit, oldest first (the newest one always stays).</summary>
    private List<HistoryVersion> Newest(List<HistoryVersion> versions)
    {
        var kept = new List<HistoryVersion>();
        long size = 2; // [ ]
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            size += SizeInList(JsonSerializer.Serialize(versions[i], FileOptions));
            if (size > _maxBytes && kept.Count > 0)
                break;
            kept.Add(versions[i]);
        }
        kept.Reverse();
        return kept;
    }

    // ================= What changed =================

    /// <summary>Names of objects that are keys (shown as "Ctrl + X").</summary>
    private static readonly HashSet<string> KeyObjects = new(StringComparer.Ordinal) { "Hotkey", "Source", "Target" };

    /// <summary>
    /// What changed between two versions of an item's JSON, for people, in the language of this thread: "Hotkey: F6 → F7",
    /// "Added action 3 (Click)", "Action 2 (Destroy): Delay 100 → 250". The names of the fields are the Code view's, in
    /// every language.
    /// </summary>
    public static List<string> Changes(string beforeJson, string afterJson) =>
        Diff(beforeJson, afterJson).Select(c => c.ToString()).ToList();

    /// <summary><see cref="Changes"/> as phrases, to be kept and shown in the language of the moment.</summary>
    public static List<Phrase> Diff(string beforeJson, string afterJson)
    {
        JsonObject? before = Parse(beforeJson);
        JsonObject? after = Parse(afterJson);
        var changes = new List<Phrase>();
        if (before == null || after == null)
            return changes;

        var a = new SortedDictionary<string, PhraseArg>(StringComparer.Ordinal);
        var b = new SortedDictionary<string, PhraseArg>(StringComparer.Ordinal);
        Leaves(before, "", a);
        Leaves(after, "", b);

        // Actions: added and removed ones first, then each one's changes.
        var beforeActions = before["Actions"] as JsonArray;
        var afterActions = after["Actions"] as JsonArray;
        int beforeCount = beforeActions?.Count ?? 0, afterCount = afterActions?.Count ?? 0;
        for (int i = beforeCount; i < afterCount; i++)
            changes.Add(ActionPhrase("History_ActionAdded", i, afterActions![i]));
        for (int i = afterCount; i < beforeCount; i++)
            changes.Add(ActionPhrase("History_ActionRemoved", i, beforeActions![i]));

        PhraseArg none = Phrase.Of("History_None");
        foreach (string key in a.Keys.Union(b.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out PhraseArg? old);
            b.TryGetValue(key, out PhraseArg? now);
            if (Same(old, now))
                continue;
            Match action = Regex.Match(key, @"^Actions\[(\d+)\]\.(.+)$");
            if (action.Success)
            {
                int index = int.Parse(action.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index >= Math.Min(beforeCount, afterCount))
                    continue; // added or removed above
                Phrase what = ActionPhrase("History_ActionChanged", index, afterActions![index]);
                what.Args!.AddRange(new[] { (PhraseArg)Label(action.Groups[2].Value), old ?? none, now ?? none });
                changes.Add(what);
            }
            else if (!key.StartsWith("Actions", StringComparison.Ordinal) && key != "Id") // actions: added or removed above
            {
                changes.Add(Phrase.Of("History_FieldChanged", Label(key), old ?? none, now ?? none));
            }
        }
        return changes;
    }

    /// <summary>The same value: compared as English text (keys by their names).</summary>
    private static bool Same(PhraseArg? a, PhraseArg? b) =>
        a?.TryRender(CultureInfo.InvariantCulture) == b?.TryRender(CultureInfo.InvariantCulture);

    /// <summary>"Added action 3" or, with the action's name or type, "Added action 3 (Click)" (the key's "…Named" text).</summary>
    private static Phrase ActionPhrase(string key, int index, JsonNode? action)
    {
        string number = (index + 1).ToString(CultureInfo.InvariantCulture);
        string? label = ActionLabel(action);
        return label == null ? Phrase.Of(key, number) : Phrase.Of(key + "Named", number, label);
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Destroy", the action's name, or "Click", its type; null when it has neither.</summary>
    private static string? ActionLabel(JsonNode? action)
    {
        if (action is not JsonObject o)
            return null;
        string? name = (o["Name"] as JsonValue)?.TryGetValue(out string? n) == true ? n : null;
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        string? type = (o["Type"] as JsonValue)?.TryGetValue(out string? t) == true ? t : null;
        return string.IsNullOrEmpty(type) ? null : Label(type);
    }

    /// <summary>Every value of the item by its path ("Name", "FloatingButton.Size", "Actions[1].DelayMs"), as shown to people.</summary>
    private static void Leaves(JsonNode? node, string path, IDictionary<string, PhraseArg> output)
    {
        string last = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
        switch (node)
        {
            case JsonObject o when KeyObjects.Contains(last) || last.EndsWith("Hotkey", StringComparison.Ordinal):
                output[path] = KeysOf(o);
                break;
            case JsonObject o:
                foreach (var (key, value) in o)
                    Leaves(value, path.Length == 0 ? key : $"{path}.{key}", output);
                break;
            case JsonArray array when last == "Path":
                output[path] = Phrase.Plural("History_Path", array.Count,
                    array.Count > 0 ? $" ({Hash(array.ToJsonString())})" : "");
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                    Leaves(array[i], $"{path}[{i}]", output);
                if (array.Count == 0)
                    output[path] = Phrase.Of("History_None");
                break;
            case JsonValue value:
                output[path] = Show(value);
                break;
            default:
                output[path] = Phrase.Of("History_None");
                break;
        }
    }

    private static PhraseArg KeysOf(JsonObject keys)
    {
        try
        {
            HotkeyBinding? binding = keys.Deserialize<HotkeyBinding>(CodeJson.Options);
            return binding is { IsSet: true } ? PhraseArg.Of(binding) : Phrase.Of("History_None");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return keys.ToJsonString();
        }
    }

    private static PhraseArg Show(JsonValue value)
    {
        if (value.TryGetValue(out bool flag))
            return Phrase.Of(flag ? "History_On" : "History_Off");
        if (value.TryGetValue(out string? text))
            return $"\"{text}\"";
        if (value.TryGetValue(out double number))
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        return value.ToJsonString();
    }

    /// <summary>"DelayMs" → "Delay ms", "FloatingButton.Size" → "Floating button size".</summary>
    internal static string Label(string path)
    {
        string spaced = Regex.Replace(path.Replace(".", " "), "(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = Regex.Replace(spaced, @"\[(\d+)\]", m => $" {int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1}");
        string lower = spaced.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    /// <summary>A short, stable fingerprint (FNV-1a), so two different paths of the same length differ.</summary>
    private static string Hash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
            hash = (hash ^ c) * 16777619;
        return hash.ToString("x8", CultureInfo.InvariantCulture)[..4];
    }
}
