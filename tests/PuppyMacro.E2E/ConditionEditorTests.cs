using System.Drawing;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
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
    public void An_If_opens_its_condition_window_with_its_conditions_from_the_list()
    {
        MacroAction ifAction = new()
        {
            Type = MacroActionType.If,
            Name = "E2E ready",
            If = new MacroIf
            {
                Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 10, Y = 20, Color = "#3BD16F", Tolerance = 5 } },
                Wait = ConditionWait.UpTo,
                WaitSeconds = 30,
                Then = { Press(Vk.F24) },
            },
        };
        using var app = new AppSession(seed => seed.Macros.Add(Macro("E2E edit if", null, ifAction)));
        app.GoTo("Macros");
        app.EditItem("E2E edit if");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("1. E2E ready")), AppSession.Timeout).Success,
            $"no If row. Rows: {string.Join(" | ", list.Items.Select(i => i.Name))}");
        list.Items.First(i => i.Name.StartsWith("1. E2E ready")).Select();

        app.EditorCommand(editor, "EditActionButton").AsButton().Invoke();
        Window condition = ChildDialog(editor, "Edit condition");
        TextBox color = Retry.WhileNull(() => condition.FindFirstDescendant(cf => cf.ByName("Color").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the Color box not found").Result!.AsTextBox();
        Assert.Equal("#3BD16F", color.Text);
        app.FindById(condition, "CancelButton").AsButton().Invoke();
    }

    /// <summary>An If with a pixel condition in a macro of its own, its condition window open.</summary>
    private static (AppSession App, Window Editor, Window Condition) OpenPixelIf(string macroName)
    {
        MacroAction ifAction = new()
        {
            Type = MacroActionType.If,
            Name = "E2E check",
            If = new MacroIf { Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 10, Y = 20, Color = "#3BD16F", Tolerance = 5 } } },
        };
        var app = new AppSession(seed => seed.Macros.Add(Macro(macroName, null, ifAction)));
        app.GoTo("Macros");
        app.EditItem(macroName);
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("1. E2E check")), AppSession.Timeout).Success);
        list.Items.First(i => i.Name.StartsWith("1. E2E check")).Select();
        app.EditorCommand(editor, "EditActionButton").AsButton().Invoke();
        return (app, editor, ChildDialog(editor, "Edit condition"));
    }

    [Fact]
    public void An_opened_condition_is_not_checked_until_Check_again()
    {
        var (app, _, condition) = OpenPixelIf("E2E check again");
        using (app)
        {
            app.Find(condition, "Not checked yet. Check it on the screen with the button.");

            app.FindButton(condition, "Check again").Invoke();

            Assert.True(Retry.WhileFalse(() => condition.FindFirstDescendant(cf => cf.ByName("Not checked yet. Check it on the screen with the button.")) == null
                    && condition.FindFirstDescendant(cf => cf.ByText("The pixel doesn't match now.", FlaUI.Core.Definitions.PropertyConditionFlags.MatchSubstring)) != null,
                AppSession.Timeout).Success, "Check again did not check the pixel");
            app.FindById(condition, "CancelButton").AsButton().Invoke();
        }
    }

    [Fact]
    public void A_condition_can_be_true_when_it_is_not_there()
    {
        var (app, editor, condition) = OpenPixelIf("E2E not there");
        using (app)
        {
            ComboBox trueWhen = Retry.WhileNull(() => condition.FindFirstDescendant(cf => cf.ByName("True when").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox))),
                AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the True when list not found").Result!.AsComboBox();
            trueWhen.Expand();
            trueWhen.Select("The pixel doesn't have this color");
            trueWhen.Collapse();
            app.Find(condition, "10, 20 isn't #3BD16F, with 5% tolerance");
            app.FindById(condition, "SaveButton").AsButton().Invoke();

            app.FindById(editor, "SaveButton").AsButton().Invoke();
            Assert.True(Retry.WhileFalse(() => AppSession.SavedMacros().Any(m => m.Name == "E2E not there" && m.Actions[0].If!.Conditions[0].Not),
                AppSession.Timeout).Success, "the condition was not saved with Not");
        }
    }

    [Fact]
    public void An_image_is_taken_on_the_screen_after_a_Retry()
    {
        using var app = new AppSession(seed => seed.Macros.Add(Macro("E2E take image", null, Press(Vk.F24))));
        app.GoTo("Macros");
        app.EditItem("E2E take image");
        Window editor = app.Dialog("Edit macro");
        app.FindById(editor, "AddConditionButton").AsButton().Invoke();
        app.MenuItem("Image on screen").AsMenuItem().Invoke();
        Window condition = ChildDialog(editor, "Add condition");

        app.Find(condition, "Take on screen").AsButton().Invoke();
        AutomationElement picker = app.TopWindow("Take the image") ?? throw new Xunit.Sdk.XunitException("the picker did not open");
        Drag(new Point(200, 200), new Point(300, 260));
        app.FindButton(picker, "Retry").Invoke();
        Assert.True(Retry.WhileFalse(() => picker.FindFirstDescendant(cf => cf.ByName("Take image"))?.IsOffscreen != false, AppSession.Timeout).Success,
            "Retry left the rectangle's buttons");
        Drag(new Point(220, 220), new Point(320, 280));
        app.FindButton(picker, "Take image").Invoke();

        Assert.True(Retry.WhileNull(() => condition.FindFirstDescendant(cf => cf.ByText("pixels, taken at", FlaUI.Core.Definitions.PropertyConditionFlags.MatchSubstring)),
            AppSession.Timeout).Success, "the image is not in the condition");
        app.Find(condition, "Take another image");
    }

    /// <summary>Drags with the left button, with moves the window sees (SendInput, not only the cursor's place).</summary>
    private static void Drag(Point from, Point to)
    {
        Native.MoveCursor(from.X, from.Y);
        Thread.Sleep(100);
        Mouse.Down(MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            Native.MoveCursor(from.X + (to.X - from.X) * step / 10, from.Y + (to.Y - from.Y) * step / 10);
            Thread.Sleep(20);
        }
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(200);
    }

    [Fact]
    public void Settings_has_Screen_recognition()
    {
        using var app = new AppSession();
        app.GoTo("Settings");
        app.Find(app.MainWindow, "Look for images with");
    }

    [Fact]
    public void An_action_that_goes_where_an_If_found_something_has_no_Test()
    {
        var ready = new MacroCondition { Kind = ConditionKind.Text, Name = "Ready", Text = "Ready", AreaX = 0, AreaY = 0, AreaWidth = 300, AreaHeight = 100 };
        MacroAction ifAction = new()
        {
            Type = MacroActionType.If,
            Name = "E2E find",
            If = new MacroIf
            {
                Conditions = { ready },
                Then = { new MacroAction { Type = MacroActionType.Click, Vk = Vk.LButton, Name = "E2E click it", FoundId = ready.Id } },
            },
        };
        MacroAction press = Press(Vk.F24);
        press.Name = "E2E press";
        using var app = new AppSession(seed => seed.Macros.Add(Macro("E2E no test", null, ifAction, press)));
        app.GoTo("Macros");
        app.EditItem("E2E no test");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.Contains("E2E click it")), AppSession.Timeout).Success,
            $"no click row. Rows: {string.Join(" | ", list.Items.Select(i => i.Name))}");

        // A plain action has Test.
        list.Items.First(i => i.Name.Contains("E2E press")).Select();
        Assert.NotNull(app.EditorCommand(editor, "TestActionButton"));

        // The click where the text was found: no Test on the bar nor in ⋯, nor in its window.
        list.Items.First(i => i.Name.Contains("E2E click it")).Select();
        Assert.True(Retry.WhileFalse(() => editor.FindFirstDescendant(cf => cf.ByAutomationId("TestActionButton")) == null,
            AppSession.Timeout).Success, "Test is shown for an action that goes where the If found something");
        app.EditorCommand(editor, "EditActionButton").AsButton().Invoke();
        Window action = ChildDialog(editor, "Edit click");
        app.FindById(action, "SaveButton");
        Assert.Null(action.FindFirstDescendant(cf => cf.ByAutomationId("TestButton")));
        app.FindById(action, "CancelButton").AsButton().Invoke();
    }
}
