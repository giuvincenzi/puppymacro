using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class MacroLibraryTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Saved_macros_load_back_in_the_given_order_and_unknown_ones_last()
    {
        var library = new MacroLibrary(_folder.Path);
        var first = new MacroDefinition { Name = "B first" };
        var second = new MacroDefinition { Name = "A second", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        var unlisted = new MacroDefinition { Name = "C unlisted" };
        foreach (MacroDefinition macro in new[] { first, second, unlisted })
            Assert.True(library.TrySave(macro, out _));

        var loaded = new MacroLibrary(_folder.Path);
        loaded.Load(new[] { first.Id, second.Id }, out string? warning);

        Assert.Null(warning);
        Assert.Equal(new[] { "B first", "A second", "C unlisted" }, loaded.Macros.Select(m => m.Name));
        Assert.Equal(0x41, Assert.Single(loaded.Find(second.Id)!.Actions).Vk);
    }

    [Fact]
    public void An_unreadable_macro_file_is_skipped_with_a_warning()
    {
        var library = new MacroLibrary(_folder.Path);
        Assert.True(library.TrySave(new MacroDefinition { Name = "Good" }, out _));
        File.WriteAllText(_folder.File("broken.json"), "{ not json");

        library.Load(Array.Empty<Guid>(), out string? warning);

        Assert.Equal("Good", Assert.Single(library.Macros).Name);
        Assert.Contains("broken.json", warning);
    }
}

public class HotkeyConflictsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault(); // Stop all F10, Game mode F11, Record F8, a loop on F9
    private readonly MacroLibrary _macros;
    private readonly MacroDefinition _macro = new() { Name = "Craft", Hotkey = new HotkeyBinding { Vk = 0x75, Ctrl = true } };

    public HotkeyConflictsTests()
    {
        _macros = new MacroLibrary(_folder.Path);
        Assert.True(_macros.TrySave(_macro, out _));
        _macros.Load(Array.Empty<Guid>(), out _);
    }

    public void Dispose() => _folder.Dispose();

    private string? Find(HotkeyBinding binding, Guid? ignoreId = null, string? ignoreGlobal = null) =>
        HotkeyConflicts.Find(binding, _settings, _macros, ignoreId, ignoreGlobal);

    [Fact]
    public void Global_hotkeys_conflict_unless_that_one_is_being_changed()
    {
        Assert.Equal("F10 is already used by Stop all.", Find(HotkeyBinding.FromKey(0x79)));
        Assert.Equal("F11 is already used by Game mode.", Find(HotkeyBinding.FromKey(0x7A)));
        Assert.Equal("F8 is already used by Record.", Find(HotkeyBinding.FromKey(0x77)));
        Assert.Null(Find(HotkeyBinding.FromKey(0x79), ignoreGlobal: "StopAll"));
    }

    [Fact]
    public void Loop_and_macro_hotkeys_conflict_except_with_themselves()
    {
        LoopDefinition loop = _settings.Loops[0];

        Assert.Equal($"F9 is already used by the loop \"{loop.Name}\".", Find(HotkeyBinding.FromKey(0x78)));
        Assert.Null(Find(HotkeyBinding.FromKey(0x78), ignoreId: loop.Id));
        Assert.Equal("Ctrl + F6 is already used by the macro \"Craft\".", Find(new HotkeyBinding { Vk = 0x75, Ctrl = true }));
        Assert.Null(Find(new HotkeyBinding { Vk = 0x75, Ctrl = true }, ignoreId: _macro.Id));
    }

    [Fact]
    public void Matching_is_exact()
    {
        Assert.Null(Find(HotkeyBinding.FromKey(0x75)));                          // F6 alone, the macro uses Ctrl+F6
        Assert.Null(Find(new HotkeyBinding { Vk = 0x79, Shift = true }));        // Shift+F10, Stop all is F10
    }
}
