using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using OpenCvSharp;

namespace PuppyMacro.Services;

/// <summary>
/// The log of one run of a macro with Debug on, or of a run an AI app tests: what each action did, what each If saw, why
/// it ended, and pictures of the screen when an If was not true and at the end. Always English (it is pasted into chats
/// and read by AI apps). Kept in <see cref="Root"/>, one folder per macro that holds only its last run; the whole folder
/// is deleted when PuppyMacro starts and closes. The macro only queues: one writer thread writes the lines and encodes the
/// pictures, so a run never waits for the disk.
/// </summary>
internal sealed class RunLog
{
    /// <summary>Past this size log.txt becomes log.1.txt (the one before is dropped): the newest lines stay.</summary>
    public const long MaxLogBytes = 2 * 1024 * 1024;

    public const string LogFile = "log.txt";

    /// <summary>
    /// %LocalAppData%\PuppyMacro\debug (PuppyMacro Dev for the development build): not user data, not in backups. The unit
    /// tests set a temporary folder.
    /// </summary>
    public static string Root { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        App.IsDevBuild ? "PuppyMacro Dev" : "PuppyMacro",
        "debug");

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private readonly Dictionary<string, Mat> _pictures = new();
    private int _pending;
    private readonly ManualResetEventSlim _idle = new(true);

    /// <summary>Starts the log of a run: the macro's folder is emptied first.</summary>
    public RunLog(Guid macroId, string macroName, string why, string? root = null)
    {
        Folder = FolderFor(macroId, root);
        string folder = Folder;
        Queue(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
        });
        Line($"Macro \"{macroName}\" starts ({why}) at {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
    }

    public string Folder { get; }

    public string LogPath => Path.Combine(Folder, LogFile);

    public static string FolderFor(Guid macroId, string? root = null) =>
        Path.Combine(root ?? Root, macroId.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>The last run's log of a macro, or null when it has none.</summary>
    public static string? Read(Guid macroId, string? root = null)
    {
        string file = Path.Combine(FolderFor(macroId, root), LogFile);
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool Exists(Guid macroId, string? root = null) => File.Exists(Path.Combine(FolderFor(macroId, root), LogFile));

    /// <summary>Deletes every macro's log (PuppyMacro starts or closes); a file in use stays.</summary>
    public static void DeleteAll(string? root = null)
    {
        try
        {
            if (Directory.Exists(root ?? Root))
                Directory.Delete(root ?? Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One line, after the time since the run started ("+00:01.250").</summary>
    public void Line(string text)
    {
        TimeSpan t = _clock.Elapsed;
        string line = $"+{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000} {text}{Environment.NewLine}";
        string file = LogPath;
        Queue(() =>
        {
            if (File.Exists(file) && new FileInfo(file).Length > MaxLogBytes)
                File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, "log.1.txt"), overwrite: true);
            File.AppendAllText(file, line);
        });
    }

    /// <summary>
    /// A picture of the screen saved as <paramref name="name"/> (a PNG). The log takes it and disposes it; a newer picture
    /// with the same name replaces one not written yet (an If false at every round keeps only its last picture).
    /// </summary>
    public void Picture(string name, Mat picture)
    {
        string file = Path.Combine(Folder, name);
        lock (_sync)
        {
            if (_pictures.Remove(name, out var older))
            {
                older.Dispose();
                _pictures[name] = picture;
                return; // already queued: it writes the newest
            }
            _pictures[name] = picture;
        }
        Queue(() =>
        {
            Mat? latest;
            lock (_sync)
                _pictures.Remove(name, out latest);
            if (latest == null)
                return;
            using (latest)
                Cv2.ImWrite(file, latest);
        });
    }

    /// <summary>
    /// A picture of every monitor as the run ended (end.png, end-monitor-2.png…), taken on the writer thread: the run does
    /// not wait for it. <paramref name="take"/> gives the pictures (the screen capture; a fake one in the tests).
    /// </summary>
    public void ScreenAtEnd(Func<IReadOnlyList<(Rect Bounds, Mat Picture)>> take)
    {
        string folder = Folder;
        Queue(() =>
        {
            var monitors = take();
            for (int i = 0; i < monitors.Count; i++)
            {
                var (bounds, picture) = monitors[i];
                string name = monitors.Count > 1 ? $"end-monitor-{i + 1}.png" : "end.png";
                using (picture)
                    Cv2.ImWrite(Path.Combine(folder, name), picture);
                TimeSpan t = _clock.Elapsed;
                File.AppendAllText(Path.Combine(folder, LogFile),
                    $"+{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000} screenshot at the end of {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}: {name}{Environment.NewLine}");
            }
        });
    }

    /// <summary>Waits until everything queued is written, at most <paramref name="timeout"/>.</summary>
    public bool WaitWritten(TimeSpan timeout) => _idle.Wait(timeout);

    private void Queue(Action write)
    {
        lock (_sync)
        {
            if (_pending++ == 0)
                _idle.Reset();
        }
        Writer.Add(() =>
        {
            try
            {
                write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OpenCVException)
            {
                // A log that cannot be written never stops the macro.
            }
            finally
            {
                lock (_sync)
                {
                    if (--_pending == 0)
                        _idle.Set();
                }
            }
        });
    }

    /// <summary>The one thread that writes every log, in order.</summary>
    private static class Writer
    {
        private static readonly BlockingCollection<Action> Work = new();

        static Writer()
        {
            new Thread(() =>
            {
                foreach (var write in Work.GetConsumingEnumerable())
                    write();
            })
            { IsBackground = true, Name = "PuppyMacro debug log", Priority = ThreadPriority.BelowNormal }.Start();
        }

        public static void Add(Action write) => Work.Add(write);
    }
}
