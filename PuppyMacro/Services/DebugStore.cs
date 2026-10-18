using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PuppyMacro.Services;

/// <summary>
/// The debug archive: everything that shows how an AI app's requests and the macros' runs went, in
/// <c>%LocalAppData%\PuppyMacro\debug</c> (not user data, not in backups). One folder per operation:
/// <c>ai\&lt;app&gt;\&lt;session&gt;\&lt;nnn&gt;-&lt;tool&gt;[-&lt;macro&gt;]\</c> for an AI app's call and
/// <c>user\macro\&lt;time&gt;-&lt;macro&gt;\</c> for a run the user started with Debug on. Names have no spaces and sort by time,
/// so they are also the paths of MCP resources (<see cref="UriOf"/>). An operation is kept <see cref="Life"/>; the cleaner
/// runs at start and every hour, never on an operation in progress and never when PuppyMacro closes, so a crash loses
/// nothing. Everything is written by one background thread (<see cref="Write"/>): nothing that plays waits for the disk.
/// </summary>
internal static class DebugStore
{
    /// <summary>How long an operation's folder is kept (the user's choice for debug data).</summary>
    public static readonly TimeSpan Life = TimeSpan.FromDays(7);

    public const string UriPrefix = "puppymacro://debug/";

    /// <summary>The folder of the debug data (PuppyMacro Dev for the development build). The unit tests set a temporary one.</summary>
    public static string Root { get; internal set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        App.IsDevBuild ? "PuppyMacro Dev" : "PuppyMacro",
        "debug");

    private static readonly ConcurrentDictionary<string, byte> InProgress = new(StringComparer.OrdinalIgnoreCase);

    // ================= Names =================

    /// <summary>"20261018-071446": sorts as text, no character Windows refuses.</summary>
    public static string Stamp(DateTime local) => local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>A name as a path part: lowercase ASCII letters and digits, the rest one hyphen, at most 40 characters.</summary>
    public static string Slug(string? text)
    {
        var slug = new StringBuilder();
        foreach (char c in (text ?? "").ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        string result = slug.ToString().Trim('-');
        if (result.Length > 40)
            result = result[..40].TrimEnd('-');
        return result.Length == 0 ? "item" : result;
    }

    /// <summary>
    /// The folder of a run the user starts with Debug on: user\macro\&lt;time with milliseconds&gt;-&lt;macro&gt;. Made by
    /// the writer thread (a hotkey starts a run on the input thread, which must never wait for the disk).
    /// </summary>
    public static string NewUserMacroRun(string macroName)
    {
        DateTime now = DateTime.Now;
        string folder = Path.Combine(Root, "user", "macro", $"{Stamp(now)}-{now:fff}-{Slug(macroName)}");
        Write(() => Directory.CreateDirectory(folder));
        return folder;
    }

    // ================= Resources =================

    /// <summary>The MCP resource URI of a file of the archive: puppymacro://debug/ai/claude-desktop/…/log.txt.</summary>
    public static string UriOf(string path) =>
        UriPrefix + Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>The file of a resource URI, or null when it is not a file inside the archive (also "..", other schemes).</summary>
    public static string? PathOf(string uri)
    {
        if (!uri.StartsWith(UriPrefix, StringComparison.Ordinal))
            return null;
        string relative = Uri.UnescapeDataString(uri[UriPrefix.Length..]);
        if (relative.Length == 0 || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".."))
            return null;
        string root = Path.GetFullPath(Root);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".json" => "application/json",
        _ => "text/plain",
    };

    // ================= Operations in progress =================

    /// <summary>An operation writes in <paramref name="folder"/>: the cleaner leaves it alone until <see cref="End"/>.</summary>
    public static void Begin(string folder) => InProgress[Path.GetFullPath(folder)] = 0;

    public static void End(string folder) => InProgress.TryRemove(Path.GetFullPath(folder), out _);

    // ================= Writing =================

    private static readonly BlockingCollection<Action> Work = new();
    private static int _pending;
    private static readonly ManualResetEventSlim Idle = new(true);

    static DebugStore()
    {
        new Thread(() =>
        {
            foreach (var write in Work.GetConsumingEnumerable())
            {
                try
                {
                    write();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Debug data that cannot be written never stops what is being debugged.
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pending) == 0)
                        Idle.Set();
                }
            }
        })
        { IsBackground = true, Name = "PuppyMacro debug writer" }.Start();
    }

    /// <summary>Queues a write for the debug writer thread, in order.</summary>
    public static void Write(Action write)
    {
        if (Interlocked.Increment(ref _pending) == 1)
            Idle.Reset();
        Work.Add(write);
    }

    /// <summary>Waits until everything queued is written, at most <paramref name="timeout"/>.</summary>
    public static bool WaitWritten(TimeSpan timeout) => Idle.Wait(timeout);

    // ================= Keeping =================

    /// <summary>The operations' folders: those with files of their own (an AI call, a run).</summary>
    private static IEnumerable<string> Operations(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Where(d => Directory.EnumerateFiles(d).Any(f => !Path.GetFileName(f).Equals("session.json", StringComparison.OrdinalIgnoreCase))
                            && !Directory.EnumerateDirectories(d).Any())
            : Enumerable.Empty<string>();

    /// <summary>
    /// Deletes the operations last written more than <paramref name="age"/> before <paramref name="nowUtc"/> (all of them
    /// with <see cref="TimeSpan.Zero"/>), never one in progress, then the folders left empty. A folder that can't be deleted
    /// (a file open) stays for the next time.
    /// </summary>
    public static void DeleteOlderThan(TimeSpan age, DateTime nowUtc)
    {
        string root = Root;
        foreach (string folder in Operations(root).ToList())
        {
            if (InProgress.ContainsKey(Path.GetFullPath(folder)))
                continue;
            DateTime last = Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
            if (nowUtc - last < age)
                continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        DeleteEmpty(root, root, nowUtc, age);
    }

    /// <summary>Folders with nothing left in them (a session with only its session.json once old, an app with no session).</summary>
    private static void DeleteEmpty(string folder, string root, DateTime nowUtc, TimeSpan age)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (string child in Directory.EnumerateDirectories(folder).ToList())
            DeleteEmpty(child, root, nowUtc, age);
        if (folder == root || InProgress.ContainsKey(Path.GetFullPath(folder)))
            return;
        var files = Directory.EnumerateFiles(folder).ToList();
        bool onlySession = files.All(f => Path.GetFileName(f).Equals("session.json", StringComparison.OrdinalIgnoreCase))
                           && files.All(f => nowUtc - File.GetLastWriteTimeUtc(f) >= age);
        if (!Directory.EnumerateDirectories(folder).Any() && onlySession)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The space the archive takes, in bytes.</summary>
    public static long Size()
    {
        try
        {
            return Directory.Exists(Root)
                ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ================= A macro's runs =================

    /// <summary>The folder of a macro's last run with a log (its own with Debug, or an AI app's test), or null.</summary>
    public static string? LastRunOf(Guid macroId)
    {
        if (!Directory.Exists(Root))
            return null;
        string id = macroId.ToString("D", CultureInfo.InvariantCulture);
        return Directory.EnumerateFiles(Root, RunLog.RunFile, SearchOption.AllDirectories)
            .Where(f => File.ReadLines(f).Take(4).Any(l => l.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .FirstOrDefault();
    }
}
