using System;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Macros with Ifs inside Ifs: the editor puts an If in another If's Then or Else with no limit of levels, so a macro file,
/// the History and the Code view must hold any depth the editor can make.
/// </summary>
public class MacroStorageDepthTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static MacroDefinition Nested(int levels)
    {
        MacroAction inner = Press();
        for (int i = 0; i < levels; i++)
            inner = If(Pixel(), inner);
        return Macro("Deep", inner);
    }

    private static int Depth(MacroDefinition macro)
    {
        int depth = 0;
        var list = macro.Actions;
        while (list.FirstOrDefault()?.If is { } inner)
        {
            depth++;
            list = inner.Then;
        }
        return depth;
    }

    /// <summary>
    /// 21 Ifs inside each other (each one checks a pixel, the last one presses a key): MacroLibrary.TrySave serializes with
    /// System.Text.Json's default MaxDepth of 64, and each If adds 3 levels (the action, its If, its Then list), so the
    /// serializer throws a JsonException. TrySave catches only IOException and UnauthorizedAccessException: the exception
    /// leaves the save (in the app, MainWindow.SaveMacro after the editor's Save). Expected: the macro is saved and loads back
    /// with its 21 levels.
    /// </summary>
    [Fact]
    public void A_macro_with_many_ifs_inside_each_other_is_saved_and_loads_back()
    {
        var macros = new MacroLibrary(_folder.File("macros"));
        var macro = Nested(21);

        bool saved = false;
        string? error = null;
        var thrown = Record.Exception(() => saved = macros.TrySave(macro, out error));

        Assert.Null(thrown);
        Assert.True(saved, error);
        var loaded = new MacroLibrary(_folder.File("macros"));
        loaded.Load(Array.Empty<Guid>(), out string? warning);
        Assert.Null(warning);
        Assert.Equal(21, Depth(Assert.Single(loaded.Macros)));
    }

    /// <summary>
    /// The same macro in the Code view and the History: MacroJson.Serialize (used for the History and the Code view) throws
    /// past 64 levels. Expected: the text is made and reads back.
    /// </summary>
    [Fact]
    public void A_macro_with_many_ifs_inside_each_other_has_its_code_view_text()
    {
        var macro = Nested(21);

        string? json = null;
        var thrown = Record.Exception(() => json = MacroJson.Serialize(macro));

        Assert.Null(thrown);
        Assert.Equal(21, Depth(MacroJson.Parse(json!, macro.Id, (_, _) => null, out var problems)
            ?? throw new Xunit.Sdk.XunitException(string.Join(" ", problems.Select(p => p.Message)))));
    }

    /// <summary>Fewer levels, as most macros have, save and load back (a guard for the test above).</summary>
    [Fact]
    public void A_macro_with_a_few_ifs_inside_each_other_is_saved_and_loads_back()
    {
        var macros = new MacroLibrary(_folder.File("macros"));
        Assert.True(macros.TrySave(Nested(10), out string? error), error);

        var loaded = new MacroLibrary(_folder.File("macros"));
        loaded.Load(Array.Empty<Guid>(), out _);
        Assert.Equal(10, Depth(Assert.Single(loaded.Macros)));
    }
}
