using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// The Library with the development build's samples (the collection "Sample: Notepad", its component "Save the file", and
/// the macro "Sample: save with a component" that links it): a linked row is one line and opens its component read only;
/// Add component links one from the Library; the Library window lists them and refuses to delete a linked component.
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

        Assert.True(Retry.WhileFalse(() => app.Has(library, "Sample: save with a component"), AppSession.Timeout).Success,
            "the Library does not say which macro links the component");
        Assert.Equal(1, components.Items.Length);
        library.Close();
    }
}
