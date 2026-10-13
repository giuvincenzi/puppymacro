using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>The parts of the data folder that Export can leave out. Everything that is not History, AI activity or logs is Data.</summary>
[Flags]
public enum BackupParts
{
    None = 0,
    /// <summary>settings.json (settings, loops, remaps), macros\ and any other file of the data folder: always exported.</summary>
    Data = 1,
    History = 2,
    AiActivity = 4,
    Logs = 8,
    All = Data | History | AiActivity | Logs,
}

/// <summary>
/// Exports the data folder to one .puppymacro file (a zip) and imports it back. Export with every part is an identical
/// copy: every file of the data folder goes in, except the copies made before an import and temporary files.
/// </summary>
internal static class BackupService
{
    public const string Extension = ".puppymacro";

    /// <summary>Which parts the file holds, so an import replaces exactly those (a part with no files is still replaced).</summary>
    private const string ManifestName = "puppymacro-backup.json";

    private const string SafetyPrefix = "backup-before-import-";

    /// <summary>The part a file of the data folder belongs to (path relative to the folder, '/' separators); None when never exported.</summary>
    public static BackupParts PartOf(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (path == ManifestName || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return BackupParts.None;
        if (!path.Contains('/') && path.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase)
                                && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return BackupParts.None;
        if (path.StartsWith("history/", StringComparison.OrdinalIgnoreCase))
            return BackupParts.History;
        if (path.Equals("ai-activity.json", StringComparison.OrdinalIgnoreCase))
            return BackupParts.AiActivity;
        if (path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase))
            return BackupParts.Logs;
        return BackupParts.Data;
    }

    /// <summary>The size in bytes of each part in the data folder.</summary>
    public static Dictionary<BackupParts, long> Sizes(string dataFolder)
    {
        var sizes = new Dictionary<BackupParts, long>
        {
            [BackupParts.Data] = 0, [BackupParts.History] = 0, [BackupParts.AiActivity] = 0, [BackupParts.Logs] = 0,
        };
        foreach (var (file, part) in Files(dataFolder))
            sizes[part] += new FileInfo(file).Length;
        return sizes;
    }

    /// <summary>Saves the chosen parts (Data always) to <paramref name="path"/>.</summary>
    public static void Export(string dataFolder, string path, BackupParts parts)
    {
        parts |= BackupParts.Data;
        string temp = path + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var manifest = zip.CreateEntry(ManifestName);
            using (var stream = manifest.Open())
                JsonSerializer.Serialize(stream, new Manifest { Parts = Names(parts) });
            foreach (var (file, part) in Files(dataFolder))
            {
                if (parts.HasFlag(part))
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(dataFolder, file).Replace('\\', '/'));
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The parts a backup file holds. Files from before 1.17 have no list: settings.json and macros only (Data).</summary>
    public static BackupParts ReadParts(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadParts(zip);
    }

    /// <summary>
    /// Replaces the parts the file holds with its content and leaves the others as they are. The whole data folder is
    /// kept first as backup-before-import-{time}.puppymacro in it.
    /// </summary>
    public static void Import(string dataFolder, string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var parts = ReadParts(zip);

        string root = Path.GetFullPath(dataFolder);
        var entries = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0)
                continue; // a folder
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Strings.Backup_NotABackup);
            var part = PartOf(entry.FullName);
            if (part != BackupParts.None && parts.HasFlag(part))
                entries.Add((entry, target));
        }

        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "settings.json")))
            Export(root, Path.Combine(root, $"{SafetyPrefix}{DateTime.Now:yyyyMMdd-HHmmss}{Extension}"), BackupParts.All);

        foreach (var (file, part) in Files(root).ToList())
        {
            if (parts.HasFlag(part))
                File.Delete(file);
        }
        foreach (var (entry, target) in entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static BackupParts ReadParts(ZipArchive zip)
    {
        if (zip.GetEntry("settings.json") == null)
            throw new InvalidDataException(Strings.Backup_NotABackup);
        var manifest = zip.GetEntry(ManifestName);
        if (manifest == null)
            return BackupParts.Data;
        try
        {
            using var stream = manifest.Open();
            var names = JsonSerializer.Deserialize<Manifest>(stream)?.Parts ?? [];
            var parts = BackupParts.Data;
            foreach (string name in names)
            {
                if (Enum.TryParse(name, out BackupParts part) && part != BackupParts.All)
                    parts |= part;
            }
            return parts;
        }
        catch (JsonException)
        {
            throw new InvalidDataException(Strings.Backup_NotABackup);
        }
    }

    /// <summary>Every file of the data folder that Export can take, with its part.</summary>
    private static IEnumerable<(string File, BackupParts Part)> Files(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
            yield break;
        foreach (string file in Directory.EnumerateFiles(dataFolder, "*", SearchOption.AllDirectories))
        {
            var part = PartOf(Path.GetRelativePath(dataFolder, file));
            if (part != BackupParts.None)
                yield return (file, part);
        }
    }

    private static List<string> Names(BackupParts parts) =>
        [.. new[] { BackupParts.Data, BackupParts.History, BackupParts.AiActivity, BackupParts.Logs }
            .Where(p => parts.HasFlag(p)).Select(p => p.ToString())];

    private sealed class Manifest
    {
        public List<string> Parts { get; set; } = [];
    }
}
