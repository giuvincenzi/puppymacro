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
    private readonly AppSettings _settings = AppSettings.CreateDefault(); // Stop all Alt+Shift+S, Overlay mode Alt+Shift+W, Record F8, a loop on F9
    private readonly MacroLibrary _macros;
    private readonly MacroDefinition _macro = new() { Name = "Craft", Hotkey = new HotkeyBinding { Vk = 0x75, Ctrl = true } };

    public HotkeyConflictsTests()
    {
        _macros = new MacroLibrary(_folder.Path);
        Assert.True(_macros.TrySave(_macro, out _));
        _macros.Load(Array.Empty<Guid>(), out _);
    }

    public void Dispose() => _folder.Dispose();

    private string? Find(HotkeyBinding binding, Guid? ignoreId = null, string? ignoreGlobal = null, string? appExe = null) =>
        HotkeyConflicts.Find(binding, _settings, _macros, ignoreId, ignoreGlobal, appExe);

    [Fact]
    public void Global_hotkeys_conflict_unless_that_one_is_being_changed()
    {
        Assert.Equal("Alt + Shift + S is already used by Stop all.", Find(AppSettings.DefaultStopAllHotkey()));
        Assert.Equal("Alt + Shift + W is already used by Overlay mode.", Find(AppSettings.DefaultOverlayModeHotkey()));
        Assert.Equal("F8 is already used by Record.", Find(HotkeyBinding.FromKey(0x77)));
        Assert.Null(Find(AppSettings.DefaultStopAllHotkey(), ignoreGlobal: "StopAll"));
        Assert.Null(Find(AppSettings.DefaultOverlayModeHotkey(), ignoreGlobal: "OverlayMode"));
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
    public void Loops_and_macros_share_a_hotkey_only_in_different_apps()
    {
        LoopDefinition all = _settings.Loops[0]; // F9, all apps
        var game = new LoopDefinition { Name = "Game", Hotkey = HotkeyBinding.FromKey(0x79), AppExe = "Game.exe" }; // F10
        _settings.Loops.Add(game);

        // One for an app and one for all apps: allowed, both ways.
        Assert.Null(Find(HotkeyBinding.FromKey(0x78), appExe: "game.exe"));
        Assert.Null(Find(HotkeyBinding.FromKey(0x79)));
        // Two for different apps: allowed. Two for the same app (any case): a conflict that names the app.
        Assert.Null(Find(HotkeyBinding.FromKey(0x79), appExe: "other.exe"));
        Assert.Equal("F10 is already used by the loop \"Game\" in Game.exe.", Find(HotkeyBinding.FromKey(0x79), appExe: "GAME.EXE"));
        // Two for all apps: a conflict, as before.
        Assert.Equal($"F9 is already used by the loop \"{all.Name}\".", Find(HotkeyBinding.FromKey(0x78)));
        // Global hotkeys work everywhere: they clash with a loop for any app.
        Assert.Equal("F10 is already used by the loop \"Game\" in Game.exe.", Find(HotkeyBinding.FromKey(0x79), ignoreGlobal: "StopAll"));
    }

    [Fact]
    public void A_remap_source_clashes_with_the_hotkeys_of_the_apps_it_works_in()
    {
        _settings.Loops.Add(new LoopDefinition { Name = "Game", Hotkey = HotkeyBinding.FromKey(0x79), AppExe = "Game.exe" }); // F10

        Assert.NotNull(HotkeyConflicts.FindForRemap(0x79, null, _settings, _macros));          // all apps includes Game.exe
        Assert.NotNull(HotkeyConflicts.FindForRemap(0x79, "game.exe", _settings, _macros));
        Assert.Null(HotkeyConflicts.FindForRemap(0x79, "other.exe", _settings, _macros));
        Assert.NotNull(HotkeyConflicts.FindForRemap(0x78, "other.exe", _settings, _macros));    // F9 is for all apps
    }

    [Fact]
    public void Matching_is_exact()
    {
        Assert.Null(Find(HotkeyBinding.FromKey(0x75)));                          // F6 alone, the macro uses Ctrl+F6
        Assert.Null(Find(new HotkeyBinding { Vk = 0x53, Alt = true }));          // Alt+S, Stop all is Alt+Shift+S
    }
}
