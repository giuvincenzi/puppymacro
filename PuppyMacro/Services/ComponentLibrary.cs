using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>
/// The Library: collections of components (<see cref="LibraryCollection"/>, <see cref="LibraryComponent"/>) that macros
/// link (a row of type <see cref="MacroActionType.Component"/>), in <c>library.json</c> in the data folder. Its rules: a
/// component is self-contained (its "Where … was found" use its own conditions) and holds no other component; a linked
/// component, or a collection with one, can't be deleted; ids never change. A collection's content has a fingerprint, so
/// two copies with the same id are told apart at import.
/// </summary>
internal sealed class ComponentLibrary
{
    /// <summary>The layout of library.json; raised only when it changes.</summary>
    public const int CurrentFormat = 1;

    public const string FileName = "library.json";

    private static readonly Regex ConceptPattern = new("^[a-z0-9_]+$", RegexOptions.CultureInvariant);

    // The fingerprint's text: the same model as the files, on one line (no line endings to differ).
    private static readonly JsonSerializerOptions CompactOptions = new(CodeJson.Options) { WriteIndented = false };

    private readonly string _file;

    public ComponentLibrary(string file) => _file = file;

    public List<LibraryCollection> Collections { get; private set; } = new();

    /// <summary>
    /// library.json could not be read: it was kept as library.invalid.json, and saving is refused, so a Library with
    /// linked components is never replaced by an empty one.
    /// </summary>
    public bool Broken { get; private set; }

    private sealed class LibraryFile
    {
        public int Format { get; set; } = CurrentFormat;
        public List<LibraryCollection> Collections { get; set; } = new();
    }

    public void Load(out string? warning)
    {
        warning = null;
        Broken = false;
        Collections = new List<LibraryCollection>();
        if (!File.Exists(_file))
            return;
        try
        {
            var read = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(_file), CodeJson.Options)
                ?? throw new JsonException("empty");
            if (read.Format > CurrentFormat)
                throw new JsonException("newer format");
            read.Collections.RemoveAll(c => c == null);
            foreach (var collection in read.Collections)
            {
                collection.Components.RemoveAll(c => c == null);
                collection.AppExe = AppScope.Normalize(collection.AppExe);
            }
            Collections = read.Collections;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Broken = true;
            try
            {
                File.Copy(_file, Path.ChangeExtension(_file, ".invalid.json"), overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                // The file stays where it is: saving is refused anyway.
            }
            warning = Strings.Library_FileUnreadable;
        }
    }

