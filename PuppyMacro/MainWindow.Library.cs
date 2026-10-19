using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;

namespace PuppyMacro;

/// <summary>
/// The Library (<see cref="ComponentLibrary"/>): the one way it changes (<see cref="SaveLibrary"/>), its History, and the
/// windows that show it.
/// </summary>
public partial class MainWindow
{
    private ComponentLibrary Library => _macros.Library;

    /// <summary>
    /// Every change of the Library goes through here (the Library window, the macro editor's Save as component and
    /// component editing, an AI app): refused while something runs (a playing macro must not change under it);
    /// <paramref name="change"/> changes the Library or answers why it can't; then every component and every macro's calls
    /// are checked against the Library as it would be (<see cref="ComponentChecks.CheckAll"/>), and a problem the change
    /// brings refuses it (one that was there before does not block it); then it is saved with <paramref name="macros"/>
    /// (copies of the macros whose calls a new signature changed, <see cref="SignatureUpdate"/>), all together or nothing,
    /// their History recorded, the engine's snapshot made again (the calls follow) and the images nobody uses deleted. A
    /// change that is refused or can't be saved leaves the Library and the macros as they were. Answers why it did not
    /// change, or null.
    /// </summary>
    internal string? SaveLibrary(Func<ComponentLibrary, string?> change, IReadOnlyList<MacroDefinition>? macros = null) =>
        SaveLibrary(change, onlyAdds: false, macros);

