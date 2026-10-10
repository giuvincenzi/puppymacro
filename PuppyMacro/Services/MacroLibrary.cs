using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>All macros, in display order. Each macro is saved as macros\{id}.json next to the exe.</summary>
internal sealed class MacroLibrary
{
    // The same format as the macro editor's Code view.
    private static JsonSerializerOptions JsonOptions => MacroJson.Options;

    private readonly string _folder;

    public MacroLibrary(string folder) => _folder = folder;

    public List<MacroDefinition> Macros { get; private set; } = new();

    public MacroDefinition? Find(Guid id) => Macros.FirstOrDefault(m => m.Id == id);

    /// <summary>Loads every macro file; <paramref name="order"/> decides the order (unknown ones go last).</summary>
    public void Load(IList<Guid> order, out string? warning)
    {
        warning = null;
        var loaded = new List<MacroDefinition>();
        if (Directory.Exists(_folder))
        {
            foreach (string file in Directory.GetFiles(_folder, "*.json"))
            {
                try
                {
                    var macro = JsonSerializer.Deserialize<MacroDefinition>(File.ReadAllText(file), JsonOptions);
                    if (macro != null)
                    {
                        Sanitize(macro);
                        loaded.Add(macro);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    warning = $"Some macro files could not be read and were skipped (e.g. {Path.GetFileName(file)}).";
                }
            }
        }
        Macros = loaded
            .OrderBy(m => order.IndexOf(m.Id) is int i && i >= 0 ? i : int.MaxValue)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Settings schema 6: floating buttons saved before it have no opacity and used the overlay
    /// panel's; they keep it. Called once, when settings.json was older. Returns false if a file
    /// could not be saved.
    /// </summary>
    public bool FillButtonOpacity(int panelOpacity)
    {
        bool allSaved = true;
        foreach (var macro in Macros)
        {
            if (macro.FloatingButton.Opacity != null)
                continue;
            macro.FloatingButton.Opacity = panelOpacity;
            allSaved &= TrySave(macro, out _);
        }
        return allSaved;
    }

    public bool TrySave(MacroDefinition macro, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(_folder);
            string path = PathFor(macro.Id);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(macro, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Delete(MacroDefinition macro)
    {
        Macros.Remove(macro);
        try
        {
            File.Delete(PathFor(macro.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover file: it would come back on next start, but the order list no longer has it.
        }
    }

    private string PathFor(Guid id) => Path.Combine(_folder, $"{id}.json");

    private static void Sanitize(MacroDefinition macro)
    {
        if (macro.Id == Guid.Empty)
            macro.Id = Guid.NewGuid();
        macro.Name = string.IsNullOrWhiteSpace(macro.Name) ? "Macro" : macro.Name.Trim();
        macro.Actions ??= new();
        macro.Speed = MacroDefinition.SpeedSteps.Contains(macro.Speed) ? macro.Speed : 1;
        macro.RepeatCount = Math.Clamp(macro.RepeatCount, 1, 100000);
        if (!SoundService.Names.Contains(macro.SoundName))
            macro.SoundName = SoundService.Names[0];
        macro.Groups ??= new();
        foreach (var group in macro.Groups)
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name.Trim();
        macro.FloatingButton ??= new();
        macro.FloatingButton.Sanitize();
        macro.AppExe = AppScope.Normalize(macro.AppExe);
        foreach (var action in macro.Actions)
        {
            action.Path ??= new();
            action.Text ??= "";
            action.Name = action.Name?.Trim() ?? "";
            action.DelayMs = Math.Clamp(action.DelayMs, 0, 86_400_000);
            action.Repeat = Math.Clamp(action.Repeat, 1, 100000);
            action.RepeatPauseMs = Math.Clamp(action.RepeatPauseMs, 0, 86_400_000);
        }
        macro.NormalizeGroups();
    }
}
