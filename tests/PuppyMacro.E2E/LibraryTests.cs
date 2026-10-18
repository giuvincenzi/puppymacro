using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// The Library with the development build's samples (the collection "Sample: Notepad", its component "Save the file", and
/// the macro "Sample: save with a component" that links it): a linked row is one line and opens its component read only;
/// Add component links one from the Library; the Library window lists them and refuses to delete a linked component. And
/// components as functions, with the samples "Type a word" (its parameter Word) and "Sample: type with a parameter": a row
/// shows its values and its window changes them; Edit in the Library opens the component in the macro editor with its
/// parameters and the parameter window.
/// </summary>
[Trait("Category", "UI")]
public class LibraryTests
{
    private static Window ChildDialog(Window owner, string title) =>
        Retry.WhileNull(() => owner.ModalWindows.FirstOrDefault(w => AppSession.HasTitle(w.Title, title)), AppSession.Timeout, throwOnTimeout: true,
            timeoutMessage: $"window \"{title}\" not found").Result!;

    [Fact]
    public void A_linked_row_is_one_line_and_opens_its_component_read_only()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: save with a component");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("1. Save the file") && i.Name.Contains("Sample: Notepad")),
            AppSession.Timeout).Success, string.Join(" | ", list.Items.Select(i => i.Name)));

        list.Items[0].Select();
        app.Find(editor, "Edit action").AsButton().Invoke();
        Window component = ChildDialog(editor, "Component - Save the file");
        Assert.True(app.Has(component, "Sample: Notepad"));
        Assert.True(app.Has(component, "office-notepad-act-save_file"));
        Assert.True(app.Has(component, "Unlink this row"));
        component.Close();
    }

    [Fact]
    public void Add_component_links_one_from_the_Library()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: type and confirm");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        int before = list.Items.Length;

        app.FindById(editor, "AddComponentButton").AsButton().Invoke();
        Window picker = ChildDialog(editor, "Add component");
        var check = Retry.WhileNull(() => picker.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName("Save the file"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the component's check box is not there").Result!.AsCheckBox();
        check.Toggle();
        app.FindById(picker, "AddLinkedButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => list.Items.Length == before + 1 && list.Items.Any(i => i.Name.Contains("Save the file")),
            AppSession.Timeout).Success, string.Join(" | ", list.Items.Select(i => i.Name)));
    }

    [Fact]
    public void A_linked_row_shows_its_values_and_its_window_changes_them()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: type with a parameter");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.Contains("Type a word") && i.Name.Contains("Word hello")),
            AppSession.Timeout).Success, string.Join(" | ", list.Items.Select(i => i.Name)));

        list.Items[0].Select();
        app.Find(editor, "Edit action").AsButton().Invoke();
        Window component = ChildDialog(editor, "Component - Type a word");
        Assert.True(app.Has(component, "Arguments"));
        var value = Retry.WhileNull(() => component.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("Value"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the argument's value is not there").Result!.AsTextBox();
        value.Text = "bye";
        app.FindById(component, "SaveButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.Contains("Word bye")), AppSession.Timeout).Success,
            string.Join(" | ", list.Items.Select(i => i.Name)));
    }

    [Fact]
    public void Edit_in_the_Library_opens_the_component_with_its_parameters()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.FindById(app.MainWindow, "LibraryButton").AsButton().Invoke();
        Window library = app.Dialog("Library");
        ListBox collections = app.FindById(library, "CollectionList").AsListBox();
        Retry.WhileNull(() => collections.Items.FirstOrDefault(i => i.Name == "Sample: typing"), AppSession.Timeout, throwOnTimeout: true,
            timeoutMessage: "the collection Sample: typing is not there").Result!.Select();
        ListBox components = app.FindById(library, "ComponentList").AsListBox();
        Assert.True(Retry.WhileFalse(() => components.Items.Length == 1 && components.Items[0].Name.Contains("Type a word"), AppSession.Timeout).Success);
        components.Items[0].Select();
        var edit = Retry.WhileNull(() => components.Items[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Edit"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "Edit is not shown").Result!.AsButton();
        edit.Invoke();

        Window editor = ChildDialog(library, "Edit component - Type a word");
        Assert.True(app.Has(editor, "Parameters"));
        Assert.True(app.Has(editor, "Word"));
        Assert.True(app.Has(editor, "Can be used as a condition"));
        app.FindById(editor, "AddParameterButton").AsButton().Invoke();
        Window parameter = ChildDialog(editor, "Add parameter");
        Assert.True(app.Has(parameter, "Every call gives a value"));
        parameter.Close();
        app.FindById(editor, "CancelButton").AsButton().Invoke();
        library.Close();
    }

    [Fact]
    public void A_Grid_parameter_s_example_opens_on_its_cells()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.FindById(app.MainWindow, "LibraryButton").AsButton().Invoke();
        Window library = app.Dialog("Library");
        ListBox collections = app.FindById(library, "CollectionList").AsListBox();
        Retry.WhileNull(() => collections.Items.FirstOrDefault(i => i.Name == "Sample: typing"), AppSession.Timeout, throwOnTimeout: true,
            timeoutMessage: "the collection Sample: typing is not there").Result!.Select();
        ListBox components = app.FindById(library, "ComponentList").AsListBox();
        Assert.True(Retry.WhileFalse(() => components.Items.Length == 1, AppSession.Timeout).Success);
        components.Items[0].Select();
        Retry.WhileNull(() => components.Items[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Edit"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "Edit is not shown").Result!.AsButton().Invoke();
        Window editor = ChildDialog(library, "Edit component - Type a word");
        app.FindById(editor, "AddParameterButton").AsButton().Invoke();
        Window parameter = ChildDialog(editor, "Add parameter");

        // Grid in the Type list: the Example becomes a card that opens on the grid's fields.
        var type = app.FindById(parameter, "TypeBox").AsComboBox();
        type.Expand();
        Assert.Contains("Grid", type.Items.Select(i => i.Name));
        type.Select("Grid");
        type.Collapse();
        Assert.True(app.Has(parameter, "Example"));
        var example = app.FindById(parameter, "ExampleGrid");
        var expander = Retry.WhileNull(() => new[] { example }.Concat(example.FindAllDescendants()).FirstOrDefault(e => e.Patterns.ExpandCollapse.IsSupported),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the grid's card does not open").Result!;
        expander.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.True(app.Has(parameter, "First cell"));
        Assert.True(app.Has(parameter, "Last cell"));
        Assert.True(app.Has(parameter, "Columns"));
        Assert.True(app.Has(parameter, "Rows"));
        Assert.True(app.Has(parameter, "1 cell, counted row by row from the top left, starting at 1"));
        Assert.True(app.Has(parameter, "Show the cells"));

        parameter.Close();
        app.FindById(editor, "CancelButton").AsButton().Invoke();
        library.Close();
    }

    [Fact]
    public void The_Library_lists_its_collections_and_refuses_to_delete_a_linked_component()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.FindById(app.MainWindow, "LibraryButton").AsButton().Invoke();
        Window library = app.Dialog("Library");
        Assert.True(app.Has(library, "Sample: Notepad"));

        ListBox components = app.FindById(library, "ComponentList").AsListBox();
        Assert.True(Retry.WhileFalse(() => components.Items.Length == 1, AppSession.Timeout).Success);
        components.Items[0].Select();
        var delete = Retry.WhileNull(() => components.Items[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Delete"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "Delete is not shown").Result!.AsButton();
        delete.Invoke();

        AutomationElement problem = app.FindById(library, "ProblemBar");
        Assert.True(Retry.WhileFalse(() => problem.Name.Contains("Sample: save with a component"), AppSession.Timeout).Success,
            $"the Library does not say which macro links the component: \"{problem.Name}\"");
        Assert.Equal(1, components.Items.Length);
        library.Close();
    }
}
