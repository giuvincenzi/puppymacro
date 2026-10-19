using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The macro editor in component mode (approved: the Design canvas "PuppyMacro Component functions", the component's
/// editor): the same action list, and on the right what the component is (Edit details), its parameters and whether it can
/// be used as a condition (what it finds). Save checks it as the Library does, asks the values the guided update needs
/// (<see cref="SignatureUpdateWindow"/>) and saves it with the calls that change, all together.
/// </summary>
public partial class MacroEditorWindow
{
    /// <summary>A parameter in the Parameters list.</summary>
    public sealed record ParameterCard(ComponentParameter Parameter, string Name, string Description);

    private LibraryComponent? _component;
    private List<ComponentParameter> _parameters = new();
    private readonly Dictionary<string, string> _renamed = new();
    private readonly ObservableCollection<ParameterCard> _parameterCards = new();
    private List<(Guid Id, string Name)> _returnChoices = new();

    /// <summary>Component mode: the component being edited (as saved); null for a macro.</summary>
    private bool IsComponent => _component != null;

    /// <summary>Inside a component: its parameters, which the action window offers; null for a macro.</summary>
    private IReadOnlyList<ComponentParameter>? EditingParameters => IsComponent ? _parameters : null;

    /// <summary>
    /// The component's own editor (from the Library): its actions as rows, its name in the Name box, its details, parameters
    /// and what it finds on the right. The macro's own cards are not there.
    /// </summary>
    private void StartComponentMode(LibraryComponent component)
    {
        _component = component.Clone();
        _parameters = component.Parameters.ConvertAll(p => p.Clone());
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Component_EditTitle, component.Name);
        MacroOptions.Visibility = Visibility.Collapsed;
        ComponentOptions.Visibility = Visibility.Visible;
        ParametersList.ItemsSource = _parameterCards;
        ShowDetails();
        ShowParameters();
        // The list of its conditions fills once the actions are loaded (Refresh).
        _pendingReturn = component.Returns?.Found;
        ReturnsSwitch.IsOn = component.Returns != null;
    }

    private void ShowDetails()
    {
        if (_component == null || _macros.Library.Find(_component.Id) is not { } found)
            return;
        DetailsCard.Description = AccessibleNames.Join(LibraryWindow.PurposeName(_component.Purpose),
            ComponentLibrary.StructuredName(found.Collection, _component));
    }

    private void ShowParameters()
    {
        _parameterCards.Clear();
        foreach (var parameter in _parameters)
        {
            string description = string.Join(" · ", new[]
            {
                ComponentChecks.TypeName(parameter.Type, parameter.Unit),
                parameter.Default == null ? Strings.Param_Required
                    : string.Format(CultureInfo.CurrentCulture, Strings.Param_DefaultIs, ComponentValues.Describe(parameter.Default, parameter.Type, parameter.Unit)),
            });
            _parameterCards.Add(new ParameterCard(parameter, parameter.Name, description));
        }
    }

    /// <summary>The conditions it can return: all of its conditions, by name (or kind).</summary>
    private void ShowReturns(Guid? selected)
    {
        _returnChoices = _list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
            .Select(c => (c.Id, string.IsNullOrWhiteSpace(c.Name) ? ConditionViewModel.KindName(c.Kind) : c.Name!))
            .ToList();
        ReturnsBox.ItemsSource = _returnChoices.Select(c => c.Name).ToList();
        int index = _returnChoices.FindIndex(c => c.Id == selected);
        ReturnsBox.SelectedIndex = index >= 0 ? index : _returnChoices.Count > 0 ? 0 : -1;
        ReturnsCard.Visibility = ReturnsSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private Guid? SelectedReturn =>
        ReturnsSwitch.IsOn && ReturnsBox.SelectedIndex >= 0 && ReturnsBox.SelectedIndex < _returnChoices.Count
            ? _returnChoices[ReturnsBox.SelectedIndex].Id
            : null;

    private void OnReturnsToggled(object sender, RoutedEventArgs e)
    {
        if (!IsComponent || _list == null)
            return;
        ShowReturns(SelectedReturn);
        Validate();
    }

    private void OnReturnsChanged(object sender, SelectionChangedEventArgs e) => Validate();

    // ---- Details ----

    private void OnEditDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_component == null)
            return;
        var draft = BuildComponent();
        var dialog = ComponentDetailsWindow.ForEdit(draft, _macros.Library, _macros.Images.Has);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.Details is not { } details)
            return;
        details.ApplyTo(_component);
        NameBox.Text = _component.Name;
        ShowDetails();
        Validate();
    }

    // ---- Parameters ----

    private void OnAddParameterClick(object sender, RoutedEventArgs e)
    {
        var window = new ParameterWindow(_engine, null, _parameters.Select(p => p.Name).ToList(), _macros.Images) { Owner = this };
        if (window.ShowDialog() != true || window.Result is not { } added)
            return;
        _parameters.Add(added);
        ShowParameters();
        Validate();
    }

    private void OnEditParameterClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ParameterCard card)
            return;
        var others = _parameters.Where(p => !ReferenceEquals(p, card.Parameter)).Select(p => p.Name).ToList();
        var window = new ParameterWindow(_engine, card.Parameter, others, _macros.Images) { Owner = this };
        if (window.ShowDialog() != true || window.Result is not { } edited)
            return;
        int index = _parameters.IndexOf(card.Parameter);
        string before = card.Parameter.Name;
        _parameters[index] = edited;
        if (edited.Name != before)
        {
            // Its fields and the calls that pass it on follow; the calls of other macros follow at Save.
            ComponentEdits.Rename(_list.Actions, before, edited.Name);
            string original = _renamed.FirstOrDefault(r => r.Value == before).Key ?? before;
            _renamed.Remove(original);
            if (_component!.Parameters.Any(p => p.Name == original))
                _renamed[original] = edited.Name;
        }
        if (edited.Type != card.Parameter.Type || edited.Unit != card.Parameter.Unit)
            ComponentEdits.Retyped(_list.Actions, card.Parameter, edited.Name); // its fields were of the old type: they keep their values
        ShowParameters();
        Refresh();
    }

    private void OnRemoveParameterClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ParameterCard card)
            return;
        // Its fields keep their values; a call inside that passed it on gets its example.
        ComponentEdits.Forget(_list.Actions, card.Parameter);
        _parameters.Remove(card.Parameter);
        foreach (string renamed in _renamed.Where(r => r.Value == card.Parameter.Name).Select(r => r.Key).ToList())
            _renamed.Remove(renamed);
        ShowParameters();
        Refresh();
    }

    // ---- The component as the editor has it ----

    /// <summary>The component with the editor's actions, parameters and what it finds; its bound fields hold the examples.</summary>
    private LibraryComponent BuildComponent()
    {
        var component = _component!.Clone();
        component.Name = NameBox.Text.Trim();
        component.Actions = _list.Actions.Select(a => a.Clone()).ToList();
        foreach (var action in MacroDefinition.Walk(component.Actions))
            action.GroupId = null;
        component.Parameters = _parameters.ConvertAll(p => p.Clone());
        component.Returns = SelectedReturn is Guid found ? new ComponentReturns { Found = found } : null;
        ComponentEdits.WriteExamples(component);
        return component;
    }

    /// <summary>The Library's other components and this one as the editor has it.</summary>
    private IEnumerable<LibraryComponent> LibraryWithThis(LibraryComponent component) =>
        _macros.Library.AllComponents.Where(c => c.Id != component.Id).Append(component);

    /// <summary>Why the component can't be saved as it is, or null.</summary>
    private string? ComponentProblem()
    {
        if (ReturnsSwitch.IsOn && SelectedReturn == null)
            return Strings.Component_ChooseWhatItFinds;
        var component = BuildComponent();
        return ComponentLibrary.Problems(component, _macros.Images.Has, LibraryWithThis(component)).Select(p => p.Message).FirstOrDefault();
    }

    /// <summary>
    /// Save in component mode: the component in place of the saved one; when its parameters changed for calls that exist,
    /// the values every call now needs (the guided update), and the macros and components whose calls change, saved with it.
    /// </summary>
    private void SaveComponent()
    {
        if (_component == null || SaveLibrary == null || _macros.Library.Find(_component.Id) is not { } saved)
            return;
        var after = BuildComponent();
        var update = SignatureUpdate.Compare(saved.Component, after, _macros.Macros, _macros.Library.AllComponents, _renamed);
        var values = new Dictionary<string, System.Text.Json.Nodes.JsonNode>();
        if (update.Needed)
        {
            var dialog = new SignatureUpdateWindow(_engine, update, new ComponentSignatures(LibraryWithThis(after)), _macros.Images) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            foreach (var (name, value) in dialog.Values)
                values[name] = value;
        }
        var (macros, components) = update.Apply(_macros.Macros, _macros.Library.AllComponents, values, after);
        string? problem = SaveLibrary(library =>
        {
            foreach (var changed in components.Append(after))
            {
                if (library.Find(changed.Id) is not { } found)
                    return Strings.Code_ComponentMissing;
                found.Collection.Components[found.Collection.Components.IndexOf(found.Component)] = changed.Clone();
            }
            return ComponentLibrary.CheckComponent(after, _macros.Images.Has, library.AllComponents);
        }, macros);
        if (problem != null)
        {
            ShowLibraryProblem(problem);
            return;
        }
        Result = null;
        DialogResult = true;
    }

    // ---- Code view in component mode ----

    /// <summary>The component's JSON for the Code view, as the editor has it.</summary>
    private string ComponentJson(MacroDefinition _) => CodeJson.Serialize(BuildComponent());

    /// <summary>The Code view's component read back: its parameters and what it finds come into the editor too.</summary>
    private MacroDefinition? ParseComponentJson(string text, out List<CodeProblem> problems)
    {
        var read = MacroJson.ParseComponent(text, _component!.Id, out problems, _macros.Images.Has,
            _macros.Library.AllComponents.Where(c => c.Id != _component.Id));
        if (read == null)
            return null;
        _component.Name = read.Name;
        _component.Purpose = read.Purpose;
        _component.Concept = read.Concept;
        _component.Description = read.Description;
        _parameters = read.Parameters.ConvertAll(p => p.Clone());
        ShowParameters();
        ShowDetails();
        ReturnsSwitch.IsOn = read.Returns != null;
        _pendingReturn = read.Returns?.Found;
        return new MacroDefinition { Id = _itemId, Name = read.Name, Actions = read.Actions };
    }

    /// <summary>What the Code view returned, chosen once the list shows its conditions.</summary>
    private Guid? _pendingReturn;
}
