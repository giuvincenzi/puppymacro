using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>What the user chose in the window of a linked row.</summary>
internal enum ComponentWindowChoice
{
    Close,
    Unlink,
    OpenLibrary,
}

/// <summary>
/// A row linked to a Library component, read only: which collection it is in, the macros that link it, and what it plays;
/// Unlink this row and Open in Library (approved: the Design canvas "PuppyMacro Library").
/// </summary>
public partial class ComponentWindow
{
    /// <summary>One line of what the component plays: an action, or Then / Else, indented under its If.</summary>
    public sealed record Line(Thickness Indent, string Title, string Detail);

    internal ComponentWindow(LibraryCollection collection, LibraryComponent component, IEnumerable<MacroDefinition> macros, bool canUnlink)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Component_Title, component.Name);
        CollectionText.Text = collection.Name;
        CollectionCard.Description = ComponentLibrary.StructuredName(collection, component);
        UsedInText.Text = UsedIn(component, macros);
        UnlinkButton.Visibility = canUnlink ? Visibility.Visible : Visibility.Collapsed;
        var lines = new List<Line>();
        AddLines(component.Actions, 0, lines, ConditionNames(component.Actions));
        ActionsList.ItemsSource = lines;
    }

    internal ComponentWindowChoice Choice { get; private set; } = ComponentWindowChoice.Close;

    /// <summary>"D4 Strip Affixes, D4 Reroll one", or "Not used".</summary>
    internal static string UsedIn(LibraryComponent component, IEnumerable<MacroDefinition> macros)
    {
        var names = macros
            .Where(m => m.AllActions.Any(a => a.Type == MacroActionType.Component && a.ComponentId == component.Id))
            .Select(m => m.Name)
            .ToList();
        return names.Count == 0 ? Strings.Library_NotUsed : string.Join(Strings.ListSeparator, names);
    }

    internal static Dictionary<System.Guid, string> ConditionNames(IEnumerable<MacroAction> actions) =>
        MacroDefinition.Walk(actions).SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
            .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name ?? "");

    internal static void AddLines(List<MacroAction> actions, int indent, List<Line> lines, Dictionary<System.Guid, string> names)
    {
        foreach (var action in actions)
        {
            var row = new MacroActionRowViewModel(action)
            {
                FoundName = action.FoundId is System.Guid id && names.TryGetValue(id, out string? name) ? name : "",
            };
            lines.Add(new Line(new Thickness(indent * 24, 0, 0, 0), row.Title, row.Detail));
            if (action.If is not { } block)
                continue;
            lines.Add(new Line(new Thickness((indent + 1) * 24, 0, 0, 0), Strings.Macro_Then, ""));
            AddLines(block.Then, indent + 2, lines, names);
            if (block.Else.Count == 0)
                continue;
            lines.Add(new Line(new Thickness((indent + 1) * 24, 0, 0, 0), Strings.Macro_Else, ""));
            AddLines(block.Else, indent + 2, lines, names);
        }
    }

    private void OnUnlinkClick(object sender, RoutedEventArgs e)
    {
        Choice = ComponentWindowChoice.Unlink;
        DialogResult = true;
    }

    private void OnOpenLibraryClick(object sender, RoutedEventArgs e)
    {
        Choice = ComponentWindowChoice.OpenLibrary;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
