using System;
using System.Linq;
using System.Reflection;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// CopyFrom (an editor's Save puts the new values into the item the list shows) copies every field, also those added
/// later: a field it misses stays as it was until PuppyMacro starts again.
/// </summary>
public class CopyFromTests
{
    /// <summary>
    /// Every settable field of a simple kind set to a value that is not its default. Nullable numbers are left out: they
    /// are older files' fields (KeyVk, SourceVk…), read once and converted when the settings load.
    /// </summary>
    private static T Filled<T>() where T : new()
    {
        var item = new T();
        foreach (PropertyInfo p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.Name != "Id" && Nullable.GetUnderlyingType(p.PropertyType) == null))
        {
            Type type = p.PropertyType;
            object? value = type == typeof(bool) ? !(bool)(p.GetValue(item) ?? false)
                : type == typeof(int) ? 7
                : type == typeof(double) ? 2.0
                : type == typeof(string) ? "changed.exe"
                : type.IsEnum ? Enum.GetValues(type).Cast<object>().Last()
                : null;
            if (value != null)
                p.SetValue(item, value);
        }
        return item;
    }

    [Fact]
    public void A_macro_copies_every_field()
    {
        MacroDefinition source = Filled<MacroDefinition>();
        var target = new MacroDefinition { Id = source.Id };

        target.CopyFrom(source);

        Assert.Equal(CodeJson.Serialize(source), CodeJson.Serialize(target));
    }

    [Fact]
    public void A_loop_copies_every_field()
    {
        LoopDefinition source = Filled<LoopDefinition>();
        var target = new LoopDefinition { Id = source.Id };

        target.CopyFrom(source);

        Assert.Equal(CodeJson.Serialize(source), CodeJson.Serialize(target));
    }

    [Fact]
    public void A_remap_copies_every_field()
    {
        RemapDefinition source = Filled<RemapDefinition>();
        var target = new RemapDefinition { Id = source.Id };

        target.CopyFrom(source);

        Assert.Equal(CodeJson.Serialize(source), CodeJson.Serialize(target));
    }
}
