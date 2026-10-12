using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>One loop, macro or remap of a .puppyitems file, as Import shows it and adds it.</summary>
internal sealed class ItemFileEntry
{
    /// <summary>Its place in the file's Items, from 0.</summary>
    public required int Index { get; init; }

    /// <summary>"loop", "macro" or "remap" (as the AI tools name them); the file's own word when it is unknown.</summary>
    public required string Kind { get; init; }

    /// <summary>Ready to add: a new Id, disabled, its name and hotkey as they are imported. Null when it can't be imported.</summary>
    public object? Item { get; init; }

    /// <summary>Its name, or a remap's keys.</summary>
    public required string Title { get; init; }

    /// <summary>Why it can't be imported, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>What changes on import: a new name, a hotkey left out.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>It will be added: it can be imported and was chosen.</summary>
    public bool Selected { get; init; }
}

/// <summary>What a .puppyitems file holds, read for Import against the user's data.</summary>
internal sealed class ItemFileContent
{
    /// <summary>Why the whole file can't be read, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The PuppyMacro version that wrote it ("1.17.0"), or null.</summary>
    public string? App { get; init; }

    public List<ItemFileEntry> Entries { get; } = new();

    public IEnumerable<object> SelectedItems => Entries.Where(e => e.Selected && e.Item != null).Select(e => e.Item!);
}

/// <summary>
/// A .puppyitems file: loops, macros and remaps to share, as one JSON file. Each item is its JSON as the Code view shows it.
/// Import never replaces anything: every item gets a new Id and comes disabled; a name already used gets " (2)"; a hotkey
/// already used is left out (a Hold item becomes Toggle, as Hold needs a hotkey); a remap whose key is already taken can't
/// be imported. Not the backup (<see cref="BackupService"/>), which replaces the whole data folder.
/// </summary>
internal static class ItemFile
{
    public const string Extension = ".puppyitems";

    /// <summary>The file's layout. Raise it only when the layout around the items changes, not when an item gets new fields.</summary>
    public const int CurrentFormat = 1;

    /// <summary>This PuppyMacro's version, written in the files it exports.</summary>
    public static Version? AppVersion { get; } = typeof(ItemFile).Assembly.GetName().Version;

    public static string Write(IEnumerable<object> items, Version? app)
    {
        var list = new JsonArray();
        foreach (object item in items)
            list.Add(new JsonObject { ["Kind"] = KindName(item), ["Item"] = JsonNode.Parse(Serialize(item)) });
        var root = new JsonObject
        {
            ["Format"] = CurrentFormat,
            ["App"] = app == null ? null : VersionText(app),
            ["Items"] = list,
        };
        return root.ToJsonString(CodeJson.Options);
    }

    /// <summary>
    /// Reads the file's items, checked as the Code views check them, and prepares the ones in <paramref name="only"/> (all
    /// when null) to be added to <paramref name="settings"/> and <paramref name="macros"/>, in the file's order: an item
    /// chosen earlier takes its name and hotkey first. An item not chosen gets the notes it would have.
    /// </summary>
    public static ItemFileContent Read(string text, AppSettings settings, MacroLibrary macros, Version? current,
        ICollection<int>? only = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return new ItemFileContent { Error = "This is not a PuppyMacro items file." };
        }
        if (root is not JsonObject file || file["Items"] is not JsonArray items
            || file["Format"] is not JsonValue formatValue || !formatValue.TryGetValue(out int format))
            return new ItemFileContent { Error = "This is not a PuppyMacro items file." };

        string? app = file["App"] is JsonValue appValue && appValue.TryGetValue(out string? appText) ? appText : null;
        bool newer = current != null && Version.TryParse(app, out Version? made) && made > current;
        string update = $"This file was made with PuppyMacro {app}, newer than this one ({(current == null ? "" : VersionText(current))}). " +
                        "Update PuppyMacro to import it.";
        if (format > CurrentFormat)
            return new ItemFileContent { Error = newer ? update : "This file was made with a newer PuppyMacro. Update PuppyMacro to import it." };

