using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// A component's details: its name, purpose, concept (the end of its structured name) and description. For Save as
/// component also its collection, an existing one or a new one (its kind of app, its app and the screen it is made on).
/// </summary>
public partial class ComponentDetailsWindow
{
    /// <summary>What the user chose: the details, and the collection (its id, or a new one to add).</summary>
    internal sealed record ComponentDetails(string Name, ComponentPurpose Purpose, string Concept, string Description,
        Guid? CollectionId, LibraryCollection? NewCollection)
    {
        public void ApplyTo(LibraryComponent component)
        {
            component.Name = Name;
            component.Purpose = Purpose;
            component.Concept = Concept;
            component.Description = Description;
        }
    }

    private readonly bool _saving;
    private readonly List<MacroAction> _actions;
    private readonly Func<string, bool> _hasImage;
    private readonly LibraryScreen? _screen;
    private bool _conceptTyped;
    private bool _settingConcept;

    private ComponentDetailsWindow(ComponentLibrary library, bool saving, List<MacroAction> actions, Func<string, bool> hasImage, string? appExe)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _saving = saving;
        _actions = actions;
        _hasImage = hasImage;
        foreach (ComponentPurpose purpose in Enum.GetValues<ComponentPurpose>())
            PurposeBox.Items.Add(new ComboBoxItem { Content = LibraryWindow.PurposeName(purpose), Tag = purpose });
        foreach (LibraryDomain domain in Enum.GetValues<LibraryDomain>())
            DomainBox.Items.Add(new ComboBoxItem { Content = LibraryWindow.DomainName(domain), Tag = domain });
        InfoText.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        CollectionPanel.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
        if (saving)
        {
            foreach (var collection in library.Collections)
                CollectionBox.Items.Add(new ComboBoxItem { Content = collection.Name, Tag = collection.Id });
            CollectionBox.Items.Add(new ComboBoxItem { Content = Strings.Library_NewCollection, Tag = null });
            // The collection for the macro's app, else a new one.
            int index = library.Collections.FindIndex(c => appExe != null && AppScope.Same(c.AppExe, appExe));
            CollectionBox.SelectedIndex = index >= 0 ? index : CollectionBox.Items.Count - 1;
            AppBox.Text = appExe ?? "";
            DomainBox.SelectedIndex = (int)LibraryDomain.Other;
            _screen = LibraryScreens.Of(actions);
            ScreenText.Text = _screen == null ? Strings.Library_ScreenUnknown
                : string.Format(CultureInfo.CurrentCulture, Strings.Library_ScreenValue, _screen.Width, _screen.Height, _screen.ScalePercent);
            ActionsList.ItemsSource = LibraryWindow.ComponentLines(actions);
            Title = Strings.Library_SaveAsTitle;
        }
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Save as component for these actions of a macro working in <paramref name="appExe"/>.</summary>
    internal static ComponentDetailsWindow ForSave(List<MacroAction> actions, ComponentLibrary library, Func<string, bool> hasImage, string? appExe)
    {
        var window = new ComponentDetailsWindow(library, saving: true, actions, hasImage, appExe);
        window.PurposeBox.SelectedIndex = (int)GuessPurpose(actions);
        window.Validate();
        return window;
    }

    /// <summary>A component's details in the Library.</summary>
    internal static ComponentDetailsWindow ForEdit(LibraryComponent component, ComponentLibrary library, Func<string, bool> hasImage)
    {
        var window = new ComponentDetailsWindow(library, saving: false, component.Actions, hasImage, null)
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.Component_Title, component.Name),
        };
        window.NameBox.Text = component.Name;
        window._conceptTyped = true;
        window.SetConcept(component.Concept);
        window.DescriptionBox.Text = component.Description;
        window.PurposeBox.SelectedIndex = (int)component.Purpose;
        window.Validate();
        return window;
    }

    internal ComponentDetails? Details { get; private set; }

    /// <summary>One If: it finds something; one click or move: a point; anything else: an operation.</summary>
    private static ComponentPurpose GuessPurpose(List<MacroAction> actions) => actions switch
    {
        [{ Type: MacroActionType.If }] => ComponentPurpose.Find,
        [{ Type: MacroActionType.Click or MacroActionType.MoveTo or MacroActionType.MouseDown or MacroActionType.MouseUp }] => ComponentPurpose.Point,
        _ => ComponentPurpose.Act,
    };

    /// <summary>A name's concept: English letters and digits, lowercase, '_' for the rest ("Remove one affix" → "remove_one_affix").</summary>
    internal static string ConceptOf(string name)
    {
        var concept = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                concept.Append(c);
            else if (concept.Length > 0 && concept[^1] != '_')
                concept.Append('_');
        }
        return concept.ToString().Trim('_');
    }

    private void SetConcept(string concept)
    {
        _settingConcept = true;
        ConceptBox.Text = concept;
        _settingConcept = false;
    }

    private bool NewCollection => _saving && CollectionBox.SelectedItem is ComboBoxItem { Tag: null };

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_conceptTyped && ReferenceEquals(sender, NameBox))
            SetConcept(ConceptOf(NameBox.Text));
        Validate();
    }

    private void OnConceptChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingConcept)
            _conceptTyped = true;
        Validate();
    }

    private void OnCollectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NewCollectionPanel != null)
            NewCollectionPanel.Visibility = NewCollection ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    private LibraryComponent Draft() => new()
    {
        Name = NameBox.Text.Trim(),
        Purpose = PurposeBox.SelectedItem is ComboBoxItem { Tag: ComponentPurpose purpose } ? purpose : ComponentPurpose.Act,
        Concept = ConceptBox.Text.Trim(),
        Description = DescriptionBox.Text.Trim(),
        Actions = _actions.ConvertAll(a => a.Clone()),
    };

    private string? Problem()
    {
        if (NewCollection && string.IsNullOrWhiteSpace(CollectionNameBox.Text))
            return Strings.Library_NameMissing;
        return ComponentLibrary.CheckComponent(Draft(), _hasImage);
    }

    private void Validate()
    {
        if (SaveButton == null)
            return;
        string? problem = Problem();
        ErrorText.Text = problem ?? "";
        SaveButton.IsEnabled = problem == null;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Problem() != null)
        {
            Validate();
            return;
        }
        var draft = Draft();
        LibraryCollection? created = null;
        Guid? collectionId = null;
        if (_saving)
        {
            if (NewCollection)
            {
                created = new LibraryCollection
                {
                    Name = CollectionNameBox.Text.Trim(),
                    Domain = DomainBox.SelectedItem is ComboBoxItem { Tag: LibraryDomain domain } ? domain : LibraryDomain.Other,
                    AppExe = AppScope.Normalize(AppBox.Text),
                    Screen = _screen,
                };
                collectionId = created.Id;
            }
            else if (CollectionBox.SelectedItem is ComboBoxItem { Tag: Guid id })
                collectionId = id;
        }
        Details = new ComponentDetails(draft.Name, draft.Purpose, draft.Concept, draft.Description, collectionId, created);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
