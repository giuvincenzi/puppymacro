using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace PuppyMacro.E2E;

public class AppTests
{
    [Fact]
    public void The_development_build_starts_marked_with_the_sample_data()
    {
        using var app = new AppSession();

        Assert.EndsWith(" DEV", app.MainWindow.Title);
        Assert.True(app.Has(app.MainWindow, "Development build"));
        Assert.True(app.Has(app.MainWindow, "Sample: click every second"));
        Assert.True(app.Has(app.MainWindow, "Sample: hold W"));
        Assert.True(app.Has(app.MainWindow, "Sample: chat message"));
    }

    [Fact]
    public void Every_page_opens_from_the_side_rail()
    {
        using var app = new AppSession();

        app.GoTo("Macros");
        Assert.True(app.Has(app.MainWindow, "Sample: type and confirm"));

        app.GoTo("Remap");
        Assert.True(app.Has(app.MainWindow, "Sample: Caps Lock to Esc"));

        app.GoTo("Settings");
        Assert.True(app.Has(app.MainWindow, "Updates are available when PuppyMacro is installed with Setup."));
        Assert.True(app.Has(app.MainWindow, "Not available in the development build"));
    }

    [Fact]
    public void A_new_loop_is_saved_starts_and_stops()
    {
        using var app = new AppSession();

        // Add a loop that presses F24, a key no keyboard has: running it is harmless.
        app.Find(app.MainWindow, "Add loop").AsButton().Click();
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E F24 loop";
        app.Find(editor, "Add key").AsButton().Click(); // starts waiting for the key right away
        app.Find(editor, "Press a key or mouse button (Esc cancels)");
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
        app.Find(editor, "F24");
        app.FindById(editor, "SaveButton").AsButton().Click();

        Assert.True(app.Has(app.MainWindow, "E2E F24 loop"));
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("E2E F24 loop"), AppSession.Timeout).Success);

        // The new loop is the last one: its button is the last "Start" in the list.
        AutomationElement[] startButtons = app.MainWindow.FindAllDescendants(cf => cf.ByName("Start"));
        Button startStop = startButtons[^1].AsButton();
        startStop.Click();
        Assert.True(Retry.WhileFalse(() => startStop.Name == "Stop", AppSession.Timeout).Success);

        app.FindById(app.MainWindow, "StopAllButton").AsButton().Click();
        Assert.True(Retry.WhileFalse(() => startStop.Name == "Start", AppSession.Timeout).Success);
    }

    [Fact]
    public void Starting_PuppyMacro_again_shows_the_running_one()
    {
        using var app = new AppSession();

        // Close to the system tray (on by default). WM_CLOSE, like the title bar's X, which may be
        // off screen on a small screen (the window is 820 px high).
        app.MainWindow.Close();
        Assert.True(Retry.WhileFalse(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);

        using Process second = Process.Start(AppSession.ExePath)!;

        Assert.True(second.WaitForExit((int)AppSession.Timeout.TotalMilliseconds), "the second PuppyMacro did not exit");
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
        Assert.Single(Process.GetProcessesByName("PuppyMacro"));
    }

    [Fact]
    public void Windows_fit_on_a_small_screen()
    {
        // On GitHub the screen is 1024x768: the main window (820 high) and the macro editor
        // (about 790) do not fit unless they are made shorter.
        using var app = new AppSession();
        System.Drawing.Rectangle workArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        AssertInside(workArea, app.MainWindow);

        app.GoTo("Macros");
        app.ItemMenu("Sample: type and confirm", "Edit");
        Window editor = app.Dialog("Edit macro");
        AssertInside(workArea, editor);
        AssertInside(workArea, app.FindById(editor, "SaveButton"));
    }

    [Fact]
    public void Macro_actions_can_be_named_grouped_and_the_group_duplicated()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.ItemMenu("Sample: click and scroll", "Edit");
        Window editor = app.Dialog("Edit macro");
        Assert.True(editor.Patterns.Transform.Pattern.CanResize.Value, "the macro editor cannot be resized");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();

        // Name the click.
        app.Find(editor, "Edit action").AsButton().Invoke();
        Window action = ChildDialog(editor, "Edit click");
        app.FindById(action, "ActionNameBox").AsTextBox().Text = "E2E click";
        app.FindById(action, "SaveButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("1. E2E click")), AppSession.Timeout).Success);

        // Select both actions and group them.
        list.Items[0].Select();
        list.Items[1].AddToSelection();
        app.FindById(editor, "GroupButton").AsButton().Invoke();
        Window group = ChildDialog(editor, "Group actions");
        app.FindById(group, "GroupNameBox").AsTextBox().Text = "E2E group";
        app.FindById(group, "OkButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("Group E2E group")), AppSession.Timeout).Success);

        // Duplicate the group from its header.
        app.Find(editor, "Duplicate group").AsButton().Invoke();
        AutomationElement total = app.FindById(editor, "TotalText");
        Assert.True(Retry.WhileFalse(() => total.Name.StartsWith("4 actions in 2 groups"), AppSession.Timeout).Success, total.Name);

        app.FindById(editor, "SaveButton").AsButton().Invoke();
        string macros = Path.Combine(AppSession.DataFolder, "macros");
        Assert.True(Retry.WhileFalse(() => Directory.GetFiles(macros, "*.json").Any(f =>
        {
            string json = File.ReadAllText(f);
            return json.Split("\"E2E group\"").Length == 3 && json.Contains("\"E2E click\"");
        }), AppSession.Timeout).Success, "the macro file does not have the two groups and the action name");
    }

    /// <summary>A dialog opened by another dialog.</summary>
    private static Window ChildDialog(Window owner, string title) =>
        Retry.WhileNull(() => owner.ModalWindows.FirstOrDefault(w => w.Title == title), AppSession.Timeout, throwOnTimeout: true,
            timeoutMessage: $"window \"{title}\" not found").Result!;

    private static void AssertInside(System.Drawing.Rectangle workArea, AutomationElement element)
    {
        System.Drawing.Rectangle bounds = element.BoundingRectangle;
        Assert.True(workArea.Contains(bounds), $"\"{element.Name}\" {bounds} is not inside the work area {workArea}");
    }

    [Fact]
    public void Game_mode_hides_the_window_and_F11_brings_it_back()
    {
        using var app = new AppSession();

        app.FindById(app.MainWindow, "GameModeButton").AsButton().Click();
        Assert.True(Retry.WhileFalse(() => app.MainWindow.IsOffscreen || !app.MainWindow.IsAvailable, AppSession.Timeout).Success);

        Keyboard.Press(VirtualKeyShort.F11);
        Keyboard.Release(VirtualKeyShort.F11);
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
    }
}
