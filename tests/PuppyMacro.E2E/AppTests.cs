using System;
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
        // (1180 x 820) do not fit unless they are made smaller.
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

    [Fact]
    public void An_action_is_tested_from_its_window_and_from_its_row()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.ItemMenu("Sample: click and scroll", "Edit");
        Window editor = app.Dialog("Edit macro");
        System.Drawing.Rectangle editorPlace = editor.BoundingRectangle;

        // A Press key action with F24 (harmless), held 1.5 s so the test lasts long enough to be seen.
        app.FindById(editor, "AddActionButton").AsButton().Invoke();
        Retry.WhileNull(() => app.Automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem).And(cf.ByName("Press key"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "menu item \"Press key\" not found").Result!
            .AsMenuItem().Invoke(); // UI Automation: a mouse click can miss the menu while its popup opens
        Window action = ChildDialog(editor, "Add press key");
        app.FindById(action, "KeyButton").AsButton().Invoke();
        app.Find(action, "Press a key (Esc cancels)");
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
        app.Find(action, "F24");
        AutomationElement hold = app.FindById(action, "KeyHoldBox");
        hold.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("1500");

        // Test from the action window, before saving: PuppyMacro moves out of the way, then comes back.
        System.Drawing.Rectangle actionPlace = action.BoundingRectangle;
        app.FindById(action, "TestButton").AsButton().Invoke();
        AssertMovesAwayAndBack(app, action, actionPlace);
        AssertMovesAwayAndBack(app, editor, editorPlace, alreadyAway: true);

        app.FindById(action, "SaveButton").AsButton().Invoke();
        ListBox list = app.FindById(editor, "ActionList").AsListBox();
        Assert.True(Retry.WhileFalse(() => list.Items.Any(i => i.Name.StartsWith("3. Press F24")), AppSession.Timeout).Success);

        // Test from the row: the editor moves out of the way, then comes back.
        editor.FindAllDescendants(cf => cf.ByName("Test action")).Last().AsButton().Invoke();
        AssertMovesAwayAndBack(app, editor, editorPlace);
    }

    /// <summary>
    /// The window goes off screen while a test plays (the main window is hidden), then returns to
    /// <paramref name="place"/> with the main window shown again.
    /// </summary>
    private static void AssertMovesAwayAndBack(AppSession app, Window window, System.Drawing.Rectangle place, bool alreadyAway = false)
    {
        if (!alreadyAway)
        {
            Assert.True(Retry.WhileFalse(() => window.BoundingRectangle.Right <= System.Windows.Forms.SystemInformation.VirtualScreen.Left,
                AppSession.Timeout, TimeSpan.FromMilliseconds(20)).Success, $"\"{window.Title}\" did not move out of the way");
        }
        Assert.True(Retry.WhileFalse(() => window.BoundingRectangle == place && app.MainWindow.IsAvailable && !app.MainWindow.IsOffscreen,
            AppSession.Timeout).Success, $"\"{window.Title}\" is at {window.BoundingRectangle}, not back at {place}");
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

    /// <summary>
    /// Sets the Overlay mode hotkey to F24 (no keyboard has it) in Settings > Hotkeys. The default,
    /// Alt+Shift+W, cannot be pressed by a test: PuppyMacro reads Ctrl, Alt, Shift and Win only from
    /// real key presses, never from simulated ones.
    /// </summary>
    private static void SetOverlayModeHotkeyToF24(AppSession app)
    {
        app.GoTo("Settings");
        app.FindById(app.MainWindow, "HotkeysGroup").Patterns.ExpandCollapse.Pattern.Expand();
        app.FindById(app.MainWindow, "OverlayModeChangeButton").AsButton().Invoke();
        app.Find(app.MainWindow, "Press a key…");
        PressF24();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Replace(" ", "").Replace("\r", "").Replace("\n", "")
            .Contains("\"OverlayModeHotkey\":{\"Vk\":135,"), AppSession.Timeout).Success, "the Overlay mode hotkey is not F24");
    }

    private static void PressF24()
    {
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
    }

    /// <summary>A loop that presses F24 (harmless), with its floating button on. Returns its editor still open.</summary>
    private static Window AddF24LoopWithButton(AppSession app)
    {
        app.Find(app.MainWindow, "Add loop").AsButton().Click();
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E F24 loop";
        app.Find(editor, "Add key").AsButton().Click();
        app.Find(editor, "Press a key or mouse button (Esc cancels)");
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
        app.Find(editor, "F24");
        app.FindById(editor, "FloatingSwitch").AsToggleButton().Toggle(); // UI Automation: no click, works off screen
        return editor;
    }

    [Fact]
    public void A_floating_button_is_saved_with_its_opacity_and_shown_in_overlay_mode_instead_of_its_panel_row()
    {
        using var app = new AppSession();

        // The button is never clicked.
        Window editor = AddF24LoopWithButton(app);
        Assert.Equal("EF", app.FindById(editor, "LabelBox").AsTextBox().Text);
        app.FindById(editor, "OpacitySlider").Patterns.RangeValue.Pattern.SetValue(60);
        app.FindById(editor, "SaveButton").AsButton().Click();

        Assert.True(Retry.WhileFalse(() =>
        {
            string json = File.ReadAllText(AppSession.SettingsFile);
            return json.Contains("\"Label\": \"EF\"") && json.Contains("\"Opacity\": 60");
        }, AppSession.Timeout).Success);

        SetOverlayModeHotkeyToF24(app);
        app.FindById(app.MainWindow, "OverlayModeButton").AsButton().Click();
        AutomationElement? button = app.TopWindow("PuppyMacro floating button: E2E F24 loop");
        Assert.NotNull(button);
        Assert.True(app.Has(button!, "EF"));

        // The samples are disabled and the new loop is on its button: the panel lists nothing.
        AutomationElement? panel = app.TopWindow("PuppyMacro overlay panel");
        Assert.NotNull(panel);
        Assert.True(app.Has(panel!, "Everything is on floating buttons."));
        Assert.False(app.Has(panel!, "E2E F24 loop"));

        PressF24(); // the Overlay mode hotkey
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
        Assert.True(Retry.WhileFalse(() => button!.IsOffscreen || !button.IsAvailable, AppSession.Timeout).Success);
    }

    [Fact]
    public void With_the_overlay_panel_off_overlay_mode_shows_only_the_floating_buttons()
    {
        using var app = new AppSession();
        Window editor = AddF24LoopWithButton(app);
        app.FindById(editor, "SaveButton").AsButton().Click();

        SetOverlayModeHotkeyToF24(app);
        app.FindById(app.MainWindow, "OverlayGroup").Patterns.ExpandCollapse.Pattern.Expand();
        app.FindById(app.MainWindow, "ShowPanelSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"ShowOverlayPanel\": false"),
            AppSession.Timeout).Success);
        Assert.False(app.FindById(app.MainWindow, "OpacitySlider").IsEnabled); // the panel's options are off with it

        app.FindById(app.MainWindow, "OverlayModeButton").AsButton().Click();
        Assert.NotNull(app.TopWindow("PuppyMacro floating button: E2E F24 loop"));
        AutomationElement? panel = app.TopWindow("PuppyMacro overlay panel");
        Assert.True(panel == null || panel.IsOffscreen, "the overlay panel is shown although it is off");

        PressF24(); // the Overlay mode hotkey
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
    }

    [Fact]
    public void Overlay_mode_hides_the_window_and_its_hotkey_brings_it_back()
    {
        using var app = new AppSession();
        SetOverlayModeHotkeyToF24(app);

        app.FindById(app.MainWindow, "OverlayModeButton").AsButton().Click();
        Assert.True(Retry.WhileFalse(() => app.MainWindow.IsOffscreen || !app.MainWindow.IsAvailable, AppSession.Timeout).Success);

        PressF24(); // the Overlay mode hotkey
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
    }

    [Fact]
    public void The_macro_code_view_shows_the_file_checks_it_and_goes_back_to_the_list()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.ItemMenu("Sample: type and confirm", "Edit");
        Window editor = app.Dialog("Edit macro");
        Assert.False(app.Has(editor, "Format"), "Format shows in the List view");

        app.FindById(editor, "CodeViewButton").Patterns.SelectionItem.Pattern.Select();

        // The code editor loaded the macro's JSON and found nothing wrong.
        Assert.True(Retry.WhileNull(() => editor.FindFirstDescendant(cf => cf.ByName("No problems.")), TimeSpan.FromSeconds(30)).Success,
            "the Code view did not report on the code");
        AutomationElement format = app.FindById(editor, "FormatButton");
        Assert.True(app.FindById(editor, "SaveButton").IsEnabled);
        Assert.True(app.FindById(editor, "ListViewButton").IsEnabled);
        format.AsButton().Invoke();

        // Back to the list: the same three actions.
        app.FindById(editor, "ListViewButton").Patterns.SelectionItem.Pattern.Select();
        AutomationElement total = app.FindById(editor, "TotalText");
        Assert.True(Retry.WhileFalse(() => !total.IsOffscreen && total.Name.StartsWith("3 actions in 1 group"), AppSession.Timeout).Success, total.Name);

        // Saved from the Code view, the file keeps the macro.
        app.FindById(editor, "CodeViewButton").Patterns.SelectionItem.Pattern.Select();
        Assert.True(Retry.WhileNull(() => editor.FindFirstDescendant(cf => cf.ByName("No problems.")), TimeSpan.FromSeconds(30)).Success);
        app.FindById(editor, "SaveButton").AsButton().Invoke();
        string macros = Path.Combine(AppSession.DataFolder, "macros");
        Assert.True(Retry.WhileFalse(() => Directory.GetFiles(macros, "*.json").Any(f =>
            {
                string json = File.ReadAllText(f);
                return json.Contains("\"Sample: type and confirm\"") && json.Contains("\"Send\"") && json.Contains("\"Type hi\"");
            }), AppSession.Timeout).Success);
    }
}
