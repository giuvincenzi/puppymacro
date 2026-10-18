using System;
using System.Globalization;
using System.Text.Json;
using iNKORE.UI.WPF.Modern.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Restore in an editor's History view: the version's item, with the editor's Id, in the fields.</summary>
internal static class HistoryRestore
{
    /// <summary>The version's loop, macro or remap with <paramref name="id"/>; null when its JSON can't be read.</summary>
    public static T? Read<T>(HistoryVersion version, Guid id) where T : class
    {
        try
        {
            T? item = JsonSerializer.Deserialize<T>(version.Json, CodeJson.Options);
            switch (item)
            {
                case LoopDefinition loop: loop.Id = id; break;
                case MacroDefinition macro: macro.Id = id; break;
                case RemapDefinition remap: remap.Id = id; break;
            }
            return item;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version's Library component with <paramref name="id"/>; null when its JSON can't be read.</summary>
    public static LibraryComponent? ReadComponent(HistoryVersion version, Guid id)
    {
        try
        {
            var component = JsonSerializer.Deserialize<LibraryComponent>(version.Json, CodeJson.Options);
            if (component != null)
                component.Id = id;
            return component;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The bar above the fields: the restored version waits for Save.</summary>
    public static void Notice(InfoBar bar, HistoryVersion version)
    {
        bar.Title = string.Format(CultureInfo.CurrentCulture, Strings.HistoryView_RestoredTitle, version.Time);
        bar.Message = Strings.HistoryView_RestoredMessage;
        bar.IsOpen = true;
    }
}
