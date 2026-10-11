using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>The windows, pages and editors. make e2e CATEGORY=UI</summary>
[Trait("Category", "UI")]
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

        // About opens like the other groups. Its buttons open the browser: never clicked.
        app.Expand("AboutGroup");
        Assert.True(app.FindById(app.MainWindow, "UserGuideButton").IsEnabled);
        Assert.True(app.FindById(app.MainWindow, "GitHubButton").IsEnabled);
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

        // The card's Play: running, it becomes Stop; Stop all stops it.
        Button startStop = app.CardButton("E2E F24 loop", "Play").AsButton();
        startStop.Invoke();
        Assert.True(Retry.WhileFalse(() => startStop.Name == "Stop", AppSession.Timeout).Success);

        app.FindById(app.MainWindow, "StopAllButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => startStop.Name == "Play", AppSession.Timeout).Success);
    }

    [Fact]
    public void The_main_window_hides_while_an_editor_is_open()
    {
        using var app = new AppSession();

        app.Find(app.MainWindow, "Add loop").AsButton().Invoke();
        Window editor = app.Dialog("Add loop");
        Assert.True(Retry.WhileFalse(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window is still shown with an editor open");

        app.FindById(editor, "CancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success,
            "the main window did not come back");
    }

    [Fact]
    public void Starting_PuppyMacro_again_shows_the_running_one()
    {
        using var app = new AppSession();

        // Close to the system tray (on by default). WM_CLOSE, like the title bar's X, which may be
        // off screen on a small screen. The window's size is saved.
        app.MainWindow.Close();
        Assert.True(Retry.WhileFalse(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
        Assert.True(Retry.WhileFalse(() => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(AppSession.SettingsFile), "\"WindowWidth\": [0-9]"),
            AppSession.Timeout).Success, "the window size was not saved");

        using Process second = Process.Start(AppSession.ExePath)!;

        Assert.True(second.WaitForExit((int)AppSession.Timeout.TotalMilliseconds), "the second PuppyMacro did not exit");
        Assert.True(Retry.WhileTrue(() => !app.MainWindow.IsAvailable || app.MainWindow.IsOffscreen, AppSession.Timeout).Success);
        Assert.Single(Process.GetProcessesByName("PuppyMacro"));
    }

    [Fact]
    public void Windows_fit_on_a_small_screen()
    {
        // On GitHub the screen is 1024x768: the main window (800 x 900) and the macro editor
        // (1180 x 820) do not fit unless they are made smaller.
        using var app = new AppSession();
        System.Drawing.Rectangle workArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

        AssertInside(workArea, app.MainWindow);

        app.GoTo("Macros");
        app.EditItem("Sample: type and confirm");
        Window editor = app.Dialog("Edit macro");
        AssertInside(workArea, editor);
        AssertInside(workArea, app.FindById(editor, "SaveButton"));
    }

    [Fact]
    public void Macro_actions_can_be_named_grouped_and_the_group_duplicated()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: click and scroll");
        Window editor = app.Dialog("Edit macro");
        Assert.True(editor.Patterns.Transform.Pattern.CanResize.Value, "the macro editor cannot be resized");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();

        // Name the click: the commands act on the selected actions.
        list.Items[0].Select();
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

        // Duplicate the group: select its row, then Duplicate.
        list.Items.First(i => i.Name.StartsWith("Group E2E group")).Select();
        app.FindById(editor, "DuplicateButton").AsButton().Invoke();
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
        app.EditItem("Sample: click and scroll");
        Window editor = app.Dialog("Edit macro");
        System.Drawing.Rectangle editorPlace = editor.BoundingRectangle;

        // A Press key action with F24 (harmless), held 1.5 s so the test lasts long enough to be seen.
        app.FindById(editor, "AddActionButton").AsButton().Invoke();
        Retry.WhileNull(() => app.Automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem).And(cf.ByName("Press key"))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "menu item \"Press key\" not found").Result!
            .AsMenuItem().Invoke(); // UI Automation: a mouse click can miss the menu while its popup opens
        Window action = ChildDialog(editor, "Add press key");
        app.Find(action, "Set key").AsButton().Invoke();
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

        // Test from the list: select the action, then Test; the editor moves out of the way, then comes back.
        list.Items.First(i => i.Name.StartsWith("3. Press F24")).Select();
        app.Find(editor, "Test action").AsButton().Invoke();
        AssertMovesAwayAndBack(app, editor, editorPlace);
    }

    /// <summary>
    /// The window goes off screen while a test plays, then returns to <paramref name="place"/>. The main
    /// window stays hidden: it is, while an editor is open.
    /// </summary>
    private static void AssertMovesAwayAndBack(AppSession app, Window window, System.Drawing.Rectangle place, bool alreadyAway = false)
    {
        if (!alreadyAway)
        {
            Assert.True(Retry.WhileFalse(() => window.BoundingRectangle.Right <= System.Windows.Forms.SystemInformation.VirtualScreen.Left,
                AppSession.Timeout, TimeSpan.FromMilliseconds(20)).Success, $"\"{window.Title}\" did not move out of the way");
        }
        Assert.True(Retry.WhileFalse(() => window.BoundingRectangle == place, AppSession.Timeout).Success,
            $"\"{window.Title}\" is at {window.BoundingRectangle}, not back at {place}");
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
        app.Expand("HotkeysGroup");
        // The Overlay mode card's key field: its keys are a button that changes them.
        app.Find(app.FindById(app.MainWindow, "OverlayModeHotkeyField"), "Change hotkey").AsButton().Invoke();
        app.Find(app.MainWindow, "Press the hotkey (Esc cancels)");
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

        // The new loop is on its button; the panel lists the samples, shown as disabled.
        AutomationElement? panel = app.TopWindow("PuppyMacro overlay panel");
        Assert.NotNull(panel);
        Assert.True(app.Has(panel!, "Sample: click every second"));
        Assert.True(app.Has(panel!, "Disabled"));
        Assert.False(app.Has(panel!, "E2E F24 loop"));
        Assert.True(app.Has(panel!, "Stop all"));

        PressF24(); // the Overlay mode hotkey
        app.AssertLeftOverlayModeInTray(button!);
    }

    [Fact]
    public void Show_in_overlay_is_on_by_default_and_off_it_turns_off_the_floating_button_and_is_saved()
    {
        using var app = new AppSession();
        Window editor = AddF24LoopWithButton(app);
        var show = app.FindById(editor, "ShowSwitch").AsToggleButton();
        Assert.Equal(ToggleState.On, show.ToggleState);
        Assert.True(app.FindById(editor, "FloatingSwitch").IsEnabled);

        show.Toggle(); // UI Automation: no click, works off screen
        Assert.True(Retry.WhileTrue(() => app.FindById(editor, "FloatingSwitch").IsEnabled, AppSession.Timeout).Success,
            "the floating button can still be turned on");
        app.FindById(editor, "SaveButton").AsButton().Click();

        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"ShowInOverlay\": false"),
            AppSession.Timeout).Success, "Show in overlay off was not saved");
    }

    [Fact]
    public void With_the_overlay_panel_off_overlay_mode_shows_only_the_floating_buttons()
    {
        using var app = new AppSession();
        Window editor = AddF24LoopWithButton(app);
        app.FindById(editor, "SaveButton").AsButton().Click();

        SetOverlayModeHotkeyToF24(app);
        app.Expand("OverlayGroup");
        app.FindById(app.MainWindow, "ShowPanelSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"ShowOverlayPanel\": false"),
            AppSession.Timeout).Success);
        Assert.False(app.FindById(app.MainWindow, "OpacitySlider").IsEnabled); // the panel's options are off with it

        app.FindById(app.MainWindow, "OverlayModeButton").AsButton().Click();
        AutomationElement? button = app.TopWindow("PuppyMacro floating button: E2E F24 loop");
        Assert.NotNull(button);
        AutomationElement? panel = app.TopWindow("PuppyMacro overlay panel");
        Assert.True(panel == null || panel.IsOffscreen, "the overlay panel is shown although it is off");

        PressF24(); // the Overlay mode hotkey
        app.AssertLeftOverlayModeInTray(button!);
    }

    [Fact]
    public void Overlay_mode_hides_the_window_and_its_hotkey_leaves_it_in_the_tray()
    {
        using var app = new AppSession();
        SetOverlayModeHotkeyToF24(app);

        app.FindById(app.MainWindow, "OverlayModeButton").AsButton().Click();
        Assert.True(Retry.WhileFalse(() => app.MainWindow.IsOffscreen || !app.MainWindow.IsAvailable, AppSession.Timeout).Success);
        AutomationElement? panel = app.TopWindow("PuppyMacro overlay panel");
        Assert.NotNull(panel);

        PressF24(); // the Overlay mode hotkey
        app.AssertLeftOverlayModeInTray(panel!);
    }

    [Fact]
    public void The_macro_code_view_shows_the_file_checks_it_and_goes_back_to_the_list()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: type and confirm");
        Window editor = app.Dialog("Edit macro");
        Assert.False(app.Has(editor, "Format"), "Format shows in the List view");

        app.FindById(editor, "CodeViewButton").Patterns.SelectionItem.Pattern.Select();

        // The code editor loaded the macro's JSON and found nothing wrong.
        Assert.True(Retry.WhileNull(() => editor.FindFirstDescendant(cf => cf.ByName("No problems.")), TimeSpan.FromSeconds(30)).Success,
            "the Code view did not report on the code");
        AutomationElement format = app.FindById(editor, "FormatButton");
        Assert.True(app.FindById(editor, "SaveButton").IsEnabled);
        Assert.True(app.FindById(editor, "FormViewButton").IsEnabled);
        format.AsButton().Invoke();

        // Back to the list: the same three actions.
        app.FindById(editor, "FormViewButton").Patterns.SelectionItem.Pattern.Select();
        AutomationElement total = app.FindById(editor, "TotalText");
        Assert.True(Retry.WhileFalse(() => !total.IsOffscreen && total.Name.StartsWith("3 actions in 1 group"), AppSession.Timeout).Success, total.Name);

        // Discard changes (Cancel in the Code view): back to the list, the editor stays open.
        app.FindById(editor, "CodeViewButton").Patterns.SelectionItem.Pattern.Select();
        AutomationElement discard = app.FindById(editor, "CancelButton");
        Assert.True(Retry.WhileFalse(() => discard.Name == "Discard changes", AppSession.Timeout).Success, discard.Name);
        discard.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => app.FindById(editor, "CancelButton").Name == "Cancel", AppSession.Timeout).Success);
        Assert.StartsWith("3 actions in 1 group", app.FindById(editor, "TotalText").Name);

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

    /// <summary>
    /// Opens the Code view of an editor window and waits until the code editor checked the code:
    /// "No problems." when <paramref name="problem"/> is null, otherwise that problem in the list.
    /// </summary>
    private static void OpenCodeView(AppSession app, Window window, string? problem = null)
    {
        app.FindById(window, "CodeViewButton").Patterns.SelectionItem.Pattern.Select();
        // A problem is a row (ARIA option) named "message Ln x, Col y": its text is not exposed on its own.
        // The page redraws the Problems list while the code is checked: a row found a moment ago can be gone
        // when its name is read (COMException), so an exception is one more try, not a failure.
        bool shown = problem == null
            ? Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName("No problems.")), TimeSpan.FromSeconds(30),
                ignoreException: true).Success
            : Retry.WhileFalse(() => window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                .Any(row => row.Name.StartsWith(problem)), TimeSpan.FromSeconds(30), ignoreException: true).Success;
        Assert.True(shown, $"the Code view did not show \"{problem ?? "No problems."}\"");
    }

    /// <summary>Discard changes: back to the Form view, with Cancel again.</summary>
    private static void DiscardCodeChanges(AppSession app, Window window)
    {
        AutomationElement discard = app.FindById(window, "CancelButton");
        Assert.True(Retry.WhileFalse(() => discard.Name == "Discard changes", AppSession.Timeout).Success, discard.Name);
        discard.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => app.FindById(window, "CancelButton").Name == "Cancel", AppSession.Timeout).Success);
    }

    [Fact]
    public void A_loop_and_a_remap_have_a_code_view_in_the_same_window()
    {
        using var app = new AppSession();

        // A new loop: the code shows what is missing, Form view stays off, Discard changes goes back.
        // The window keeps its place and size in both views.
        app.Find(app.MainWindow, "Add loop").AsButton().Click();
        Window loop = app.Dialog("Add loop");
        System.Drawing.Rectangle formPlace = loop.BoundingRectangle;
        OpenCodeView(app, loop, "Name: Enter a name.");
        Assert.Equal(formPlace, loop.BoundingRectangle);
        Assert.False(app.FindById(loop, "FormViewButton").IsEnabled);
        Assert.False(app.FindById(loop, "SaveButton").IsEnabled);
        DiscardCodeChanges(app, loop);
        Assert.Equal(formPlace, loop.BoundingRectangle);
        app.FindById(loop, "CancelButton").AsButton().Invoke();

        // A sample remap: its code has no problems and goes back to the form.
        app.GoTo("Remap");
        app.EditItem("Sample: Caps Lock to Esc");
        Window remap = app.Dialog("Edit remap");
        OpenCodeView(app, remap);
        Assert.True(app.FindById(remap, "SaveButton").IsEnabled);
        app.FindById(remap, "FormViewButton").Patterns.SelectionItem.Pattern.Select();
        Assert.True(Retry.WhileFalse(() => app.FindById(remap, "CancelButton").Name == "Cancel", AppSession.Timeout).Success);
        app.FindById(remap, "CancelButton").AsButton().Invoke();
    }

    [Fact]
    public void A_macro_action_has_a_code_view()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: click and scroll");
        Window editor = app.Dialog("Edit macro");

        app.FindById(editor, "ActionList").AsListBox().Items[0].Select();
        app.Find(editor, "Edit action").AsButton().Invoke();
        Window action = ChildDialog(editor, "Edit click");
        OpenCodeView(app, action);
        Assert.True(app.FindById(action, "TestButton").IsEnabled);
        DiscardCodeChanges(app, action);
        app.FindById(action, "CancelButton").AsButton().Invoke();
    }

    [Fact]
    public void Specific_app_is_saved_shown_on_the_card_and_needs_an_app()
    {
        using var app = new AppSession();
        app.Find(app.MainWindow, "Add loop").AsButton().Click();
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E app loop";
        app.Find(editor, "Add key").AsButton().Click();
        app.Find(editor, "Press a key or mouse button (Esc cancels)");
        PressF24();
        app.Find(editor, "F24");

        // On without an app: it cannot be saved.
        app.FindById(editor, "AppSwitch").AsToggleButton().Toggle();
        app.Find(editor, "Choose the app it works in, or turn off Specific app.");
        Assert.False(app.FindById(editor, "SaveButton").IsEnabled);

        // The ".exe" is added.
        app.FindById(editor, "AppBox").AsComboBox().EditableText = "e2e-app";
        app.Find(editor, "Works only while e2e-app.exe is in front");
        app.FindById(editor, "SaveButton").AsButton().Click();

        Assert.True(app.Has(app.MainWindow, "e2e-app.exe")); // on the loop's card
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"AppExe\": \"e2e-app.exe\""),
            AppSession.Timeout).Success);
    }

    [Fact]
    public void The_key_field_menu_sets_the_side_of_a_modifier()
    {
        using var app = new AppSession();
        app.GoTo("Remap");
        // The sample's source was chosen with the left Alt: only that Alt counts.
        app.EditItem("Sample: Left Alt + J to the left arrow");
        Window remap = app.Dialog("Edit remap");
        AutomationElement source = app.FindById(remap, "SourceField");
        Assert.True(app.Has(source, "Left Alt"));

        // The keys are a split button: its menu has Change and the sides of Alt.
        app.Find(source, "Change key").Patterns.ExpandCollapse.Pattern.Expand();
        app.MenuItem("Right Alt");
        app.MenuItem("Left or right Alt").AsMenuItem().Invoke();
        Assert.True(Retry.WhileFalse(() => app.Has(source, "Alt") && !app.Has(source, "Left Alt"), AppSession.Timeout).Success,
            "the source does not show Alt for either side");

        app.FindById(remap, "SaveButton").AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Replace(" ", "").Replace("\r", "").Replace("\n", "")
            .Contains("\"Source\":{\"Vk\":74,\"Ctrl\":false,\"Alt\":true,\"Shift\":false,\"Win\":false}"), AppSession.Timeout).Success,
            "the remap was not saved with Alt on either side");
    }
}
