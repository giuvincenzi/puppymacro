using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>The macro editor's conditions: Add condition, the condition window, the If with its Then in the list, Save. make e2e category=UI</summary>
[Trait("Category", "UI")]
public class ConditionEditorTests
{
    private static Window ChildDialog(Window owner, string title) =>
        Retry.WhileNull(() => owner.ModalWindows.FirstOrDefault(w => AppSession.HasTitle(w.Title, title)), AppSession.Timeout, throwOnTimeout: true,
            timeoutMessage: $"window \"{title}\" not found").Result!;

    [Fact]
    public void A_pixel_condition_is_added_in_its_window_and_shows_as_an_If_with_its_Then()
    {
        MacroDefinition macro = Macro("E2E conditions", null, Press(Vk.F24));
        using var app = new AppSession(seed => seed.Macros.Add(macro));
        app.GoTo("Macros");
        app.EditItem("E2E conditions");
        Window editor = app.Dialog("Edit macro");

        app.FindById(editor, "AddConditionButton").AsButton().Invoke();
        app.MenuItem("Pixel color").AsMenuItem().Invoke(); // UI Automation: a mouse click can miss the menu while its popup opens
        Window condition = ChildDialog(editor, "Add condition");
        // Without a color it cannot be saved yet.
        Assert.False(app.FindById(condition, "SaveButton").IsEnabled);
        TextBox color = Retry.WhileNull(() => condition.FindFirstDescendant(cf => cf.ByName("Color").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the Color box not found").Result!.AsTextBox();
        color.Text = "#112233";
        Assert.True(Retry.WhileFalse(() => app.FindById(condition, "SaveButton").IsEnabled, AppSession.Timeout).Success,
            "Save stays off with a color");
        app.FindById(condition, "SaveButton").AsButton().Invoke();

        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("2. If")), AppSession.Timeout).Success,
            $"no If row. Rows: {string.Join(" | ", list.Items.Select(i => i.Name))}");
        Assert.Contains(list.Items, i => i.Name.StartsWith("Then"));

        // The macro has an If: Checks per second shows.
        app.Find(editor, "Checks per second");

        app.FindById(editor, "SaveButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => AppSession.SavedMacros().Any(m => m.Name == "E2E conditions" && m.Actions.Count == 2), AppSession.Timeout).Success);
        MacroAction saved = AppSession.SavedMacros().First(m => m.Name == "E2E conditions").Actions[1];
        Assert.Equal(MacroActionType.If, saved.Type);
        MacroCondition pixel = Assert.Single(saved.If!.Conditions);
        Assert.Equal("#112233", pixel.Color);
        Assert.Equal(5, pixel.Tolerance);
    }

    [Fact]
    public void Settings_has_Screen_recognition()
    {
        using var app = new AppSession();
        app.GoTo("Settings");
        app.Find(app.MainWindow, "Look for images with");
    }
}
