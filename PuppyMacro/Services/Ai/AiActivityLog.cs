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
}

/// <summary>
/// The AI apps' requests of the last <see cref="KeepDays"/> days, in <c>ai-activity.json</c> in the data folder.
/// Written from the AI server's threads and read by the UI: every member is thread-safe.
/// </summary>
internal sealed class AiActivityLog
{
    public const int KeepDays = 30;
    private const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private List<AiActivity> _entries = new();

    public AiActivityLog(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Raised after an entry is added or the history is cleared, on the thread that did it.</summary>
    public event Action? Changed;

    /// <summary>Reads the file (a missing or unreadable file is an empty history) and drops entries older than <see cref="KeepDays"/>.</summary>
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
        string detail = "", string? data = null)
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

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private List<AiActivity> Prune(List<AiActivity> entries)
    {
        DateTime oldest = _now().AddDays(-KeepDays);
        return entries.Where(e => e.Time >= oldest).OrderBy(e => e.Time).TakeLast(MaxEntries).ToList();
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
