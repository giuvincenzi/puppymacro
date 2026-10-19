using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// settings.json as it can be on a user's disk: edited by hand, from the first version, held by another program for a moment,
/// written on a PC in another language (<see cref="SettingsStore"/>).
/// </summary>
public class SettingsStoreFilesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string SettingsPath => _folder.File("settings.json");

    /// <summary>
    /// data.md: a null where the model allows none "is left out when a data file is read (a null list is empty, a null entry
    /// is dropped), never a crash". Macro files and library.json follow it (CodeJson.ReadFile); settings.json is read with
    /// JsonSerializer directly, and Sanitize dereferences the null entry: PuppyMacro does not start (the error box at every
    /// start) until the user finds and fixes the file by hand. Peek (a backup opened from File Explorer) throws the same way.
    /// </summary>
    [Theory]
    [InlineData("""{ "SchemaVersion": 8, "Loops": [ null, { "Name": "Kept", "Actions": [ { "Type": "Key", "KeyVk": 1 } ] } ] }""")]
    [InlineData("""{ "SchemaVersion": 8, "Remaps": [ null ], "Loops": [ { "Name": "Kept", "Actions": [ { "Type": "Key", "KeyVk": 1 } ] } ] }""")]
    [InlineData("""{ "SchemaVersion": 8, "Loops": [ { "Name": "Kept", "Actions": [ null, { "Type": "Key", "KeyVk": 1 } ] } ] }""")]
    public void A_null_entry_in_a_list_of_settings_json_is_left_out_not_a_crash(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var store = new SettingsStore(SettingsPath);

        var exception = Record.Exception(() =>
        {
            AppSettings settings = store.Load(out _);
            Assert.Equal("Kept", Assert.Single(settings.Loops).Name); // the user's loop is still there
            store.Peek();
        });

        Assert.Null(exception);
    }

    /// <summary>
    /// A settings.json another program holds open for a moment (an antivirus scan, a backup or sync tool) while PuppyMacro
    /// starts with Windows: Load takes the read error for invalid content, its copy to settings.invalid.json fails for the
    /// same reason, and PuppyMacro runs on the defaults. The next save, once the file is free, writes the defaults over the
    /// user's loops and remaps, and no copy of them is left anywhere. library.json, in the same case, is kept and saving it
    /// is refused (ComponentLibrary.Broken).
    /// </summary>
    [Fact]
    public void A_settings_file_held_by_another_program_at_start_is_never_replaced_by_the_defaults()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings mine = AppSettings.CreateDefault();
        mine.Loops.Clear();
        mine.Loops.Add(new LoopDefinition
        {
            Name = "Farm gold",
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x41, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds } },
        });
        Assert.True(store.TrySave(mine, out _));

        AppSettings loaded;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) // another program has it open
            loaded = store.Load(out _);
        store.TrySave(loaded, out _); // the app's next save (a switch, a window moved), once the file is free

        string kept = File.ReadAllText(SettingsPath);
        string copy = _folder.File("settings.invalid.json");
        if (File.Exists(copy))
            kept += File.ReadAllText(copy);
        Assert.Contains("Farm gold", kept);
    }

    /// <summary>
    /// A settings.json of version 1.0 has no SchemaVersion (Version_1_0_files_are_converted's file). The property's default is
    /// the current schema, so such a file is read as schema 8: every migration for older files is skipped, and Open
    /// PuppyMacro (schema 8, data.md: "added to a file from before schema 8 only if nothing uses it") never gets its hotkey,
    /// as if the user had cleared it.
    /// </summary>
    [Fact]
    public void A_version_1_0_file_without_a_schema_gets_Open_PuppyMacro_like_every_older_file()
    {
        File.WriteAllText(SettingsPath, """
            {
              "StopAllHotkeyVk": 121,
              "GameModeHotkeyVk": 122,
              "Loops": [ { "Name": "Old loop", "KeyVk": 1, "IntervalMs": 50, "HotkeyVk": 120 } ]
            }
            """);
        AppSettings settings = new SettingsStore(SettingsPath).Load(out _);
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        macros.Load(settings.MacroOrder, out _);

        SettingsStore.AddOpenMainHotkey(settings, macros);

        Assert.True(settings.LoadedSchemaVersion < AppSettings.CurrentSchemaVersion, $"read as schema {settings.LoadedSchemaVersion}");
        Assert.True(settings.OpenMainHotkey?.SameAs(AppSettings.DefaultOpenMainHotkey()) == true);
    }

    /// <summary>
    /// A file saved on a PC in one language reads back the same on a PC in another: numbers with a fraction, sizes and
    /// positions are invariant in the file, whatever the decimal separator or the calendar of the language.
    /// </summary>
    [Theory]
    [InlineData("it-IT", "ar-SA")]
    [InlineData("de-DE", "en-US")]
    [InlineData("ar-SA", "fr-FR")]
    public void Settings_saved_in_one_language_read_back_the_same_in_another(string savedIn, string readIn)
    {
        AppSettings mine = AppSettings.CreateDefault();
        mine.Loops[0].Actions[0].IntervalValue = 1.5;
        mine.Loops[0].Actions[0].IntervalUnit = IntervalUnit.Seconds;
        mine.OverlayPanelX = 1234.75;
        mine.OverlayPanelY = -0.5;
        mine.MacroEditorWidth = 1520.25;
        mine.MacroEditorHeight = 900.5;
        mine.Loops[0].Name = "Ciclo è – 循环 – حلقة";

        string saved = InCulture(savedIn, () =>
        {
            Assert.True(new SettingsStore(SettingsPath).TrySave(mine, out _));
            return File.ReadAllText(SettingsPath);
        });
        AppSettings read = InCulture(readIn, () => new SettingsStore(SettingsPath).Load(out _));

        Assert.Contains("1.5", saved);
        Assert.DoesNotContain("1,5", saved);
        Assert.Equal(1.5, read.Loops[0].Actions[0].IntervalValue);
        Assert.Equal(1234.75, read.OverlayPanelX);
        Assert.Equal(-0.5, read.OverlayPanelY);
        Assert.Equal(1520.25, read.MacroEditorWidth);
        Assert.Equal(900.5, read.MacroEditorHeight);
        Assert.Equal("Ciclo è – 循环 – حلقة", read.Loops[0].Name);
        // And the second save writes the same text: load → save is a fixed point.
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
