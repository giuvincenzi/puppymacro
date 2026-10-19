using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// One connection of an AI app, as the debug archive keeps it: <c>debug\ai\&lt;app&gt;\&lt;time&gt;-&lt;id&gt;\</c> with
/// <c>session.json</c> (who connected and how) and a folder per tool call (<see cref="DebugCall"/>). The id is
/// PuppyMacro's own: MCP gives no id of the AI app's chat, and one app may use one connection for all its chats.
/// </summary>
internal sealed class DebugSession
{
    private int _calls;

    private DebugSession(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>A new session's folder and its session.json, written by the debug writer.</summary>
    public static DebugSession Start(string app, object? info)
    {
        string id = Guid.NewGuid().ToString("N")[..4];
        string folder = Path.Combine(DebugStore.Root, "ai", DebugStore.Slug(app), $"{DebugStore.Stamp(DateTime.Now)}-{id}");
        var session = new DebugSession(folder);
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Session"] = id,
            ["App"] = app,
            ["Started"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["Client"] = info,
        }, DebugJson.Options);
        DebugStore.Write(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "session.json"), json);
        });
        return session;
    }

    /// <summary>For the calls made outside a connection (the unit tests): one session for the debug folder in use.</summary>
    public static DebugSession Detached
    {
        get
        {
            lock (DetachedSync)
            {
                if (_detached == null || !_detached.Folder.StartsWith(DebugStore.Root, StringComparison.OrdinalIgnoreCase))
                    _detached = Start("no-session", null);
                return _detached;
            }
        }
    }

    private static readonly object DetachedSync = new();
    private static DebugSession? _detached;

    /// <summary>
    /// A call's folder, &lt;nnn&gt;-&lt;tool&gt;[-&lt;subject&gt;], with its request.json: the tool, every argument and the
    /// app's _meta, as they came.
    /// </summary>
    public DebugCall NewCall(string tool, string? subject, JsonElement arguments, JsonElement? meta)
    {
        int number = Interlocked.Increment(ref _calls);
        string name = $"{number:000}-{tool}{(string.IsNullOrWhiteSpace(subject) ? "" : "-" + DebugStore.Slug(subject))}";
        string folder = Path.Combine(Folder, name);
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Tool"] = tool,
            ["Time"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["Arguments"] = arguments,
            ["Meta"] = meta,
        }, DebugJson.Options);
        DebugStore.Begin(folder);
        DebugStore.Write(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "request.json"), json);
        });
        return new DebugCall(folder);
    }
}

/// <summary>
/// One tool call's debug folder: request.json, response.json, and what the tool keeps there (a test's log and pictures, a
/// recording, the screen). The tools find it through <see cref="Current"/>, set by the server around the call.
/// </summary>
internal sealed class DebugCall
{
    private static readonly AsyncLocal<DebugCall?> CurrentCall = new();

    public DebugCall(string folder) => Folder = folder;

    public string Folder { get; }

    /// <summary>The call running on this flow (the server sets it), or null outside a connection.</summary>
    public static DebugCall? Current
    {
        get => CurrentCall.Value;
        set => CurrentCall.Value = value;
    }

    /// <summary>The call's response.json: what the AI app got, whole, and how long it took. Ends the call's operation.</summary>
    public void Answered(AiToolResult result, double ms, IEnumerable<object> links)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["IsError"] = result.IsError,
            ["Ms"] = Math.Round(ms, 1),
            ["Text"] = result.Text,
            ["Notes"] = result.Notes,
            ["Links"] = links,
        }, DebugJson.Options);
        string folder = Folder;
        DebugStore.Write(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "response.json"), json);
            DebugStore.End(folder);
        });
    }
}

internal static class DebugJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
