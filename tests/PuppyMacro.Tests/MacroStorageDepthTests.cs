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
