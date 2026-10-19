using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// A macro as the user has it (read from its file by <see cref="MacroLibrary"/>), shared with a .puppyitems file and
/// imported on another PC: what a load keeps must also travel (data.md: "Each item is exactly the Code view's JSON").
/// </summary>
public class ItemFileRoundTripTests : IDisposable
{
    private static readonly Version This = new(1, 23, 0);
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>A macro file written by an earlier version, read as the app reads it at start.</summary>
    private MacroDefinition Loaded(string json)
    {
        string folder = _folder.File("macros");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recorded.json"), json);
        var macros = new MacroLibrary(folder);
        macros.Load(Array.Empty<Guid>(), out string? warning);
        Assert.Null(warning);
        return Assert.Single(macros.Macros);
    }

    /// <summary>A macro with odd characters in its name and texts goes and comes back with every character.</summary>
    [Fact]
    public void Names_and_texts_in_any_script_go_and_come_back_unchanged()
    {
        const string name = "Macro è – 宏 – ماكرو – 🐶 \"quoted\" \\ back";
        var macro = new MacroDefinition
        {
            Name = name,
            Actions = { new MacroAction { Type = MacroActionType.PasteText, Text = "Ciao\n– 你好 🐾\t\"x\"" } },
        };
        string text = ItemFile.Write(new object[] { macro }, This);
        var settings = AppSettings.CreateDefault();
        settings.Loops.Clear();

        ItemFileContent content = ItemFile.Read(text, settings, new MacroLibrary(_folder.File("other-pc")), This);

        var read = Assert.IsType<MacroDefinition>(Assert.Single(content.Entries).Item);
        Assert.Equal(name, read.Name);
        Assert.Equal(macro.Actions[0].Text, read.Actions[0].Text);
    }
}
