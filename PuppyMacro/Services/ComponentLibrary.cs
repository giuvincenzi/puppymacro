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
using PuppyMacro.Services.Components;

namespace PuppyMacro.Services;

/// <summary>
/// The Library: collections of components (<see cref="LibraryCollection"/>, <see cref="LibraryComponent"/>) that macros
/// link (a row of type <see cref="MacroActionType.Component"/>, a condition of kind <see cref="ConditionKind.Component"/>),
/// in <c>library.json</c> in the data folder. Its rules: a component is self-contained (its "Where … was found" use its own
/// conditions; what comes from outside comes as its parameters' values), and may call other components, never in a loop and
/// at most <see cref="ComponentSignatures.MaxDepth"/> deep; a linked component, or a collection with one, can't be deleted;
/// ids never change. A collection's content has a fingerprint, so two copies with the same id are told apart at import.
/// The checks of components and calls are in <c>Services/Components</c>.
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
        // The copy a save together left: a library.json it left missing is never read as an empty Library.
        MacroLibrary.RecoverBackup(_file);
        if (!File.Exists(_file))
            return;
        try
        {
            // A null where a list or a collection goes holds nothing: read as an empty list, that entry left out.
            var read = CodeJson.ReadFile<LibraryFile>(File.ReadAllText(_file)) ?? throw new JsonException("empty");
            if (read.Format > CurrentFormat)
                throw new JsonException("newer format");
            foreach (var collection in read.Collections)
                collection.AppExe = AppScope.Normalize(collection.AppExe);
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

    /// <summary>library.json's path, and its text as it would be saved now (<see cref="MacroLibrary.TrySaveTogether"/>).</summary>
    internal string FilePath => _file;

    internal string Text() => JsonSerializer.Serialize(new LibraryFile { Collections = Collections }, CodeJson.Options);

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
            File.WriteAllText(temp, Text());
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

    /// <summary>The Library's components as the checks of calls see them.</summary>
    public ComponentSignatures Signatures() => new(AllComponents);

    /// <summary>
    /// These collections and every collection whose components theirs call, down every call, each once, in that order: what
    /// a file needs so that its components can play where it is imported.
    /// </summary>
    public List<LibraryCollection> WithDependencies(IEnumerable<LibraryCollection> collections)
    {
        var result = new List<LibraryCollection>();
        var queue = new Queue<LibraryCollection>(collections);
        while (queue.Count > 0)
        {
            var collection = queue.Dequeue();
            if (result.Any(c => c.Id == collection.Id))
                continue;
            result.Add(collection);
            foreach (Guid called in collection.Components.SelectMany(c => ComponentSignatures.Callees(c.Actions)))
                if (Find(called) is { } found && !result.Any(c => c.Id == found.Collection.Id))
                    queue.Enqueue(found.Collection);
        }
        return result;
    }

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
    /// What a collection's content is, as a SHA-256 of its components' ids, signatures (each parameter's name, type, unit,
    /// example and default; what they return) and actions (sorted by id, compact JSON with the current model): two copies
    /// with the same id and fingerprint play the same and are called the same way. The components' names and descriptions,
    /// the parameters' descriptions, the app and the screen are left out, so renaming on one PC never blocks an import.
    /// Always computed with the code that compares, never read from a file (a newer model writes other text).
    /// </summary>
    public static string Fingerprint(LibraryCollection collection)
    {
        var content = collection.Components
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                Parameters = c.Parameters.Select(p => new { p.Name, p.Type, p.Unit, p.Example, p.Default }),
                c.Returns,
                c.Actions,
            })
            .ToList();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, CompactOptions)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ================= Rules =================

    /// <summary>Why a collection can't be saved (no name), or null.</summary>
    public static string? CheckCollection(LibraryCollection collection) =>
        string.IsNullOrWhiteSpace(collection.Name) ? Strings.Library_NameMissing : null;

    /// <summary>A concept: lowercase letters, digits and '_'.</summary>
    public static bool IsConcept(string? concept) => concept != null && ConceptPattern.IsMatch(concept);

    /// <summary>
    /// Why a component can't be saved into <paramref name="library"/>, or null: what <see cref="MacroJson.ParseComponent"/>
    /// checks (a name, a concept, at least one action, no groups, its parameters, what it returns, its actions as a macro's
    /// checks accept them with its parameters, its calls against the Library, no loop of calls, not too deep).
    /// </summary>
    /// <param name="hasImage">Whether PuppyMacro has an image; null skips that check.</param>
    /// <param name="library">The Library's components it is checked among (it replaces the one with its id); none when null.</param>
    public static string? CheckComponent(LibraryComponent component, Func<string, bool>? hasImage = null,
        IEnumerable<LibraryComponent>? library = null) =>
        Problems(component, hasImage, library).FirstOrDefault()?.Message;

    /// <summary>Every problem <see cref="CheckComponent"/> would name, with its line in the component's JSON.</summary>
    public static List<CodeProblem> Problems(LibraryComponent component, Func<string, bool>? hasImage = null,
        IEnumerable<LibraryComponent>? library = null)
    {
        MacroJson.ParseComponent(CodeJson.Serialize(component), component.Id, out var problems, hasImage,
            library ?? Enumerable.Empty<LibraryComponent>());
        return problems;
    }

    /// <summary>The calls to each component (linked rows and Component conditions), by component id, in these macros.</summary>
    public static Dictionary<Guid, int> Links(IEnumerable<MacroDefinition> macros)
    {
        var links = new Dictionary<Guid, int>();
        foreach (Guid id in macros.SelectMany(Called))
            links[id] = links.GetValueOrDefault(id) + 1;
        return links;
    }

    /// <summary>
    /// The components a macro calls, one id per call: its linked rows and its Component conditions (a component used only as
    /// a condition is used too).
    /// </summary>
    public static IEnumerable<Guid> Called(MacroDefinition macro)
    {
        foreach (var action in macro.AllActions)
        {
            if (action.Type == MacroActionType.Component && action.ComponentId is Guid id)
                yield return id;
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                if (condition.Kind == ConditionKind.Component && condition.ComponentId is Guid called)
                    yield return called;
        }
    }

    /// <summary>
    /// The macros and the other components that call a component directly (a linked row or a Component condition), by name.
    /// <paramref name="open"/>: what open editors hold now (a macro, or a component in the component editor, by its Id),
    /// counted instead of its saved version.
    /// </summary>
    public (List<string> Macros, List<string> Components) CallersOf(Guid id, IEnumerable<MacroDefinition> macros,
        IEnumerable<MacroDefinition>? open = null)
    {
        var (linking, calling) = Callers(new HashSet<Guid> { id }, macros, open);
        return (linking.Select(m => m.Name).ToList(), calling.Select(c => c.Name).ToList());
    }

    /// <summary>
    /// The macros and the components outside <paramref name="ids"/> that call one of them directly, each once; see
    /// <see cref="CallersOf"/>. <paramref name="savedToo"/>: an open item counts also by its saved version (a deletion is
    /// refused while either uses it: the saved one stays if the editor is cancelled).
    /// </summary>
    private (List<(Guid Id, string Name)> Macros, List<(Guid Id, string Name)> Components) Callers(HashSet<Guid> ids,
        IEnumerable<MacroDefinition> macros, IEnumerable<MacroDefinition>? open, bool savedToo = false)
    {
        // The last version of each open item counts, instead of the saved one (or with it).
        var editing = (open ?? Enumerable.Empty<MacroDefinition>()).GroupBy(m => m.Id).Select(g => g.Last()).ToList();
        var replaced = savedToo ? new HashSet<Guid>() : editing.Select(m => m.Id).ToHashSet();
        var linking = macros.Where(m => !replaced.Contains(m.Id))
            .Concat(editing.Where(m => Find(m.Id) == null))
            .Where(m => Called(m).Any(ids.Contains))
            .Select(m => (m.Id, m.Name))
            .DistinctBy(m => m.Id)
            .ToList();
        var calling = AllComponents.Where(c => !replaced.Contains(c.Id))
            .Select(c => (c.Id, c.Name, c.Actions))
            .Concat(editing.Where(m => Find(m.Id) != null).Select(m => (m.Id, m.Name, m.Actions)))
            .Where(c => !ids.Contains(c.Id) && ComponentSignatures.Callees(c.Actions).Any(ids.Contains))
            .Select(c => (c.Id, c.Name))
            .DistinctBy(c => c.Id)
            .ToList();
        return (linking, calling);
    }

    /// <summary>
    /// Why a component can't be deleted (macros link it, other components call it, as saved or as the open editors hold
    /// them), or null.
    /// </summary>
    public string? CanDeleteComponent(LibraryComponent component, IEnumerable<MacroDefinition> macros, IEnumerable<MacroDefinition>? open = null)
    {
        var (linking, calling) = Callers(new HashSet<Guid> { component.Id }, macros, open, savedToo: true);
        return InUse(component.Name, linking, calling,
            Strings.Library_ComponentInUse, Strings.Library_ComponentCalled, Strings.Library_ComponentInUseAndCalled);
    }

    /// <summary>
    /// Why a collection can't be deleted (macros link one of its components, components of other collections call one), or
    /// null.
    /// </summary>
    public string? CanDeleteCollection(LibraryCollection collection, IEnumerable<MacroDefinition> macros, IEnumerable<MacroDefinition>? open = null)
    {
        var (linking, calling) = Callers(collection.Components.Select(c => c.Id).ToHashSet(), macros, open, savedToo: true);
        return InUse(collection.Name, linking, calling,
            Strings.Library_CollectionInUse, Strings.Library_CollectionCalled, Strings.Library_CollectionInUseAndCalled);
    }

    /// <summary>The refusal that names who uses it: the macros, the components, or both; null when nothing does.</summary>
    private static string? InUse(string name, List<(Guid Id, string Name)> linking, List<(Guid Id, string Name)> calling,
        string byMacros, string byComponents, string byBoth)
    {
        string macroNames = string.Join(Strings.ListSeparator, linking.Select(m => m.Name));
        string componentNames = string.Join(Strings.ListSeparator, calling.Select(c => c.Name));
        return (linking.Count, calling.Count) switch
        {
            (0, 0) => null,
            (_, 0) => string.Format(CultureInfo.CurrentCulture, byMacros, name, macroNames),
            (0, _) => string.Format(CultureInfo.CurrentCulture, byComponents, name, componentNames),
            _ => string.Format(CultureInfo.CurrentCulture, byBoth, name, macroNames, componentNames),
        };
    }

    /// <summary>The ids of the images the components use (kept while the Library has them).</summary>
    public IEnumerable<string> ImageIds() => AllComponents.SelectMany(ImageStore.IdsIn);

    // ================= Linked rows =================

    /// <summary>The ids of the components a macro links (rows and Component conditions) that the Library doesn't have.</summary>
    public List<Guid> Missing(MacroDefinition macro) =>
        macro.AllActions
            .Where(a => a.Type == MacroActionType.Component)
            .Select(a => a.ComponentId ?? Guid.Empty)
            .Concat(macro.AllActions.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
                .Where(c => c.Kind == ConditionKind.Component).Select(c => c.ComponentId ?? Guid.Empty))
            .Where(id => Find(id) == null)
            .Distinct()
            .ToList();
}
