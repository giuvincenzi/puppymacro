using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The Library: its collections and their components, one view in two modes (approved: the Design canvas "PuppyMacro
/// Library"). Pick (the macro editor's Add component): read only, a check box per component, Add linked gives the chosen
/// ones (<see cref="Picked"/>). Manage (the Macros page): change a component's details, its History, delete, rename or
/// delete a collection, import and export; every change goes through the main window's one way (<see cref="SaveChange"/>).
/// </summary>
public partial class LibraryWindow
{
    internal enum Mode
    {
        Pick,
        Manage,
    }

    /// <summary>A collection in the list on the left.</summary>
    public sealed record CollectionItem(LibraryCollection Collection, string Name, string Info);

    /// <summary>A component in the list on the right.</summary>
    public sealed class ComponentItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        internal ComponentItem(LibraryCollection collection, LibraryComponent component, bool picking, string useText)
        {
            Collection = collection;
            Component = component;
            Picking = picking;
            UseText = useText;
            Lines = ComponentLines(component.Actions);
        }

        internal LibraryCollection Collection { get; }
        internal LibraryComponent Component { get; }
        public bool Picking { get; }
        public bool Managing => !Picking;
        public string Name => Component.Name;
        public string StructuredName => ComponentLibrary.StructuredName(Collection, Component);
        public string Description => Component.Description;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Component.Description);
        public string UseText { get; }
        public IReadOnlyList<ComponentWindow.Line> Lines { get; }
        public string AccessibleName => AccessibleNames.Join(Name, StructuredName, UseText);

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                CheckedChanged?.Invoke();
            }
        }

        internal event Action? CheckedChanged;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly MacroLibrary _macros;
    private readonly Mode _mode;
    private readonly Func<Func<ComponentLibrary, string?>, string?>? _save;
    private readonly IReadOnlyList<MacroDefinition> _alsoLinked;
    private readonly HashSet<Guid> _checked = new();
    private Guid? _selectedCollection;
    private readonly bool _onlyConditions;

    /// <param name="save">Manage: the main window's one way to change the Library (<c>MainWindow.SaveLibrary</c>).</param>
    /// <param name="alsoLinked">Macros not saved yet that link components (the open editor's): they count as uses.</param>
    /// <param name="onlyConditions">Pick for a Component condition: only the components that say what they find (Returns).</param>
    internal LibraryWindow(MacroLibrary macros, Mode mode, Func<Func<ComponentLibrary, string?>, string?>? save = null,
        IEnumerable<MacroDefinition>? alsoLinked = null, string? appExe = null, bool onlyConditions = false)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _macros = macros;
        _mode = mode;
        _onlyConditions = onlyConditions;
        _save = save;
        _alsoLinked = alsoLinked?.ToList() ?? new List<MacroDefinition>();
        bool picking = mode == Mode.Pick;
        Title = picking ? Strings.Library_AddComponent : Strings.Library_Title;
        PickBar.Visibility = picking ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = picking ? Visibility.Collapsed : Visibility.Visible;
        CollectionCommands.Visibility = picking ? Visibility.Collapsed : Visibility.Visible;
        // Pick: the collection for the macro's app first.
        _selectedCollection = macros.Library.Collections
            .FirstOrDefault(c => appExe != null && AppScope.Same(c.AppExe, appExe))?.Id
            ?? macros.Library.Collections.FirstOrDefault()?.Id;
        Refresh();
        UpdatePickButton();
    }

    /// <summary>Pick: the components chosen, in the order they show.</summary>
    internal List<Guid> Picked { get; private set; } = new();

    /// <summary>The macros that link components: the saved ones and those of the open editor.</summary>
    private IEnumerable<MacroDefinition> LinkingMacros => _macros.Macros.Concat(_alsoLinked);

    private string Query => SearchBox.Text.Trim();

    private bool Matches(LibraryCollection collection, LibraryComponent component) =>
        (!_onlyConditions || component.Returns != null) && Matches(collection, component, Query);

    private static bool Matches(LibraryCollection collection, LibraryComponent component, string query) => query.Length == 0
        || component.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || component.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || ComponentLibrary.StructuredName(collection, component).Contains(query, StringComparison.OrdinalIgnoreCase)
        || collection.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Shows the collections (with something matching the search) and the selected one's components.</summary>
    private void Refresh()
    {
        var collections = _macros.Library.Collections
            .Where(c => c.Components.Any(k => Matches(c, k))
                        || (!_onlyConditions && (Query.Length == 0 || c.Name.Contains(Query, StringComparison.CurrentCultureIgnoreCase))))
            .Select(c => new CollectionItem(c, c.Name, CollectionInfo(c)))
            .ToList();
        CollectionList.ItemsSource = collections;
        var selected = collections.FirstOrDefault(c => c.Collection.Id == _selectedCollection) ?? collections.FirstOrDefault();
        CollectionList.SelectedItem = selected;
        ShowComponents(selected?.Collection);
        bool empty = _macros.Library.Collections.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CollectionList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"Game · Diablo IV.exe", "Office · Any app".</summary>
    internal static string CollectionInfo(LibraryCollection collection) =>
        AccessibleNames.Join(DomainName(collection.Domain), collection.AppExe ?? Strings.Library_AnyApp)
            .Replace(Strings.ListSeparator, " · ", StringComparison.Ordinal);

    internal static string DomainName(LibraryDomain domain) =>
        Strings.ResourceManager.GetString($"Library_Domain_{domain}", CultureInfo.CurrentUICulture) ?? domain.ToString();

    internal static string PurposeName(ComponentPurpose purpose) =>
        Strings.ResourceManager.GetString($"Library_Purpose_{purpose}", CultureInfo.CurrentUICulture) ?? purpose.ToString();

    /// <summary>"Used in 2 macros", or "Not used".</summary>
    private string UseText(LibraryComponent component)
    {
        int macros = LinkingMacros.Count(m => m.AllActions.Any(a => a.Type == MacroActionType.Component && a.ComponentId == component.Id));
        return macros == 0 ? Strings.Library_NotUsed : Plurals.Format("Library_UsedInMacros", macros);
    }

    private void ShowComponents(LibraryCollection? collection)
    {
        _selectedCollection = collection?.Id;
        CollectionTitle.Text = collection?.Name ?? "";
        ExportButton.IsEnabled = collection != null;
        CollectionMoreButton.IsEnabled = collection != null;
        var items = collection == null ? new List<ComponentItem>()
            : collection.Components.Where(c => Matches(collection, c))
                .Select(c => new ComponentItem(collection, c, _mode == Mode.Pick, UseText(c)) { IsChecked = _checked.Contains(c.Id) })
                .ToList();
        foreach (var item in items)
        {
            var current = item;
            current.CheckedChanged += () =>
            {
                if (current.IsChecked)
                    _checked.Add(current.Component.Id);
                else
                    _checked.Remove(current.Component.Id);
                UpdatePickButton();
            };
        }
        ComponentList.ItemsSource = items;
    }

    /// <summary>The lines of what a component plays, as the read-only window shows them.</summary>
    internal static IReadOnlyList<ComponentWindow.Line> ComponentLines(List<MacroAction> actions)
    {
        var lines = new List<ComponentWindow.Line>();
        ComponentWindow.AddLines(actions, 0, lines, ComponentWindow.ConditionNames(actions));
        return lines;
    }

    private void UpdatePickButton() => AddLinkedButton.IsEnabled = _checked.Count > 0;

    private void OnCollectionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CollectionList.SelectedItem is CollectionItem item && item.Collection.Id != _selectedCollection)
            ShowComponents(item.Collection);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    // ---- Pick ----

    private void OnAddLinkedClick(object sender, RoutedEventArgs e)
    {
        Picked = _macros.Library.AllComponents.Where(c => _checked.Contains(c.Id)).Select(c => c.Id).ToList();
        if (Picked.Count == 0)
            return;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Pick)
            DialogResult = false;
        else
            Close();
    }

    // ---- Manage ----

    /// <summary>Changes the Library through the main window; shows why it could not, and shows it again.</summary>
    private bool SaveChange(Func<ComponentLibrary, string?> change)
    {
        if (_save == null)
            return false;
        string? problem = _save(change);
        SetProblem(problem);
        Refresh();
        return problem == null;
    }

    /// <summary>Edit: the component's actions, parameters and what it finds, in the macro editor (set by the main window).</summary>
    internal Action<LibraryWindow, LibraryComponent>? EditRequested { get; set; }

    private void OnEditActionsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComponentItem item || EditRequested == null)
            return;
        EditRequested(this, item.Component);
        SetProblem(null);
        Refresh();
    }

    private void OnEditComponentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComponentItem item)
            return;
        var dialog = ComponentDetailsWindow.ForEdit(item.Component, _macros.Library, _macros.Images.Has);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.Details is not { } details)
            return;
        SaveChange(library =>
        {
            if (library.Find(item.Component.Id) is not { } found)
                return Strings.Code_ComponentMissing;
            details.ApplyTo(found.Component);
            return ComponentLibrary.CheckComponent(found.Component, _macros.Images.Has, library.AllComponents);
        });
    }

    private void OnComponentHistoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComponentItem item || HistoryProvider == null)
            return;
        var window = new HistoryWindow(item.Component.Name, HistoryProvider(item.Component.Id)) { Owner = this };
        window.RestoreRequested += version =>
        {
            if (HistoryRestore.ReadComponent(version, item.Component.Id) is not { } restored)
                return;
            if (SaveChange(library =>
                {
                    if (library.Find(item.Component.Id) is not { } found)
                        return Strings.Code_ComponentMissing;
                    found.Component.Actions = restored.Actions;
                    found.Component.Parameters = restored.Parameters;
                    found.Component.Returns = restored.Returns;
                    found.Component.Name = restored.Name;
                    found.Component.Purpose = restored.Purpose;
                    found.Component.Concept = restored.Concept;
                    found.Component.Description = restored.Description;
                    return ComponentLibrary.CheckComponent(found.Component, _macros.Images.Has, library.AllComponents);
                }))
                window.Close();
        };
        window.ShowDialog();
    }

    /// <summary>The History's versions of an item (set by the main window).</summary>
    internal Func<Guid, IReadOnlyList<HistoryVersion>>? HistoryProvider { get; init; }

    private async void OnDeleteComponentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComponentItem item)
            return;
        if (ComponentLibrary.CanDeleteComponent(item.Component, LinkingMacros) is string refused)
        {
            SetProblem(refused);
            return;
        }
        if (!await Confirm(string.Format(CultureInfo.CurrentCulture, Strings.Library_DeleteComponentConfirm, item.Component.Name)))
            return;
        SaveChange(library =>
        {
            if (library.Find(item.Component.Id) is not { } found)
                return null;
            found.Collection.Components.Remove(found.Component);
            return null;
        });
    }

    private void OnCollectionMoreClick(object sender, RoutedEventArgs e)
    {
        if (_macros.Library.FindCollection(_selectedCollection ?? Guid.Empty) is not { } collection)
            return;
        var menu = new ContextMenu { PlacementTarget = CollectionMoreButton, Placement = PlacementMode.Bottom };
        var rename = new MenuItem { Header = Strings.Library_RenameCollection };
        rename.Click += (_, _) => RenameCollection(collection);
        var delete = new MenuItem { Header = Strings.Library_DeleteCollection };
        delete.Click += (_, _) => DeleteCollection(collection);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private void RenameCollection(LibraryCollection collection)
    {
        var dialog = new GroupNameWindow(Strings.Library_RenameCollection, Strings.Library_Rename, collection.Name, "") { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } name)
            return;
        SaveChange(library =>
        {
            if (library.FindCollection(collection.Id) is not { } found)
                return null;
            found.Name = name;
            return ComponentLibrary.CheckCollection(found);
        });
    }

    private async void DeleteCollection(LibraryCollection collection)
    {
        if (ComponentLibrary.CanDeleteCollection(collection, LinkingMacros) is string refused)
        {
            SetProblem(refused);
            return;
        }
        if (!await Confirm(string.Format(CultureInfo.CurrentCulture, Strings.Library_DeleteCollectionConfirm, collection.Name)))
            return;
        SaveChange(library =>
        {
            library.Collections.RemoveAll(c => c.Id == collection.Id);
            return null;
        });
    }

    private async System.Threading.Tasks.Task<bool> Confirm(string text)
    {
        var dialog = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
        {
            Title = text,
            PrimaryButtonText = Strings.Library_Delete,
            CloseButtonText = Strings.Editor_Cancel,
            DefaultButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton.Close,
        };
        return await dialog.ShowAsync(this) == iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Primary;
    }

    /// <summary>Import and export of collections (<c>.puppyitems</c>), set by the main window.</summary>
    internal Action<LibraryWindow>? ImportRequested { get; init; }

    internal Action<LibraryWindow, LibraryCollection>? ExportRequested { get; init; }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        ImportRequested?.Invoke(this);
        Refresh();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_macros.Library.FindCollection(_selectedCollection ?? Guid.Empty) is { } collection)
            ExportRequested?.Invoke(this, collection);
    }

    /// <summary>Shows a problem in the window's bar (an import refused).</summary>
    internal void ShowProblem(string? problem)
    {
        SetProblem(problem);
        Refresh();
    }

    /// <summary>The bar's message, also its name for screen readers (an InfoBar's texts have no UI Automation peer).</summary>
    private void SetProblem(string? problem)
    {
        ProblemBar.Message = problem ?? "";
        ProblemBar.IsOpen = problem != null;
        System.Windows.Automation.AutomationProperties.SetName(ProblemBar, problem ?? "");
    }
}