    /// <param name="onlyAdds">The change only adds collections (an import): it may happen while something runs, as nothing
    /// that plays changes.</param>
    /// <param name="refusals">The language of the answer when it is refused (InvariantCulture: English, for an AI app); the
    /// language of the moment when null. Only the checks run in it: what a saved change shows stays in the user's.</param>
    private string? SaveLibrary(Func<ComponentLibrary, string?> change, bool onlyAdds, IReadOnlyList<MacroDefinition>? macros = null,
        System.Globalization.CultureInfo? refusals = null)
    {
        macros ??= Array.Empty<MacroDefinition>();
        var before = Library.Collections.Select(c => c.Clone()).ToList();
        string? problem = Loc.In(refusals ?? System.Globalization.CultureInfo.CurrentUICulture, () =>
        {
            if (_engine.AnyRunning && !onlyAdds)
                return Strings.Library_StopFirst;
            if (Library.Broken)
                return Strings.Library_FileUnreadable;
            var known = ProblemKeys(_macros.Macros);
            string? why = change(Library);
            if (why == null)
            {
                var after = _macros.Macros.Select(m => macros.FirstOrDefault(x => x.Id == m.Id) ?? m).ToList();
                var fresh = ComponentChecks.CheckAll(Library.Signatures(), after, _macros.Images.Has)
                    .FirstOrDefault(p => !known.Contains((p.ItemId, p.Path, p.Message)));
                if (fresh != null)
                    why = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library_ChangeBreaks, fresh.Where, fresh.Message);
            }
            if (why == null && !_macros.TrySaveTogether(macros, out string? error))
                why = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library_SaveFailed, error);
            return why;
        });
        if (problem != null)
        {
            Library.Collections.Clear();
            Library.Collections.AddRange(before);
            return problem;
        }
        foreach (var changed in macros)
        {
            if (_macros.Find(changed.Id) is not { } saved)
                continue;
            saved.CopyFrom(changed);
            _itemHistory?.Record(saved.Id, MacroJson.Serialize(saved), _savedBy);
        }
        RecordLibraryHistory();
        _engine.Publish(EngineSnapshot.From(_settings, _macros));
        _macros.ReleaseUnusedImages();
        RefreshMacros();
        return null;
    }

    /// <summary>The problems the Library and these macros' calls have now, so a change is judged only by the ones it brings.</summary>
    private HashSet<(Guid, string, string)> ProblemKeys(IEnumerable<MacroDefinition> macros) =>
        ComponentChecks.CheckAll(Library.Signatures(), macros, _macros.Images.Has).Select(p => (p.ItemId, p.Path, p.Message)).ToHashSet();

    /// <summary>
    /// The new collections of a file being imported, added whole before its items (they may link them); the ones the
    /// Library has meanwhile are left as they are. Answers why they could not be added (in <paramref name="refusals"/>, the
    /// language of the moment when null), or null.
    /// </summary>
    internal string? AddCollections(IReadOnlyList<LibraryCollection> collections, System.Globalization.CultureInfo? refusals = null) =>
        collections.Count == 0 ? null : SaveLibrary(library =>
        {
            foreach (var collection in collections)
                if (library.FindCollection(collection.Id) == null)
                    library.Collections.Add(collection.Clone());
            return null;
        }, onlyAdds: true, refusals: refusals);

    /// <summary>
    /// Saves a component (an AI app's, a Restore): one the Library has gets its new content where it is; a new one goes into
    /// <paramref name="created"/> (a new collection, added). Checked as the Library window checks it. Why not, or null.
    /// </summary>
    /// <param name="macros">Copies of the macros whose calls the new signature changed, saved with it.</param>
    /// <param name="callers">Copies of the components whose calls it changed, saved with it.</param>
    /// <param name="refusals">The language of the answer when it is refused; the language of the moment when null.</param>
    internal string? SaveComponent(LibraryComponent component, LibraryCollection? created, IReadOnlyList<MacroDefinition>? macros = null,
        IReadOnlyList<LibraryComponent>? callers = null, System.Globalization.CultureInfo? refusals = null) => SaveLibrary(library =>
    {
        foreach (var caller in callers ?? Array.Empty<LibraryComponent>())
            if (library.Find(caller.Id) is { } calling)
                calling.Collection.Components[calling.Collection.Components.IndexOf(calling.Component)] = caller.Clone();
        if (library.Find(component.Id) is { } found)
        {
            int index = found.Collection.Components.IndexOf(found.Component);
            found.Collection.Components[index] = component.Clone();
        }
        else
        {
            if (created == null)
                return Strings.Code_ComponentMissing;
            if (ComponentLibrary.CheckCollection(created) is string bad)
                return bad;
            var collection = library.FindCollection(created.Id);
            if (collection == null)
            {
                collection = created.Clone();
                collection.Components.Clear();
                library.Collections.Add(collection);
            }
            collection.Components.Add(component.Clone());
        }
        return ComponentLibrary.CheckComponent(component, _macros.Images.Has, library.AllComponents);
    }, onlyAdds: false, macros, refusals);

    /// <summary>The Library's Import: the same Import as the title bar's (a file's items and collections).</summary>
    private void ImportCollections(LibraryWindow window) => ImportItems(null, null, null);

    /// <summary>The Library's Export collection: a .puppyitems file with the collection, whole, and its images.</summary>
    private void ExportCollection(LibraryWindow owner, LibraryCollection collection)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Library_ExportCollection,
            FileName = ItemFile.SafeFileName(collection.Name),
            Filter = ItemsFilter,
            DefaultExt = ItemFile.Extension,
        };
        if (dialog.ShowDialog(owner) != true)
            return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName,
                ItemFile.Write(Array.Empty<object>(), ItemFile.AppVersion, _macros.Images, Library, new[] { collection }));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            owner.ShowProblem(ex.Message);
        }
    }

    /// <summary>A macro editor reaches the Library through the main window: Save as component, and Open in Library.</summary>
    private void PrepareEditor(MacroEditorWindow editor)
    {
        editor.SaveLibrary = (change, macros) => SaveLibrary(change, macros);
        editor.OpenLibrary = (owner, editing) => ShowLibrary(owner, editing);
    }

    /// <summary>The Library button of the Macros page.</summary>
    private void OnLibraryClick(object sender, System.Windows.RoutedEventArgs e) => ShowLibrary(this, null);

    /// <summary>
    /// The Library to manage, over <paramref name="owner"/>; <paramref name="editing"/>: the macro of an open editor, whose
    /// links count as uses (its component can't be deleted while it links it). Hotkeys are off meanwhile, as with the
    /// editors, and AI apps wait.
    /// </summary>
    private void ShowLibrary(System.Windows.Window owner, MacroDefinition? editing)
    {
        var window = new LibraryWindow(_macros, LibraryWindow.Mode.Manage, change => SaveLibrary(change), editing == null ? null : new[] { editing })
        {
            Owner = owner,
            HistoryProvider = id => _itemHistory?.Versions(id) ?? new List<HistoryVersion>(),
            RestoreImages = (id, json) => _itemHistory?.RestoreImages(id, json),
            ImportRequested = ImportCollections,
            ExportRequested = ExportCollection,
            EditRequested = EditComponent,
        };
        if (ReferenceEquals(owner, this))
            ShowDialogWithoutHotkeys(window);
        else
            window.ShowDialog();
    }

    /// <summary>
    /// The Library's Edit: the component in the macro editor's component mode, over the Library window. It saves itself
    /// (its Save goes through <see cref="SaveLibrary"/>, with the macros whose calls change).
    /// </summary>
    private void EditComponent(LibraryWindow owner, LibraryComponent component)
    {
        if (_engine.AnyRunning)
        {
            owner.ShowProblem(Strings.Library_StopFirst);
            return;
        }
        var editor = new MacroEditorWindow(_engine, _settings, _macros, _sounds, null, component: component) { Owner = owner };
        PrepareEditor(editor);
        editor.ShowDialog();
        RefreshMacros();
    }

    /// <summary>A collection as the History keeps it: what it is, and its components by id (each has a History of its own).</summary>
    private static string CollectionJson(LibraryCollection collection) => CodeJson.Serialize(new
    {
        collection.Id,
        collection.Name,
        collection.Domain,
        collection.AppExe,
        collection.Screen,
        Components = collection.Components.Select(c => c.Id).ToList(),
    });

    /// <summary>The Library's ids and their JSON for the History: every collection and every component.</summary>
    private IEnumerable<(Guid Id, string Json)> LibraryItems() =>
        Library.Collections.Select(c => (c.Id, CollectionJson(c)))
            .Concat(Library.AllComponents.Select(c => (c.Id, CodeJson.Serialize(c))));

    /// <summary>Each collection and component that changed gets a version; the ones gone are recorded deleted by <see cref="RecordHistory"/>.</summary>
    private void RecordLibraryHistory()
    {
        if (_itemHistory == null)
            return;
        foreach (var (id, json) in LibraryItems())
            _itemHistory.Record(id, json, _savedBy);
        RecordHistory();
    }
}
