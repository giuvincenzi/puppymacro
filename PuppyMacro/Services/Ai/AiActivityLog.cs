using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuppyMacro.Services.Ai;

/// <summary>What an AI app asked for, as the AI page's Recent activity and the AI activity window show it.</summary>
public enum AiActivityKind
{
    Connection,
    Read,
    Proposal,
    Interaction,
    Confirmation,
}

/// <summary>How a request ended. <see cref="None"/>: a request with nothing to decide (a read).</summary>
public enum AiActivityResult
{
    None,
    Saved,
    SavedWithChanges,
    Applied,
    Cancelled,
    TimedOut,
    Refused,
    Error,
}

/// <summary>One request of an AI app.</summary>
public sealed class AiActivity
{
    public DateTime Time { get; set; }

    /// <summary>The AI app, as it named itself when it connected ("Claude Desktop").</summary>
    public string App { get; set; } = "";

    public AiActivityKind Kind { get; set; }

    /// <summary>What it asked, in a few words ("New remap: Ctrl + X → A").</summary>
    public string Request { get; set; } = "";

    public AiActivityResult Result { get; set; }

    /// <summary>A short text after the result: the key pressed, the point picked, an error message.</summary>
    public string Detail { get; set; } = "";

    /// <summary>The JSON the AI proposed, or what was saved, for the details of the AI activity window.</summary>
    public string? Data { get; set; }

    /// <summary>The loop, macro or remap it changed or deleted (null: none, or the settings).</summary>
    public Guid? ItemId { get; set; }

    /// <summary>"loop", "macro", "remap" or "settings": what <see cref="Before"/> is.</summary>
    public string? ItemKind { get; set; }

    /// <summary>The item (or the settings) before this change, JSON: Restore puts it back. Null for something new.</summary>
    public string? Before { get; set; }

    /// <summary>The user put <see cref="Before"/> back.</summary>
    public bool Restored { get; set; }
}

/// <summary>
/// The AI apps' requests, the newest that fit in <see cref="MaxBytes"/> (a change keeps the item before and after it, so
/// size, not days or a count, is what fills the disk), in <c>ai-activity.json</c> in the data folder.
/// Written from the AI server's threads and read by the UI: every member is thread-safe.
/// </summary>
internal sealed class AiActivityLog
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Func<DateTime> _now;
    private readonly long _maxBytes;
    private readonly object _sync = new();
    private List<AiActivity> _entries = new();

    public AiActivityLog(string path, Func<DateTime>? now = null, long maxBytes = MaxBytes)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
        _maxBytes = maxBytes;
    }

    /// <summary>Raised after an entry is added or the history is cleared, on the thread that did it.</summary>
    public event Action? Changed;

    /// <summary>Reads the file (a missing or unreadable file is an empty history) and drops the oldest entries past <see cref="MaxBytes"/>.</summary>
    public void Load()
    {
        List<AiActivity> entries;
        try
        {
            entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<AiActivity>>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            entries = new();
        }
        lock (_sync)
            _entries = Prune(entries.Where(e => e != null).ToList());
    }

    /// <summary>Every entry, newest first.</summary>
    public List<AiActivity> Entries()
    {
        lock (_sync)
            return _entries.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>Adds an entry with the current time and saves the file.</summary>
    public AiActivity Add(string app, AiActivityKind kind, string request, AiActivityResult result = AiActivityResult.None,
        string detail = "", string? data = null, Guid? itemId = null, string? itemKind = null, string? before = null)
    {
        var entry = new AiActivity
        {
            Time = _now(),
            App = app,
            Kind = kind,
            Request = request,
            Result = result,
            Detail = detail,
            Data = data,
            ItemId = itemId,
            ItemKind = itemKind,
            Before = before,
        };
        lock (_sync)
        {
            _entries.Add(entry);
            _entries = Prune(_entries);
            Save();
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>The user restored what <paramref name="entry"/> changed: its Restore is done.</summary>
    public void MarkRestored(AiActivity entry)
    {
        lock (_sync)
        {
            entry.Restored = true;
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Oldest first, the newest entries whose JSON fits in the limit (the newest one always stays).</summary>
    private List<AiActivity> Prune(List<AiActivity> entries)
    {
        var kept = new List<AiActivity>();
        long size = 2; // [ ]
        foreach (AiActivity entry in entries.OrderByDescending(e => e.Time))
        {
            size += ItemHistory.SizeInList(JsonSerializer.Serialize(entry, JsonOptions));
            if (size > _maxBytes && kept.Count > 0)
                break;
            kept.Add(entry);
        }
        kept.Reverse();
        return kept;
    }

    /// <summary>Under <see cref="_sync"/>. A history that cannot be written stays in memory.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
