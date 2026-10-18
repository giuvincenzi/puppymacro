using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenCvSharp;

namespace PuppyMacro.Services;

/// <summary>
/// The log of one operation in its debug folder (<see cref="DebugStore"/>): a macro's run (Debug on, or an AI app's test)
/// or an AI app's recording. Each event has a number, its time from the start and the pictures of the screen taken just
/// before it, named by that number ("0002 +00:00.512 If … → 0002.png", "0002-m2.png" for a second monitor), so the
/// log and its pictures go together without an index. Always English: it is pasted into chats and read by AI apps.
/// Everything is queued to the debug writer: the macro never waits for the disk.
/// </summary>
internal sealed class RunLog
{
    public const string LogFile = "log.txt";

    /// <summary>What the run is (its macro's Id and name, when it started and why it ended): the first lines name the macro.</summary>
    public const string RunFile = "run.json";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private readonly Dictionary<string, object?> _run;
    private int _events;

    /// <param name="folder">The operation's folder, already made.</param>
    /// <param name="macro">The macro that runs, or null (a recording).</param>
    /// <param name="what">What it is: "a run started by the user", "a test by Claude Desktop", "a recording".</param>
    public RunLog(string folder, Models.MacroDefinition? macro, string what)
    {
        Folder = folder;
        DebugStore.Begin(folder);
        _run = new Dictionary<string, object?>
        {
            ["MacroId"] = macro?.Id,
            ["Macro"] = macro?.Name,
            ["What"] = what,
            ["Started"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        };
        WriteRun();
        Line(macro == null
            ? $"Starts: {what}"
            : $"Macro \"{macro.Name}\" ({macro.Id:D}) starts: {what}");
    }

    public string Folder { get; }

    public string LogPath => Path.Combine(Folder, LogFile);

    /// <summary>
    /// An event with the pictures of the screen just before it (each monitor; the log takes and disposes them): its line
    /// names them. Answers the event's number.
    /// </summary>
    public int Event(string text, IReadOnlyList<(Rect Bounds, Mat Picture)>? pictures = null)
    {
        int number;
        lock (_sync)
            number = ++_events;
        var names = new List<string>();
        for (int i = 0; i < (pictures?.Count ?? 0); i++)
        {
            string name = $"{number:0000}{(i == 0 ? "" : $"-m{i + 1}")}.png";
            names.Add(name);
            Mat picture = pictures![i].Picture;
            string file = Path.Combine(Folder, name);
            DebugStore.Write(() =>
            {
                using (picture)
                    Cv2.ImWrite(file, picture);
            });
        }
        Append($"{number:0000} {Time()} {text}{(names.Count == 0 ? "" : " → " + string.Join(", ", names))}");
        return number;
    }

    /// <summary>A detail of the event before it (what a condition saw), without a number.</summary>
    public void Line(string text) => Append($"     {Time()} {text}");

    /// <summary>The run ended: its last event (with the screen at the end) and why, also in <see cref="RunFile"/>.</summary>
    public void End(string why, IReadOnlyList<(Rect Bounds, Mat Picture)>? pictures = null)
    {
        Event($"Ends: {why}", pictures);
        lock (_sync)
        {
            _run["Ended"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            _run["EndReason"] = why;
        }
        WriteRun();
        string folder = Folder;
        DebugStore.Write(() => DebugStore.End(folder));
    }

    /// <summary>Waits until everything queued is written, at most <paramref name="timeout"/>.</summary>
    public static bool WaitWritten(TimeSpan timeout) => DebugStore.WaitWritten(timeout);

    /// <summary>A run's log, or null when it is not there any more.</summary>
    public static string? Read(string? folder)
    {
        if (folder == null)
            return null;
        try
        {
            string file = Path.Combine(folder, LogFile);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string Time()
    {
        TimeSpan t = _clock.Elapsed;
        return $"+{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private void Append(string line)
    {
        string file = LogPath;
        DebugStore.Write(() => File.AppendAllText(file, line + Environment.NewLine));
    }

    private void WriteRun()
    {
        string json;
        lock (_sync)
            json = JsonSerializer.Serialize(_run, new JsonSerializerOptions { WriteIndented = true });
        string file = Path.Combine(Folder, RunFile);
        DebugStore.Write(() => File.WriteAllText(file, json));
    }
}
