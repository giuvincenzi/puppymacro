using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>One saved version of a loop, macro or remap: who saved it, what changed, and the item's JSON then.</summary>
public sealed class HistoryVersion
{
    public DateTime Time { get; set; }

    /// <summary>"You", or the AI app that saved it ("Claude Code"). Empty for the version before the history started.</summary>
    public string By { get; set; } = "";

    /// <summary>One line: "Created", "Hotkey: F6 → F7", "3 changes", "Deleted".</summary>
    public string Summary { get; set; } = "";

    /// <summary>What changed from the version before, one line each.</summary>
    public List<string> Changes { get; set; } = new();

    /// <summary>The item as saved (for a deletion: as it was before).</summary>
    public string Json { get; set; } = "";

    public bool Deleted { get; set; }
}

/// <summary>
/// The History of every loop, macro and remap: the last <see cref="MaxVersions"/> versions of each, in
/// <c>history\{id}.json</c> in the data folder, recorded on every save (by the user or an AI app). A version equal to the
/// one before is not kept. The first change of an item saved before the history existed keeps its earlier version too.
/// </summary>
internal sealed class ItemHistory
{
    public const int MaxVersions = 50;
    public const string You = "You";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<Guid, string> _current = new();

    public ItemHistory(string folder, Func<DateTime>? now = null)
    {
        _folder = folder;
        _now = now ?? (() => DateTime.Now);
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
            versions.Add(new HistoryVersion { Time = _now(), Summary = "Before the history started", Json = before });

        // Compared with the last version kept (a deleted item: with what it was when deleted).
        string? baseJson = versions.Count > 0 ? versions[^1].Json : before;
        List<string> changes = baseJson != null ? Changes(baseJson, json) : new List<string>();
        string summary = restored ? "Restored"
            : baseJson == null ? "Created"
            : changes.Count == 1 ? changes[0]
            : changes.Count == 0 ? "Saved"
            : $"{changes.Count} changes";
        versions.Add(new HistoryVersion { Time = _now(), By = by, Summary = summary, Changes = changes, Json = json });
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
            versions.Add(new HistoryVersion { Time = _now(), Summary = "Before the history started", Json = json });
        versions.Add(new HistoryVersion { Time = _now(), By = by, Summary = "Deleted", Json = json, Deleted = true });
        Write(id, versions);
    }

    /// <summary>The versions of an item, newest first (empty when it has none).</summary>
    public List<HistoryVersion> Versions(Guid id)
    {
        string path = PathOf(id);
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<HistoryVersion>>(File.ReadAllText(path), FileOptions) ?? new()).OrderByDescending(v => v.Time).ToList()
                : new List<HistoryVersion>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new List<HistoryVersion>();
        }
    }

    private string PathOf(Guid id) => Path.Combine(_folder, id.ToString("D") + ".json");

    /// <summary>Oldest first; keeps the last <see cref="MaxVersions"/>. A history that can't be written is only lost, never an error.</summary>
    private void Write(Guid id, List<HistoryVersion> versions)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string path = PathOf(id);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(versions.TakeLast(MaxVersions).ToList(), FileOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ================= What changed =================

    /// <summary>Names of objects that are keys (shown as "Ctrl + X").</summary>
    private static readonly HashSet<string> KeyObjects = new(StringComparer.Ordinal) { "Hotkey", "Source", "Target" };

    /// <summary>
    /// What changed between two versions of an item's JSON, for people: "Hotkey: F6 → F7", "Added action 3 (Click)",
    /// "Action 2 (Destroy): Delay 100 → 250".
    /// </summary>
    public static List<string> Changes(string beforeJson, string afterJson)
    {
        JsonObject? before = Parse(beforeJson);
        JsonObject? after = Parse(afterJson);
        var changes = new List<string>();
        if (before == null || after == null)
            return changes;

        var a = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var b = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Leaves(before, "", a);
        Leaves(after, "", b);

        // Actions: added and removed ones first, then each one's changes.
        var beforeActions = before["Actions"] as JsonArray;
        var afterActions = after["Actions"] as JsonArray;
        int beforeCount = beforeActions?.Count ?? 0, afterCount = afterActions?.Count ?? 0;
        for (int i = beforeCount; i < afterCount; i++)
            changes.Add($"Added action {i + 1}{ActionLabel(afterActions![i])}");
        for (int i = afterCount; i < beforeCount; i++)
            changes.Add($"Removed action {i + 1}{ActionLabel(beforeActions![i])}");

        foreach (string key in a.Keys.Union(b.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out string? old);
            b.TryGetValue(key, out string? now);
            if (old == now)
                continue;
            Match action = Regex.Match(key, @"^Actions\[(\d+)\]\.(.+)$");
            if (action.Success)
            {
                int index = int.Parse(action.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index >= Math.Min(beforeCount, afterCount))
                    continue; // added or removed above
                changes.Add($"Action {index + 1}{ActionLabel(afterActions![index])}: {Label(action.Groups[2].Value)} {old ?? "none"} → {now ?? "none"}");
            }
            else if (!key.StartsWith("Actions[", StringComparison.Ordinal) && key != "Id")
            {
                changes.Add($"{Label(key)}: {old ?? "none"} → {now ?? "none"}");
            }
        }
        return changes;
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

    /// <summary>" (Destroy)", the action's name, or " (Click)", its type; "" when it has neither.</summary>
    private static string ActionLabel(JsonNode? action)
    {
        if (action is not JsonObject o)
            return "";
        string? name = (o["Name"] as JsonValue)?.TryGetValue(out string? n) == true ? n : null;
        if (!string.IsNullOrWhiteSpace(name))
            return $" ({name})";
        string? type = (o["Type"] as JsonValue)?.TryGetValue(out string? t) == true ? t : null;
        return string.IsNullOrEmpty(type) ? "" : $" ({Label(type)})";
    }

    /// <summary>Every value of the item by its path ("Name", "FloatingButton.Size", "Actions[1].DelayMs"), as shown to people.</summary>
    private static void Leaves(JsonNode? node, string path, IDictionary<string, string> output)
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
                output[path] = $"a path of {array.Count} points{(array.Count > 0 ? $" ({Hash(array.ToJsonString())})" : "")}";
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                    Leaves(array[i], $"{path}[{i}]", output);
                if (array.Count == 0)
                    output[path] = "none";
                break;
            case JsonValue value:
                output[path] = Show(value);
                break;
            default:
                output[path] = "none";
                break;
        }
    }

    private static string KeysOf(JsonObject keys)
    {
        try
        {
            HotkeyBinding? binding = keys.Deserialize<HotkeyBinding>(CodeJson.Options);
            return binding is { IsSet: true } ? KeyNames.Format(binding) : "none";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return keys.ToJsonString();
        }
    }

    private static string Show(JsonValue value)
    {
        if (value.TryGetValue(out bool flag))
            return flag ? "On" : "Off";
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