    public bool TrySave(out string? error)
    {
        if (Broken)
        {
            error = Strings.Library_FileUnreadable;
            return false;
        }
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new LibraryFile { Collections = Collections }, CodeJson.Options));
            File.Move(temp, _file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public LibraryCollection? FindCollection(Guid id) => Collections.FirstOrDefault(c => c.Id == id);

    /// <summary>A component and its collection, by the component's id.</summary>
    public (LibraryCollection Collection, LibraryComponent Component)? Find(Guid componentId)
    {
        foreach (var collection in Collections)
            if (collection.Components.FirstOrDefault(c => c.Id == componentId) is { } component)
                return (collection, component);
        return null;
    }

    public IEnumerable<LibraryComponent> AllComponents => Collections.SelectMany(c => c.Components);

    // ================= Names =================

    /// <summary>
    /// The structured name the AI apps search by, in English: domain-app-purpose-concept
    /// ("game-diablo_iv-act-remove_one_affix"); the app is its exe's name, lowercase, '_' for anything else, or "any".
    /// </summary>
    public static string StructuredName(LibraryCollection collection, LibraryComponent component) =>
        string.Join("-", collection.Domain.ToString().ToLowerInvariant(), AppSlug(collection.AppExe),
            component.Purpose.ToString().ToLowerInvariant(), component.Concept);

    public static string AppSlug(string? appExe)
    {
        if (string.IsNullOrWhiteSpace(appExe))
            return "any";
        string name = appExe.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var slug = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '_')
                slug.Append('_');
        }
        string result = slug.ToString().Trim('_');
        return result.Length == 0 ? "any" : result;
    }

    // ================= Fingerprint =================

    /// <summary>
    /// What a collection's content is, as a SHA-256 of its components' ids and actions (sorted by id, compact JSON with the
    /// current model): two copies with the same id and fingerprint play the same. Names, descriptions, the app and the
    /// screen are left out, so renaming on one PC never blocks an import. Always computed with the code that compares, never
    /// read from a file (a newer model writes other text).
    /// </summary>
    public static string Fingerprint(LibraryCollection collection)
    {
        var content = collection.Components
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Actions })
            .ToList();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, CompactOptions)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ================= Rules =================

    /// <summary>Why a collection can't be saved (no name), or null.</summary>
    public static string? CheckCollection(LibraryCollection collection) =>
        string.IsNullOrWhiteSpace(collection.Name) ? Strings.Library_NameMissing : null;

    /// <summary>
    /// Why a component can't be saved, or null: a name, a concept of lowercase letters, digits and '_', at least one
    /// action, no linked component inside, no groups, and its actions as a macro's checks accept them (so its "Where …
    /// was found" use its own conditions only, and its condition ids and names are its own).
    /// </summary>
    /// <param name="hasImage">Whether PuppyMacro has an image; null skips that check.</param>
    public static string? CheckComponent(LibraryComponent component, Func<string, bool>? hasImage = null)
    {
        if (string.IsNullOrWhiteSpace(component.Name))
            return Strings.Library_NameMissing;
        if (!ConceptPattern.IsMatch(component.Concept))
            return Strings.Library_ConceptInvalid;
        if (component.Actions.Count == 0)
            return Strings.Library_NoActions;
        if (MacroDefinition.Walk(component.Actions).Any(a => a.Type == MacroActionType.Component))
            return Strings.Library_NestedComponent;
        var alone = new MacroDefinition { Name = component.Name, Actions = component.Actions.ConvertAll(a => a.Clone()) };
        foreach (var action in MacroDefinition.Walk(alone.Actions))
            action.GroupId = null;
        MacroJson.Parse(MacroJson.Serialize(alone), alone.Id, (_, _) => null, out var problems, hasImage);
        return problems.Count == 0 ? null : problems[0].Message;
    }

    /// <summary>The rows linking each component, by component id, in these macros.</summary>
    public static Dictionary<Guid, int> Links(IEnumerable<MacroDefinition> macros)
    {
        var links = new Dictionary<Guid, int>();
        foreach (var action in macros.SelectMany(m => m.AllActions))
            if (action.Type == MacroActionType.Component && action.ComponentId is Guid id)
                links[id] = links.GetValueOrDefault(id) + 1;
        return links;
    }

    /// <summary>The macros that link a component, by name, or none.</summary>
    private static List<string> LinkedIn(IEnumerable<Guid> components, IEnumerable<MacroDefinition> macros)
    {
        var ids = components.ToHashSet();
        return macros
            .Where(m => m.AllActions.Any(a => a.Type == MacroActionType.Component && a.ComponentId is Guid id && ids.Contains(id)))
            .Select(m => m.Name)
            .ToList();
    }

    /// <summary>Why a component can't be deleted (macros link it, the open editors' too), or null.</summary>
    public static string? CanDeleteComponent(LibraryComponent component, IEnumerable<MacroDefinition> macros)
    {
        var linked = LinkedIn(new[] { component.Id }, macros);
        return linked.Count == 0 ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Library_ComponentInUse, component.Name, string.Join(Strings.ListSeparator, linked));
    }

    /// <summary>Why a collection can't be deleted (macros link one of its components), or null.</summary>
    public static string? CanDeleteCollection(LibraryCollection collection, IEnumerable<MacroDefinition> macros)
    {
        var linked = LinkedIn(collection.Components.Select(c => c.Id), macros);
        return linked.Count == 0 ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Library_CollectionInUse, collection.Name, string.Join(Strings.ListSeparator, linked));
    }

    /// <summary>The ids of the images the components use (kept while the Library has them).</summary>
    public IEnumerable<string> ImageIds() =>
        Collections.SelectMany(c => c.Components).SelectMany(c => ImageStore.IdsIn(new MacroDefinition { Actions = c.Actions }));

    // ================= Linked rows =================

    /// <summary>The ids of the components a macro links that the Library doesn't have.</summary>
    public List<Guid> Missing(MacroDefinition macro) =>
        macro.AllActions
            .Where(a => a.Type == MacroActionType.Component)
            .Select(a => a.ComponentId ?? Guid.Empty)
            .Where(id => Find(id) == null)
            .Distinct()
            .ToList();

    /// <summary>
    /// A copy of the macro to play: each linked row gets its component's actions (<see cref="MacroAction.Resolved"/>),
    /// each link with new condition ids, so a component linked twice, or sharing ids with the macro, keeps its found
    /// points apart. Null when a linked component is missing (the macro can't play).
    /// </summary>
    public MacroDefinition? Resolve(MacroDefinition macro)
    {
        var copy = macro.Clone();
        foreach (var action in copy.AllActions.ToList())
        {
            if (action.Type != MacroActionType.Component)
                continue;
            if (action.ComponentId is not Guid id || Find(id) is not { } found)
                return null;
            var actions = found.Component.Actions.ConvertAll(a => a.Clone());
            MacroEditList.RenewConditions(actions);
            action.Resolved = actions;
            action.ResolvedName = found.Component.Name;
        }
        return copy;
    }
}
