using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>All macros, in display order. Each macro is saved in its own file in the macros folder, macros\{id}.json for a new one.</summary>
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

    // The files the macros were read from, by Id; a new macro goes to macros\{id}.json.
    private readonly Dictionary<Guid, string> _files = new();

    // The images named by the files that could not be read at start, and whether one could not be read at all: kept, so the
    // macro finds them when its file can be read (a newer version's file, a file another program had open).
    private readonly HashSet<string> _imagesOfUnread = new(StringComparer.Ordinal);
    private bool _unreadFile;

    public List<MacroDefinition> Macros { get; private set; } = new();

    public MacroDefinition? Find(Guid id) => Macros.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Loads every macro file; <paramref name="order"/> decides the order (unknown ones go last). A macro is saved back to the
    /// file it was read from. A file without an Id, or with the Id of another file (a copy made in File Explorer), is a macro
    /// of its own: its Id comes from its file name, so it stays the same at every start until the macro is saved with it.
    /// </summary>
    public void Load(IList<Guid> order, out string? warning)
    {
        warning = null;
        _files.Clear();
        _imagesOf.Clear();
        _imagesOfUnread.Clear();
        _unreadFile = false;
        var read = new List<(string File, MacroDefinition Macro)>();
        if (Directory.Exists(_folder))
        {
            // The copies a save together left first, so a macro file it left missing is read.
            foreach (string backup in Directory.GetFiles(_folder, "*.json" + BackupSuffix))
                RecoverBackup(backup[..^BackupSuffix.Length]);
            foreach (string file in Directory.GetFiles(_folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                string? text = null;
                try
                {
                    text = File.ReadAllText(file);
                    if (CodeJson.ReadFile<MacroDefinition>(text) is { } macro)
                        read.Add((file, macro));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    warning = string.Format(CultureInfo.CurrentCulture, Strings.App_MacroFilesSkipped, Path.GetFileName(file));
                    // Its images stay until it can be read: the ones its text names, or every image when it can't be read at all.
                    if (text != null)
                        _imagesOfUnread.UnionWith(ImageStore.IdsIn(text));
                    else
                        _unreadFile = true;
                }
            }
        }

        // The file named after its Id keeps it; another file with that Id, or without one, gets the Id of its file name.
        var loaded = new List<MacroDefinition>();
        var ids = new HashSet<Guid>();
        foreach (var (file, macro) in read.OrderBy(r => r.Macro.Id != Guid.Empty && IsNamedAfter(r.File, r.Macro.Id) ? 0 : 1))
        {
            if (macro.Id == Guid.Empty || ids.Contains(macro.Id))
                macro.Id = IdOfFile(file, ids);
            ids.Add(macro.Id);
            Sanitize(macro);
            loaded.Add(macro);
            _files[macro.Id] = file;
            _imagesOf[macro.Id] = ImageStore.IdsIn(macro);
        }
        Macros = loaded
            .OrderBy(m => order.IndexOf(m.Id) is int i && i >= 0 ? i : int.MaxValue)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsNamedAfter(string file, Guid id) =>
        Guid.TryParse(Path.GetFileNameWithoutExtension(file), out Guid named) && named == id;

    /// <summary>An Id made from a file's name, the same at every start, that none of <paramref name="taken"/> has.</summary>
    private static Guid IdOfFile(string file, HashSet<Guid> taken)
    {
        string name = Path.GetFileName(file).ToLowerInvariant();
        for (int n = 0; ; n++)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(n == 0 ? name : $"{name}|{n}"));
            var id = new Guid(hash.AsSpan(0, 16));
            if (id != Guid.Empty && !taken.Contains(id))
                return id;
        }
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
        // Each file takes its place in one step (a move that replaces it: the place always holds the old file or the new one,
        // never nothing), after a copy of the old one is kept aside. When one can't (its place is taken by a folder, a file
        // kept open), those already placed get their old copy back, so the Library and the macros stay as they were.
        var moved = new List<(string Path, string? Backup, bool Placed)>();
        try
        {
            foreach (var (temp, path) in files)
            {
                string? backup = File.Exists(path) ? path + BackupSuffix : null;
                if (backup != null)
                    File.Copy(path, backup, overwrite: true);
                moved.Add((path, backup, false));
                File.Move(temp, path, overwrite: true);
                moved[^1] = (path, backup, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                PutBack(moved[i].Path, moved[i].Backup, moved[i].Placed);
            foreach (var (temp, _) in files)
                TryDelete(temp);
            error = ex.Message;
            return false;
        }
        foreach (var (_, backup, _) in moved)
            if (backup != null)
                TryDelete(backup);
        foreach (var macro in macros)
            _imagesOf[macro.Id] = ImageStore.IdsIn(macro);
        return true;
    }

    /// <summary>
    /// The copy <see cref="TrySaveTogether"/> keeps of a file it replaces, next to it, deleted when the save ends. Never in a
    /// backup (<see cref="BackupService.PartOf"/>).
    /// </summary>
    internal const string BackupSuffix = ".bak";

    /// <summary>
    /// A file of <see cref="TrySaveTogether"/> that took its place gets its old copy back (replaced in one step, as it was
    /// placed), or goes when there was none; a file that had not taken its place is untouched and its copy goes.
    /// </summary>
    private static void PutBack(string path, string? backup, bool placed)
    {
        try
        {
            if (placed && backup != null)
                File.Move(backup, path, overwrite: true);
            else if (placed)
                File.Delete(path);
            else if (backup != null)
                File.Delete(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk refuses even this: the copy stays next to the file, and the next load resolves it (RecoverBackup).
        }
    }

    /// <summary>
    /// At load, the copy a save together left next to <paramref name="path"/> when it was stopped before it ended (the app
    /// killed, the PC turned off). The file is replaced in one step, so when it is there it is whole: it wins and the copy
    /// goes. When it is missing (a save of an earlier build, which moved the file aside first) the copy takes its place, so
    /// the data is never read as missing.
    /// </summary>
    internal static void RecoverBackup(string path)
    {
        string backup = path + BackupSuffix;
        try
        {
            if (!File.Exists(backup))
                return;
            if (File.Exists(path))
                File.Delete(backup);
            else
                File.Move(backup, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: the next load tries again.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp file is never read; a leftover .bak is resolved at the next load (RecoverBackup).
        }
    }

    public void Delete(MacroDefinition macro)
    {
        Macros.Remove(macro);
        try
        {
            File.Delete(PathFor(macro.Id));
            _imagesOf.Remove(macro.Id);
            _files.Remove(macro.Id);
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
        // A Library or a macro file it could not read may still name images: none is deleted until it is fixed.
        if (Library.Broken || _unreadFile)
            return;
        var keep = _imagesOf.Values.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
        // The macros as the app has them too: one whose last save failed names images its file doesn't yet.
        keep.UnionWith(Macros.SelectMany(ImageStore.IdsIn));
        keep.UnionWith(_imagesOfUnread);
        keep.UnionWith(Library.ImageIds());
        keep.UnionWith(Images.Held());
        foreach (string id in Images.All().Where(id => !keep.Contains(id)))
            Images.Delete(id);
    }

    private string PathFor(Guid id) => _files.TryGetValue(id, out string? file) ? file : Path.Combine(_folder, $"{id}.json");

    private static void Sanitize(MacroDefinition macro)
    {
        macro.Name = string.IsNullOrWhiteSpace(macro.Name) ? "Macro" : macro.Name.Trim();
        macro.Speed = MacroDefinition.SpeedSteps.Contains(macro.Speed) ? macro.Speed : 1;
        macro.RepeatCount = Math.Clamp(macro.RepeatCount, 1, 100000);
        if (!SoundService.Names.Contains(macro.SoundName))
            macro.SoundName = SoundService.Names[0];
        foreach (var group in macro.Groups)
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name.Trim();
        macro.FloatingButton.Sanitize();
        macro.AppExe = AppScope.Normalize(macro.AppExe);
        if (!MacroDefinition.ChecksPerSecondSteps.Contains(macro.ChecksPerSecond))
            macro.ChecksPerSecond = MacroDefinition.DefaultChecksPerSecond;
        foreach (var action in macro.AllActions.ToList())
        {
            if (action.Type == MacroActionType.If)
            {
                action.If ??= new();
                action.If.WaitSeconds = Math.Clamp(action.If.WaitSeconds, 0, 86_400);
                foreach (var condition in action.If.Conditions)
                {
                    condition.Name = condition.Name?.Trim();
                    condition.Tolerance = Math.Clamp(condition.Tolerance, 0, 100);
                    condition.Similarity = Math.Clamp(condition.Similarity, 0, 100);
                }
            }
            else
            {
                action.If = null;
            }
            if (action.Type is not (MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo))
                action.FoundId = null;
            action.Name = action.Name.Trim();
            action.DelayMs = Math.Clamp(action.DelayMs, 0, 86_400_000);
            action.Repeat = Math.Clamp(action.Repeat, 1, 100000);
            action.RepeatPauseMs = Math.Clamp(action.RepeatPauseMs, 0, 86_400_000);
            // A hold of an earlier recording outside the editor's limits is kept as it is (the user's choice): it plays as
            // recorded, never changed behind the user's back; recordings now stay inside them (MacroBuilder).
            if (action.Type != MacroActionType.PressKey)
                action.ClearModifiers(); // only a Press key is a combination
        }
        macro.NormalizeGroups();
    }
}
