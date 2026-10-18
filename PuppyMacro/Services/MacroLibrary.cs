using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>All macros, in display order. Each macro is saved as macros\{id}.json next to the exe.</summary>
internal sealed class MacroLibrary
{
    // The same format as the macro editor's Code view.
    private static JsonSerializerOptions JsonOptions => MacroJson.Options;

    private readonly string _folder;

    /// <param name="imagesFolder">Where the images are; by default "images" next to <paramref name="folder"/>.</param>
    public MacroLibrary(string folder, string? imagesFolder = null)
    {
        _folder = folder;
        string data = Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder;
        Images = new ImageStore(imagesFolder ?? Path.Combine(data, "images"));
        Library = new ComponentLibrary(Path.Combine(data, ComponentLibrary.FileName));
    }

    /// <summary>The Library of components the macros link (library.json, next to the macros' folder).</summary>
    public ComponentLibrary Library { get; }

    /// <summary>The images of the Image conditions, in the data folder next to the macros.</summary>
    public ImageStore Images { get; }

    // The images each saved macro uses (the others are deleted by ReleaseUnusedImages).
    private readonly Dictionary<Guid, HashSet<string>> _imagesOf = new();

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
                        _imagesOf[macro.Id] = ImageStore.IdsIn(macro);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    warning = string.Format(CultureInfo.CurrentCulture, Strings.App_MacroFilesSkipped, Path.GetFileName(file));
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
            _imagesOf[macro.Id] = ImageStore.IdsIn(macro);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Saves the Library and some macros together (a component and the calls its new signature changed): every file is
    /// written next to its place first, and only when all of them are written do they take their places, so a failure
    /// leaves every file as it was.
    /// </summary>
    public bool TrySaveTogether(IReadOnlyList<MacroDefinition> macros, out string? error)
    {
        error = null;
        if (Library.Broken)
        {
            error = Strings.Library_FileUnreadable;
            return false;
        }
        var files = new List<(string Temp, string Path)>();
        try
        {
            Directory.CreateDirectory(_folder);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Library.FilePath))!);
            files.Add((Library.FilePath + ".tmp", Library.FilePath));
            File.WriteAllText(files[^1].Temp, Library.Text());
            foreach (var macro in macros)
            {
                string path = PathFor(macro.Id);
                files.Add((path + ".tmp", path));
                File.WriteAllText(files[^1].Temp, JsonSerializer.Serialize(macro, JsonOptions));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (temp, _) in files)
                TryDelete(temp);
            error = ex.Message;
            return false;
        }
        foreach (var (temp, path) in files)
            File.Move(temp, path, overwrite: true);
        foreach (var macro in macros)
            _imagesOf[macro.Id] = ImageStore.IdsIn(macro);
        return true;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp file is never read.
        }
    }

    public void Delete(MacroDefinition macro)
    {
        Macros.Remove(macro);
        try
        {
            File.Delete(PathFor(macro.Id));
            _imagesOf.Remove(macro.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover file: it would come back on next start, but the order list no longer has it. Its images stay for it.
        }
    }

    /// <summary>
    /// Deletes every image of the folder that no saved macro uses and nothing holds (<see cref="ImageStore.Hold"/>): those a
    /// save or a deletion left unused, those taken and never saved, those of an import not taken. Called after the History
    /// recorded the change, so the version before it still found its images to keep a copy of.
    /// </summary>
    public void ReleaseUnusedImages()
    {
        // A Library it could not read may still name images: none is deleted until it is fixed.
        if (Library.Broken)
            return;
        var keep = _imagesOf.Values.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
        keep.UnionWith(Library.ImageIds());
        keep.UnionWith(Images.Held());
        foreach (string id in Images.All().Where(id => !keep.Contains(id)))
            Images.Delete(id);
    }

    private string PathFor(Guid id) => Path.Combine(_folder, $"{id}.json");

    /// <summary>Empty entries a file edited by hand can have (null in a list): left out, also inside Then and Else.</summary>
    private static void RemoveEmpty(List<MacroAction> actions)
    {
        actions.RemoveAll(a => a == null);
        foreach (var action in actions)
        {
            if (action.If is not { } block)
                continue;
            block.Conditions ??= new();
            block.Conditions.RemoveAll(c => c == null);
            foreach (var condition in block.Conditions)
                condition.Images?.RemoveAll(i => i == null);
            block.Then ??= new();
            block.Else ??= new();
            RemoveEmpty(block.Then);
            RemoveEmpty(block.Else);
        }
    }

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
        if (!MacroDefinition.ChecksPerSecondSteps.Contains(macro.ChecksPerSecond))
            macro.ChecksPerSecond = MacroDefinition.DefaultChecksPerSecond;
        RemoveEmpty(macro.Actions);
        foreach (var action in macro.AllActions.ToList())
        {
            if (action.Type == MacroActionType.If)
            {
                action.If ??= new();
                action.If.Conditions ??= new();
                action.If.Conditions.RemoveAll(c => c == null);
                action.If.Then ??= new();
                action.If.Else ??= new();
                action.If.WaitSeconds = Math.Clamp(action.If.WaitSeconds, 0, 86_400);
                foreach (var condition in action.If.Conditions)
                {
                    condition.Name = condition.Name?.Trim();
                    condition.Tolerance = Math.Clamp(condition.Tolerance, 0, 100);
                    condition.Similarity = Math.Clamp(condition.Similarity, 0, 100);
                    condition.Images?.RemoveAll(i => i == null);
                }
            }
            else
            {
                action.If = null;
            }
            if (action.Type is not (MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo))
                action.FoundId = null;
            action.Path ??= new();
            action.Text ??= "";
            action.Name = action.Name?.Trim() ?? "";
            action.DelayMs = Math.Clamp(action.DelayMs, 0, 86_400_000);
            action.Repeat = Math.Clamp(action.Repeat, 1, 100000);
            action.RepeatPauseMs = Math.Clamp(action.RepeatPauseMs, 0, 86_400_000);
            if (action.Type != MacroActionType.PressKey)
                action.ClearModifiers(); // only a Press key is a combination
        }
        macro.NormalizeGroups();
    }
}