        var content = new ItemFileContent { App = app };
        var taken = new Taken(settings, macros);
        for (int index = 0; index < items.Count; index++)
            content.Entries.Add(ReadEntry(items[index], index, only == null || only.Contains(index), newer ? update : null, taken));
        return content;
    }

    private static ItemFileEntry ReadEntry(JsonNode? node, int index, bool chosen, string? update, Taken taken)
    {
        string kindName = node is JsonObject o && o["Kind"] is JsonValue k && k.TryGetValue(out string? word) ? word : "";
        string kind = kindName.ToLowerInvariant();
        JsonObject? item = node is JsonObject entry ? entry["Item"] as JsonObject : null;
        string title = item?["Name"] is JsonValue n && n.TryGetValue(out string? name) && name.Trim().Length > 0 ? name.Trim() : $"Item {index + 1}";

        ItemFileEntry Unusable(string problem, string? shownTitle = null) =>
            new() { Index = index, Kind = kind.Length > 0 ? kind : "item", Title = shownTitle ?? title, Problem = update ?? problem };

        if (kind is not ("loop" or "macro" or "remap"))
            return Unusable($"Unknown kind \"{kindName}\": PuppyMacro imports loops, macros and remaps.");
        if (item == null)
            return Unusable($"The {kind} is missing.");

        // The Code view's checks, with a new Id; the hotkeys are checked below, where they can be left out instead.
        Guid id = Guid.NewGuid();
        var copy = (JsonObject)item.DeepClone();
        copy["Id"] = id.ToString();
        string json = copy.ToJsonString(CodeJson.Options);
        List<CodeProblem> problems;
        object? parsed = kind switch
        {
            "loop" => LoopJson.Parse(json, id, (_, _) => null, out problems),
            "macro" => MacroJson.Parse(json, id, (_, _) => null, out problems),
            _ => RemapJson.Parse(json, id, (_, _) => null, out problems),
        };
        if (parsed is RemapDefinition remapTitle)
            title = Keys(remapTitle);
        if (parsed == null)
            return Unusable(string.Join(" ", problems.Select(p => p.Message)), title);

        var notes = new List<string>();
        switch (parsed)
        {
            case LoopDefinition loop:
                loop.Enabled = false;
                loop.Name = taken.Name(loop.Name, "loop", notes, chosen);
                loop.Hotkey = taken.Hotkey(loop.Hotkey, loop.AppExe, $"loop \"{loop.Name}\"", loop.Mode, notes, chosen, out ActivationMode loopMode);
                loop.Mode = loopMode;
                title = loop.Name;
                break;
            case MacroDefinition macro:
                macro.Enabled = false;
                macro.Name = taken.Name(macro.Name, "macro", notes, chosen);
                macro.Hotkey = taken.Hotkey(macro.Hotkey, macro.AppExe, $"macro \"{macro.Name}\"", macro.Mode, notes, chosen, out ActivationMode macroMode);
                macro.Mode = macroMode;
                title = macro.Name;
                break;
            case RemapDefinition remap:
                remap.Enabled = false;
                if (taken.RemapClash(remap, chosen) is string clash)
                    return Unusable(clash, title);
                break;
        }
        var result = new ItemFileEntry { Index = index, Kind = kind, Item = parsed, Title = title, Selected = chosen };
        result.Notes.AddRange(notes);
        return result;
    }

    /// <summary>The names, hotkeys and remap keys in use: the user's, then the chosen items' of the file, in order.</summary>
    private sealed class Taken(AppSettings settings, MacroLibrary macros)
    {
        private readonly HashSet<string> _loopNames = new(settings.Loops.Select(l => l.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _macroNames = new(macros.Macros.Select(m => m.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        private readonly List<(HotkeyBinding Keys, string? App, string What)> _hotkeys = new();
        private readonly List<(HotkeyBinding Keys, string? App)> _remaps = new();

        public string Name(string name, string kind, List<string> notes, bool chosen)
        {
            HashSet<string> names = kind == "loop" ? _loopNames : _macroNames;
            string free = name;
            for (int n = 2; names.Contains(free); n++)
                free = $"{name} ({n})";
            if (free != name)
                notes.Add($"A {kind} has this name: imported as \"{free}\".");
            if (chosen)
                names.Add(free);
            return free;
        }

        public HotkeyBinding? Hotkey(HotkeyBinding? hotkey, string? app, string what, ActivationMode mode, List<string> notes,
            bool chosen, out ActivationMode importedMode)
        {
            importedMode = mode;
            if (hotkey is not { IsSet: true })
                return hotkey;
            string? conflict = HotkeyConflicts.Find(hotkey, settings, macros, appExe: app)
                ?? _hotkeys.Where(h => hotkey.Overlaps(h.Keys) && AppScope.Same(h.App, app))
                    .Select(h => $"{KeyNames.Format(hotkey)} is already used by the {h.What} in this file.").FirstOrDefault();
            if (conflict == null)
            {
                if (chosen)
                    _hotkeys.Add((hotkey, app, what));
                return hotkey;
            }
            bool hold = mode == ActivationMode.Hold;
            if (hold)
                importedMode = ActivationMode.Toggle;
            notes.Add(conflict.TrimEnd('.') + ": imported without a hotkey" + (hold ? ", as Toggle (Hold needs a hotkey)." : "."));
            return null;
        }

        /// <summary>The remap editor's rule (<see cref="HotkeyConflicts.RemapClash"/>), also against the file's earlier items.</summary>
        public string? RemapClash(RemapDefinition remap, bool chosen)
        {
            HotkeyBinding source = remap.Source!;
            string? app = remap.AppExe;
            string? clash = HotkeyConflicts.RemapClash(source, app, settings, macros, Guid.Empty)
                ?? _hotkeys.Where(h => source.Overlaps(h.Keys) && (h.App == null || app == null || AppScope.Same(h.App, app)))
                    .Select(h => $"{KeyNames.Format(source)} is already used by the {h.What} in this file. Hotkeys take priority over remaps.")
                    .FirstOrDefault()
                ?? (_remaps.Any(r => source.Overlaps(r.Keys) && AppScope.Same(r.App, app))
                    ? $"{KeyNames.Format(source)} is already remapped {(app == null ? "for all apps" : "for " + app)} in this file."
                    : null);
            if (clash == null && chosen)
                _remaps.Add((source, app));
            return clash;
        }
    }

    public static string KindName(object item) => item switch
    {
        LoopDefinition => "Loop",
        MacroDefinition => "Macro",
        RemapDefinition => "Remap",
        _ => throw new ArgumentException("Not a loop, macro or remap.", nameof(item)),
    };

    /// <summary>"Ctrl + J → C", and the app when it works in one.</summary>
    public static string Keys(RemapDefinition remap) =>
        $"{KeyNames.Format(remap.Source)} → {KeyNames.Format(remap.Target)}" + (remap.AppExe == null ? "" : $" in {remap.AppExe}");

    /// <summary>A file name for these items: the item's name for one item, "PuppyMacro items" for more.</summary>
    public static string FileName(IReadOnlyList<object> items)
    {
        string name = items.Count != 1 ? "PuppyMacro items" : items[0] switch
        {
            LoopDefinition loop => loop.Name,
            MacroDefinition macro => macro.Name,
            RemapDefinition remap => remap.Note.Trim().Length > 0 ? remap.Note : "Remap " + Keys(remap),
            _ => "PuppyMacro items",
        };
        foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(bad, ' ');
        name = name.Replace('→', '-').Trim();
        return (name.Length > 0 ? name : "PuppyMacro items") + Extension;
    }

    private static string Serialize(object item) => item switch
    {
        LoopDefinition loop => LoopJson.Serialize(loop),
        MacroDefinition macro => MacroJson.Serialize(macro),
        RemapDefinition remap => RemapJson.Serialize(remap),
        _ => throw new ArgumentException("Not a loop, macro or remap.", nameof(item)),
    };

    private static string VersionText(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}
