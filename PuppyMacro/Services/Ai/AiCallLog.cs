using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PuppyMacro.Services.Ai;

/// <summary>One tool call of an AI app, as <see cref="AiCallLog"/> keeps it: sizes and times, never the content.</summary>
/// <param name="Start">When PuppyMacro got the call. The gap from the previous call's end is the AI app's own time (the model thinking and writing).</param>
/// <param name="Ms">How long PuppyMacro took to answer (with a window for the user: until the user answered, or "waiting").</param>
/// <param name="In">Characters of the arguments the AI app sent.</param>
/// <param name="Out">Characters of the answer.</param>
/// <param name="Result">ok, error, or failed (an exception or the call cancelled).</param>
/// <param name="Ops">patch_item: how many operations.</param>
internal sealed record AiCall(DateTime Start, double Ms, string App, string Tool, int In, int Out, string Result, int? Ops = null);

/// <summary>
/// Every tool call of every AI app, for debugging (where the time goes, which tools an AI uses): one JSON line per call in
/// logs\ai-calls.jsonl, at most <see cref="MaxBytes"/> (the oldest lines go). Not user data: no arguments or answers are
/// written, only their sizes; a log that can't be written is only lost, never an error.
/// </summary>
internal sealed class AiCallLog
{
    public const long MaxBytes = 5L * 1024 * 1024;

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Encoding Utf8 = new UTF8Encoding(false); // no BOM: one JSON object per line

    private readonly string _file;
    private readonly long _maxBytes;
    private readonly object _sync = new();

    public AiCallLog(string file, long maxBytes = MaxBytes)
    {
        _file = file;
        _maxBytes = maxBytes;
    }

    public void Add(AiCall call)
    {
        string line = JsonSerializer.Serialize(call, LineOptions) + "\n";
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, line, Utf8);
                if (new FileInfo(_file).Length > _maxBytes)
                    Trim();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Keeps the newest lines that fill three quarters of the limit, so it is not trimmed again at every call.</summary>
    private void Trim()
    {
        string[] lines = File.ReadAllLines(_file, Utf8);
        long budget = _maxBytes * 3 / 4, size = 0;
        int keep = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            size += Utf8.GetByteCount(lines[i]) + 1;
            if (size > budget)
                break;
            keep++;
        }
        File.WriteAllText(_file, string.Concat(lines.Skip(lines.Length - keep).Select(l => l + "\n")), Utf8);
    }

    /// <summary>The calls kept, oldest first (lines that can't be read are skipped).</summary>
    public AiCall[] Entries()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_file))
                    return Array.Empty<AiCall>();
                return File.ReadAllLines(_file, Utf8).Select(Read).OfType<AiCall>().ToArray();
            }
            catch (IOException)
            {
                return Array.Empty<AiCall>();
            }
        }
    }

    private static AiCall? Read(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AiCall>(line, LineOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
