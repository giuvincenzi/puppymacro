using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>Import of a backup that fails part way, and backups of names and bytes the other tests don't use (<see cref="BackupService"/>).</summary>
public class BackupServiceEdgesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Data => _folder.File("data");

    private void Write(string relative, string text)
    {
        string path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(Data, relative));

    /// <summary>A backup whose second file can't be read back (its data damaged on a USB stick or a download).</summary>
    private string DamagedBackup()
    {
        string path = _folder.File("damaged.puppymacro");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entryName, text) in new[] { ("settings.json", "settings of the backup"), ("macros/b.json", "macro b of the backup") })
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }
        // The compression method of macros/b.json, in its local and central headers, becomes one no reader knows.
        byte[] bytes = File.ReadAllBytes(path);
        byte[] name = System.Text.Encoding.UTF8.GetBytes("macros/b.json");
        for (int i = 0; i + 46 < bytes.Length; i++)
        {
            uint signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50 && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
                BitConverter.GetBytes((ushort)99).CopyTo(bytes, i + 8);
            else if (signature == 0x02014b50 && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
                BitConverter.GetBytes((ushort)99).CopyTo(bytes, i + 10);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Import deletes the parts it replaces, then extracts the file's entries one by one. When an entry can't be extracted
    /// (damaged data, a full disk), the exception leaves the data folder half replaced: the backup's settings.json, the
    /// user's macros, images and library.json deleted, the rest of the backup missing. The window says "import failed" and
    /// PuppyMacro goes on with the old data in memory, which its next saves mix with the half-imported files; the macros it
    /// deleted are gone at the next start. Only the copy made before the import (backup-before-import-*.puppymacro, which
    /// nothing in the app offers to restore) still has them.
    /// </summary>
    [Fact]
    public void An_import_that_fails_part_way_leaves_the_data_as_it_was()
    {
        Write("settings.json", "my settings");
        Write("macros/a.json", "my macro a");
        Write("library.json", "my library");
        string backup = DamagedBackup();

        var exception = Record.Exception(() => BackupService.Import(Data, backup));

        Assert.IsType<InvalidDataException>(exception); // the window shows it as a failed import
        Assert.Equal("my settings", Read("settings.json"));
        Assert.Equal("my macro a", Read("macros/a.json"));
        Assert.Equal("my library", Read("library.json"));
    }

    /// <summary>The same when a file of the data folder can't be deleted (read only, copied from a disc or a sync tool).</summary>
    [Fact]
    public void An_import_that_can_t_delete_a_file_leaves_the_data_as_it_was()
    {
        Write("settings.json", "my settings");
        Write("macros/a.json", "my macro a");
        Write("macros/z.json", "my macro z");
        string zz = Path.Combine(Data, "macros", "z.json");
        File.SetAttributes(zz, FileAttributes.ReadOnly);
        string backup = _folder.File("good.puppymacro");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("settings.json").Open()))
                writer.Write("settings of the backup");
            using (var writer = new StreamWriter(zip.CreateEntry("macros/b.json").Open()))
                writer.Write("macro b of the backup");
        }

        try
        {
            var exception = Record.Exception(() => BackupService.Import(Data, backup));

            Assert.IsType<UnauthorizedAccessException>(exception); // the window shows it as a failed import
            Assert.True(File.Exists(Path.Combine(Data, "settings.json")), "settings.json was deleted and nothing put in its place");
            Assert.Equal("my settings", Read("settings.json"));
            Assert.Equal("my macro a", Read("macros/a.json"));
        }
        finally
        {
            File.SetAttributes(zz, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// Every file comes back byte for byte, with names in any script and the images' binary content, also a name with
    /// spaces and a dot inside; nested folders keep their place.
    /// </summary>
    [Fact]
    public void Names_in_any_script_and_binary_files_come_back_byte_for_byte()
    {
        Write("settings.json", "{ \"Language\": \"ar\" }");
        Write("macros/Pesca è 釣り – صيد.json", "{ \"Name\": \"Pesca è 釣り – صيد\" }");
        Write("macros/name with spaces.v2.json", "{ }");
        Write("history/ä/ö.json", "[]");
        byte[] png = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
        Directory.CreateDirectory(Path.Combine(Data, "images"));
        File.WriteAllBytes(Path.Combine(Data, "images", new string('c', 64) + ".png"), png);
        string file = _folder.File("all.puppymacro");
        BackupService.Export(Data, file, BackupParts.All);

        string other = _folder.File("other");
        Directory.CreateDirectory(other);
        BackupService.Import(other, file);

        Assert.Equal(png, File.ReadAllBytes(Path.Combine(other, "images", new string('c', 64) + ".png")));
        Assert.Equal("{ \"Name\": \"Pesca è 釣り – صيد\" }", File.ReadAllText(Path.Combine(other, "macros", "Pesca è 釣り – صيد.json")));
        Assert.True(File.Exists(Path.Combine(other, "macros", "name with spaces.v2.json")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(other, "history", "ä", "ö.json")));
    }
}
