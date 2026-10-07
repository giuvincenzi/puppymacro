using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PuppyMacro.Services;

/// <summary>Exports all data (settings, loops, remaps, macros) to one .puppymacro file and imports it back.</summary>
internal static class BackupService
{
    public const string Extension = ".puppymacro";

    public static void Export(string path)
    {
        string temp = path + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(AppPaths.SettingsFile, "settings.json");
            if (Directory.Exists(AppPaths.MacrosFolder))
            {
                foreach (string file in Directory.GetFiles(AppPaths.MacrosFolder, "*.json"))
                    zip.CreateEntryFromFile(file, "macros/" + Path.GetFileName(file));
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Replaces the current data with the file's content. The current data is kept first as
    /// backup-before-import-{time}.puppymacro in the data folder.
    /// </summary>
    public static void Import(string path)
    {
        using (var check = ZipFile.OpenRead(path))
        {
            if (check.GetEntry("settings.json") == null)
                throw new InvalidDataException("This file is not a PuppyMacro backup (settings.json is missing).");
        }

        string safety = Path.Combine(AppPaths.DataFolder, $"backup-before-import-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        if (File.Exists(AppPaths.SettingsFile))
            Export(safety);

        if (Directory.Exists(AppPaths.MacrosFolder))
        {
            foreach (string file in Directory.GetFiles(AppPaths.MacrosFolder, "*.json"))
                File.Delete(file);
        }
        Directory.CreateDirectory(AppPaths.MacrosFolder);

        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            string target = entry.FullName == "settings.json"
                ? AppPaths.SettingsFile
                : entry.FullName.StartsWith("macros/", StringComparison.Ordinal)
                    ? Path.Combine(AppPaths.MacrosFolder, Path.GetFileName(entry.FullName))
                    : "";
            if (target.Length > 0)
                entry.ExtractToFile(target, overwrite: true);
        }
    }
}
