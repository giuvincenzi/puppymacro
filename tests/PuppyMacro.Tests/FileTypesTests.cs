using System;
using Microsoft.Win32;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The keys of PuppyMacro's file types (<see cref="FileTypes"/>), written under a test key instead of Software\Classes.</summary>
public class FileTypesTests : IDisposable
{
    private const string Exe = @"C:\Users\Someone\AppData\Local\PuppyMacro\current\PuppyMacro.exe";
    private const string Icons = @"C:\Users\Someone\AppData\Local\PuppyMacro\current\Assets\FileIcons";

    private readonly string _path = $@"Software\PuppyMacroTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _classes;

    public FileTypesTests() => _classes = Registry.CurrentUser.CreateSubKey(_path);

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    private string? Value(string key) => _classes.OpenSubKey(key)?.GetValue(null) as string;

    [Fact]
    public void Items_files_get_their_icon_and_open_in_PuppyMacro_and_backups_only_their_icon()
    {
        Assert.True(FileTypes.Apply(_classes, Exe, Icons));

        Assert.Equal("PuppyMacro.Items", Value(".puppyitems"));
        Assert.Equal("PuppyMacro items", Value("PuppyMacro.Items"));
        Assert.Equal(Icons + @"\PuppyMacroItems.ico", Value(@"PuppyMacro.Items\DefaultIcon"));
        Assert.Equal($"\"{Exe}\" \"%1\"", Value(@"PuppyMacro.Items\shell\open\command"));
        Assert.Equal("PuppyMacro.Backup", Value(".puppymacro"));
        Assert.Equal(Icons + @"\PuppyMacroBackup.ico", Value(@"PuppyMacro.Backup\DefaultIcon"));
        Assert.Null(_classes.OpenSubKey(@"PuppyMacro.Backup\shell")); // a backup is imported from Settings only
    }

    [Fact]
    public void Writing_them_again_changes_nothing_until_the_exe_moves()
    {
        FileTypes.Apply(_classes, Exe, Icons);

        Assert.False(FileTypes.Apply(_classes, Exe, Icons));
        Assert.True(FileTypes.Apply(_classes, @"D:\PuppyMacro\PuppyMacro.exe", Icons));
        Assert.Equal("\"D:\\PuppyMacro\\PuppyMacro.exe\" \"%1\"", Value(@"PuppyMacro.Items\shell\open\command"));
    }

    [Fact]
    public void Uninstall_removes_them_but_leaves_an_extension_another_app_took()
    {
        FileTypes.Apply(_classes, Exe, Icons);
        using (RegistryKey backup = _classes.CreateSubKey(".puppymacro"))
            backup.SetValue(null, "Other.App");

        FileTypes.Remove(_classes);

        Assert.Null(_classes.OpenSubKey("PuppyMacro.Items"));
        Assert.Null(_classes.OpenSubKey("PuppyMacro.Backup"));
        Assert.Null(_classes.OpenSubKey(".puppyitems"));
        Assert.Equal("Other.App", Value(".puppymacro"));
    }
}
