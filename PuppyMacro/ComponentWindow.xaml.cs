using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
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

    /// <summary>Save: the row's arguments changed (<see cref="ComponentWindow.Arguments"/>).</summary>
    Save,
}

/// <summary>
/// A row linked to a Library component, read only: which collection it is in, the macros and components that use it, and
/// what it plays;
/// Unlink this row and Open in Library (approved: the Design canvas "PuppyMacro Library").
/// </summary>
public partial class ComponentWindow
{
    /// <summary>One line of what the component plays: an action, or Then / Else, indented under its If.</summary>
    public sealed record Line(Thickness Indent, string Title, string Detail);

    /// <param name="usedBy">The macros and the components that use it (<see cref="ComponentLibrary.CallersOf"/>), by name.</param>
    /// <param name="arguments">A row's values for the component's parameters, editable (null: read only, as the Library shows it).</param>
    internal ComponentWindow(LibraryCollection collection, LibraryComponent component, (List<string> Macros, List<string> Components) usedBy,
        bool canUnlink,
        List<ArgumentViewModel>? arguments = null, LoopEngine? engine = null)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        if (engine != null)
        {
            KeyCaptureField.SetEngine(this, engine); // a Key argument waits for keys through it
            Closed += (_, _) => engine.CancelCapture();
        }
        _arguments = arguments;
        if (arguments is { Count: > 0 })
        {
            ArgumentsPanel.Visibility = Visibility.Visible;
            ArgumentsEditor.Arguments = arguments;
            InfoBar.Message = Strings.Component_LinkedInfoArguments;
            SaveButton.Visibility = Visibility.Visible;
            CloseButton.Content = Strings.Editor_Cancel;
            CloseButton.Style = (Style)FindResource("DefaultButtonStyle");
            CloseButton.IsDefault = false;
            foreach (var argument in arguments)
            {
                argument.PropertyChanged += (_, _) => Validate();
                argument.Value.PropertyChanged += (_, _) => Validate();
            }
            Validate();
        }
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Component_Title, component.Name);
        CollectionText.Text = collection.Name;
        CollectionCard.Description = ComponentLibrary.StructuredName(collection, component);
        UsedInText.Text = UsedIn(usedBy.Macros, usedBy.Components);
        UnlinkButton.Visibility = canUnlink ? Visibility.Visible : Visibility.Collapsed;
        var lines = new List<Line>();
        AddLines(component.Actions, 0, lines, ConditionNames(component.Actions));
        ActionsList.ItemsSource = lines;
    }

    internal ComponentWindowChoice Choice { get; private set; } = ComponentWindowChoice.Close;

    private readonly List<ArgumentViewModel>? _arguments;

    /// <summary>Save: the row's arguments as the cards give them.</summary>
    internal Dictionary<string, JsonNode?>? Arguments => _arguments == null ? null : ArgumentViewModel.ToArguments(_arguments);

    private void Validate()
    {
        string? problem = ArgumentsEditor.Problem;
        ErrorText.Text = problem ?? "";
        ErrorText.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = problem == null;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.Focus(); // commit the number being edited
        if (ArgumentsEditor.Problem != null)
        {
            Validate();
            return;
        }
        Choice = ComponentWindowChoice.Save;
        DialogResult = true;
    }

    /// <summary>"D4 Strip Affixes, D4 Reroll one, Strip one slot": the macros, then the components; or "Not used".</summary>
    internal static string UsedIn(List<string> macros, List<string> components) =>
        macros.Count + components.Count == 0 ? Strings.Library_NotUsed : string.Join(Strings.ListSeparator, macros.Concat(components));

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
