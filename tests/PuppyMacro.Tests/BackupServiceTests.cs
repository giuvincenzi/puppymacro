using System.IO;
using System.IO.Compression;
using System.Linq;
using PuppyMacro.Services;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

public class BackupServiceTests
{
    /// <summary>A data folder with a file of every part, a copy made before an import and a temporary file.</summary>
    private static string FillDataFolder(TempFolder temp, string name = "data")
    {
        string data = Path.Combine(temp.Path, name);
        Write(data, "settings.json", "{\"settings\":1}");
        Write(data, "macros/a.json", "macro a");
        Write(data, "history/a.json", "history a");
        Write(data, "ai-activity.json", "activity");
        Write(data, "logs/ai-calls.jsonl", "calls");
        Write(data, "future/new-thing.json", "a file a later version adds");
        Write(data, "backup-before-import-20260101-120000.puppymacro", "old copy");
        Write(data, "settings.json.tmp", "half written");
        return data;
    }

    private static void Write(string folder, string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string folder, string relative) => File.ReadAllText(Path.Combine(folder, relative));

    private static string[] Entries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName).OrderBy(n => n)];
    }

    [Theory]
    [InlineData("settings.json", BackupParts.Data)]
    [InlineData("macros/a.json", BackupParts.Data)]
    [InlineData("future/new-thing.json", BackupParts.Data)]
    [InlineData(@"history\a.json", BackupParts.History)]
    [InlineData("ai-activity.json", BackupParts.AiActivity)]
    [InlineData("logs/ai-calls.jsonl", BackupParts.Logs)]
    [InlineData("backup-before-import-20260101-120000.puppymacro", BackupParts.None)]
    [InlineData("history/a.json.tmp", BackupParts.None)]
    [InlineData("puppymacro-backup.json", BackupParts.None)]
    public void Every_file_belongs_to_a_part_or_is_never_exported(string path, BackupParts expected) =>
        Assert.Equal(expected, BackupService.PartOf(path));

    [Fact]
    public void Exporting_everything_takes_every_file_but_the_copies_before_an_import_and_temporary_files()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);
        string file = temp.File("all.puppymacro");

        BackupService.Export(data, file, BackupParts.All);

        Assert.Equal(
            ["ai-activity.json", "future/new-thing.json", "history/a.json", "logs/ai-calls.jsonl", "macros/a.json",
             "puppymacro-backup.json", "settings.json"],
            Entries(file));
        Assert.Equal(BackupParts.All, BackupService.ReadParts(file));
    }

    [Fact]
    public void A_partial_export_always_keeps_the_data()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);
        string file = temp.File("some.puppymacro");

        BackupService.Export(data, file, BackupParts.History);

        Assert.Equal(["future/new-thing.json", "history/a.json", "macros/a.json", "puppymacro-backup.json", "settings.json"],
            Entries(file));
        Assert.Equal(BackupParts.Data | BackupParts.History, BackupService.ReadParts(file));
    }

    [Fact]
    public void Sizes_count_each_part()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);

        var sizes = BackupService.Sizes(data);

        Assert.Equal("{\"settings\":1}".Length + "macro a".Length + "a file a later version adds".Length, sizes[BackupParts.Data]);
        Assert.Equal("history a".Length, sizes[BackupParts.History]);
        Assert.Equal("activity".Length, sizes[BackupParts.AiActivity]);
        Assert.Equal("calls".Length, sizes[BackupParts.Logs]);
    }

    [Fact]
    public void Importing_everything_on_another_PC_gives_an_identical_copy()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);
        string file = temp.File("all.puppymacro");
        BackupService.Export(data, file, BackupParts.All);

        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");
        Write(other, "macros/b.json", "macro b");
        Write(other, "history/b.json", "history b");
        Write(other, "logs/old.log", "old");
        BackupService.Import(other, file);

        Assert.Equal("{\"settings\":1}", Read(other, "settings.json"));
        Assert.Equal("macro a", Read(other, "macros/a.json"));
        Assert.Equal("history a", Read(other, "history/a.json"));
        Assert.Equal("activity", Read(other, "ai-activity.json"));
        Assert.Equal("calls", Read(other, "logs/ai-calls.jsonl"));
        Assert.Equal("a file a later version adds", Read(other, "future/new-thing.json"));
        Assert.False(File.Exists(Path.Combine(other, "macros/b.json")));
        Assert.False(File.Exists(Path.Combine(other, "history/b.json")));
        Assert.False(File.Exists(Path.Combine(other, "logs/old.log")));
        Assert.False(File.Exists(Path.Combine(other, "puppymacro-backup.json")));
    }

    [Fact]
    public void Import_keeps_a_whole_copy_of_the_data_first()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);
        string file = temp.File("all.puppymacro");
        BackupService.Export(data, file, BackupParts.All);

        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");
        Write(other, "history/b.json", "history b");
        BackupService.Import(other, file);

        string copy = Directory.GetFiles(other, "backup-before-import-*.puppymacro").Single();
        Assert.Equal(["history/b.json", "puppymacro-backup.json", "settings.json"], Entries(copy));
        Assert.Equal(BackupParts.All, BackupService.ReadParts(copy));
    }

    [Fact]
    public void Import_replaces_only_the_parts_the_file_has()
    {
        using var temp = new TempFolder();
        string data = FillDataFolder(temp);
        string file = temp.File("some.puppymacro");
        BackupService.Export(data, file, BackupParts.Data);

        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");
        Write(other, "macros/b.json", "macro b");
        Write(other, "history/b.json", "history b");
        Write(other, "ai-activity.json", "other activity");
        BackupService.Import(other, file);

        Assert.Equal("{\"settings\":1}", Read(other, "settings.json"));
        Assert.False(File.Exists(Path.Combine(other, "macros/b.json")));
        Assert.Equal("history b", Read(other, "history/b.json"));
        Assert.Equal("other activity", Read(other, "ai-activity.json"));
        Assert.False(File.Exists(Path.Combine(other, "history/a.json")));
    }

    [Fact]
    public void An_exported_part_with_no_files_still_replaces_that_part()
    {
        using var temp = new TempFolder();
        string data = Path.Combine(temp.Path, "data");
        Write(data, "settings.json", "{}");
        string file = temp.File("all.puppymacro");
        BackupService.Export(data, file, BackupParts.All);

        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");
        Write(other, "history/b.json", "history b");
        BackupService.Import(other, file);

        Assert.False(File.Exists(Path.Combine(other, "history/b.json")));
    }

    [Fact]
    public void A_file_from_an_older_version_imports_settings_and_macros_only()
    {
        using var temp = new TempFolder();
        string file = temp.File("old.puppymacro");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open()))
                w.Write("{\"old\":1}");
            using (var w = new StreamWriter(zip.CreateEntry("macros/a.json").Open()))
                w.Write("macro a");
        }
        Assert.Equal(BackupParts.Data, BackupService.ReadParts(file));

        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");
        Write(other, "history/b.json", "history b");
        BackupService.Import(other, file);

        Assert.Equal("{\"old\":1}", Read(other, "settings.json"));
        Assert.Equal("macro a", Read(other, "macros/a.json"));
        Assert.Equal("history b", Read(other, "history/b.json"));
    }

    [Fact]
    public void A_file_without_settings_is_not_a_backup()
    {
        using var temp = new TempFolder();
        string file = temp.File("other.puppymacro");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            zip.CreateEntry("macros/a.json");

        Assert.Throws<InvalidDataException>(() => BackupService.ReadParts(file));
    }

    [Fact]
    public void An_entry_outside_the_data_folder_is_refused_before_anything_changes()
    {
        using var temp = new TempFolder();
        string file = temp.File("bad.puppymacro");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            zip.CreateEntry("settings.json");
            zip.CreateEntry("../outside.json");
        }
        string other = Path.Combine(temp.Path, "other");
        Write(other, "settings.json", "{\"other\":1}");

        Assert.Throws<InvalidDataException>(() => BackupService.Import(other, file));

        Assert.Equal("{\"other\":1}", Read(other, "settings.json"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.json")));
        Assert.Empty(Directory.GetFiles(other, "backup-before-import-*"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(253_952, "248 KB")]
    [InlineData(3_355_443, "3.2 MB")]
    public void Sizes_are_shown_as_Explorer_shows_them(long bytes, string expected) =>
        Assert.Equal(expected, ExportOptionsView.Size(bytes));
}
