using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>Characters no name of the archive has: a stream name's ':' (NTFS "x.png::$DATA"), wildcards, control characters.</summary>
    private static readonly char[] NotInNames = Path.GetInvalidFileNameChars()
        .Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' })
        .Concat(Enumerable.Range(0, 32).Select(c => (char)c))
        .Distinct()
        .ToArray();

    /// <summary>
    /// The file of a resource URI, or null when it is not a plain file inside the archive: other schemes, "..", a stream
    /// name, a character no file name has, a name Windows reads as another one (a short 8.3 name, trailing dots or
    /// spaces), a folder, or a link (a reparse point) on the way. The path given back is the file's own, so its type is
    /// read from its real name.
    /// </summary>
    public static string? PathOf(string uri)
    {
        if (uri == null || !uri.StartsWith(UriPrefix, StringComparison.Ordinal))
            return null;
        string relative = Uri.UnescapeDataString(uri[UriPrefix.Length..]);
        if (relative.Length == 0)
            return null;
        string[] parts = relative.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(NotInNames) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            return null;
        try
        {
            var folder = new DirectoryInfo(Path.GetFullPath(Root));
            if (!folder.Exists)
                return null;
            // Each part, from the archive's folder down, must be an entry with exactly that name (not a short name Windows
            // also matches) and must not be a link to somewhere else.
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                FileSystemInfo? entry = folder.EnumerateFileSystemInfos(parts[i])
                    .FirstOrDefault(e => e.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return null;
                if (last)
                    return entry is FileInfo file && !file.Attributes.HasFlag(FileAttributes.Directory) ? file.FullName : null;
                if (entry is not DirectoryInfo child)
                    return null;
                folder = child;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".json" => "application/json",
        _ => "text/plain",
    };

    /// <summary>
    /// The file of a resource URI with its type, or null (<see cref="PathOf"/>): the one way to a file of the archive, for
    /// resources/read and the read_resource tool alike (through <c>AiTools.OpenResource</c>).
    /// </summary>
    public static DebugFile? Open(string uri) => PathOf(uri) is { } path ? new DebugFile(path, UriOf(path), MimeOf(path)) : null;

    /// <summary>
    /// What a linked file is, for the AI app before it opens it (MCP's description of a resource link): what it holds, from
    /// the archive's names, then its size, a picture's in pixels and a text's in lines.
    /// </summary>
    public static string Describe(string path)
    {
        string name = Path.GetFileName(path);
        Match monitor = Regex.Match(name, @"^monitor-(\d+)\.png$");
        Match numbered = Regex.Match(name, @"^(\d{4})(?:-m(\d+))?\.png$");
        string what = name switch
        {
            _ when monitor.Success => $"monitor {monitor.Groups[1].Value}",
            _ when numbered.Success => $"the screen just before event {numbered.Groups[1].Value}" +
                (numbered.Groups[2].Success ? $", monitor {numbered.Groups[2].Value}" : ""),
            RunLog.LogFile => "the log",
            RunLog.RunFile => "the run: macro, start and how it ended",
            "actions.json" => "the recorded actions",
            "screen.json" => "the monitors' bounds",
            _ => name,
        };
        try
        {
            if (MimeOf(path).StartsWith("image/", StringComparison.Ordinal))
                return PngSize(path) is var (width, height) ? $"{what}, PNG, {width} × {height} px" : $"{what}, PNG";
            return $"{what}, {File.ReadLines(path).Count()} lines";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return what;
        }
    }

    /// <summary>A file's size for the AI app: "320 B", "562 KB", "10.7 MB" (invariant numbers, 1 KB = 1024 bytes).</summary>
    public static string SizeText(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1024.0)} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / (1024.0 * 1024), 1)} MB"),
    };

    /// <summary>A PNG's width and height from its header, without decoding it; null when it is not a PNG.</summary>
    public static (int Width, int Height)? PngSize(string path)
    {
        Span<byte> head = stackalloc byte[24];
        using (FileStream file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
                return null;
        }
        // The signature, then the IHDR chunk: its width and height are the first 8 bytes of its data, big-endian.
        ReadOnlySpan<byte> signature = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        if (!head[..8].SequenceEqual(signature) || !head[12..16].SequenceEqual("IHDR"u8))
            return null;
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..20]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..24]));
    }

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

    /// <summary>Held by the cleaner that runs: two never run at once, as one would list what the other deletes.</summary>
    private static readonly object Cleaning = new();

    /// <summary>
    /// The operations' folders: those with files of their own (an AI call, a run). A folder that can't be listed (gone
    /// meanwhile, deleted in File Explorer; access refused) is left out.
    /// </summary>
    private static List<string> Operations(string root)
    {
        var found = new List<string>();
        if (Directory.Exists(root))
            AddOperations(root, found);
        return found;
    }

    private static void AddOperations(string folder, List<string> found)
    {
        foreach (string child in Listed(() => Directory.GetDirectories(folder)))
        {
            string[] inside = Listed(() => Directory.GetDirectories(child));
            if (inside.Length > 0)
                AddOperations(child, found);
            else if (Listed(() => Directory.GetFiles(child)).Any(f => !Path.GetFileName(f).Equals("session.json", StringComparison.OrdinalIgnoreCase)))
                found.Add(child);
        }
    }

    /// <summary>What a folder holds, or nothing when it can't be listed (gone meanwhile, access refused).</summary>
    private static string[] Listed(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deletes the operations last written more than <paramref name="age"/> before <paramref name="nowUtc"/> (all of them
    /// with <see cref="TimeSpan.Zero"/>), never one in progress, then the folders left empty. A folder that can't be read or
    /// deleted (a file open, gone meanwhile) stays for the next time. Waits while another cleaner runs.
    /// </summary>
    public static void DeleteOlderThan(TimeSpan age, DateTime nowUtc)
    {
        lock (Cleaning)
            Clean(age, nowUtc);
    }

    /// <summary>The same, only when no other cleaner runs (the hourly one: the other is already deleting). False when it did not run.</summary>
    public static bool TryDeleteOlderThan(TimeSpan age, DateTime nowUtc)
    {
        if (!Monitor.TryEnter(Cleaning))
            return false;
        try
        {
            Clean(age, nowUtc);
            return true;
        }
        finally
        {
            Monitor.Exit(Cleaning);
        }
    }

    private static void Clean(TimeSpan age, DateTime nowUtc)
    {
        string root = Root;
        foreach (string folder in Operations(root))
        {
            if (InProgress.ContainsKey(Path.GetFullPath(folder)))
                continue;
            try
            {
                DateTime last = Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
                if (nowUtc - last < age)
                    continue;
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
        foreach (string child in Listed(() => Directory.GetDirectories(folder)))
            DeleteEmpty(child, root, nowUtc, age);
        if (folder == root || InProgress.ContainsKey(Path.GetFullPath(folder)))
            return;
        try
        {
            var files = Directory.EnumerateFiles(folder).ToList();
            bool onlySession = files.All(f => Path.GetFileName(f).Equals("session.json", StringComparison.OrdinalIgnoreCase))
                               && files.All(f => nowUtc - File.GetLastWriteTimeUtc(f) >= age);
            if (!Directory.EnumerateDirectories(folder).Any() && onlySession)
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

/// <summary>A file of the debug archive as an MCP resource: where it is, its URI and its type.</summary>
internal sealed record DebugFile(string Path, string Uri, string MimeType)
{
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.Ordinal);
}
