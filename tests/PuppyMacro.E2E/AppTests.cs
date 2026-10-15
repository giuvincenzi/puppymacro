using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using System.Text.Json;
using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>The windows, pages and editors. make e2e category=UI</summary>
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
        Assert.True(app.Has(app.MainWindow, "Install PuppyMacro with Setup to get updates"));
        Assert.True(app.Has(app.MainWindow, "Not available in the development build"));

        // About opens like the other groups. Its buttons open the browser: never clicked.
        app.Expand("AboutGroup");
        Assert.True(app.FindById(app.MainWindow, "UserGuideButton").IsEnabled);
        Assert.True(app.FindById(app.MainWindow, "GitHubButton").IsEnabled);
    }

    [Fact]
    public void A_new_language_is_saved_and_asks_to_restart()
    {
        using var app = new AppSession();
        app.GoTo("Settings");

        var languages = app.FindById(app.MainWindow, "LanguageComboBox").AsComboBox();
        languages.Expand();
        string[] names = languages.Items.Select(i => i.Name).ToArray();
        Assert.Equal("Use system language", names[0]);
        Assert.Equal("Deutsch", names[1]);
        Assert.Contains("العربية", names);

        // Later keeps PuppyMacro open in English: the language is used from the next start.
        languages.Select("Italiano");
        languages.Collapse();
        Assert.True(app.Has(app.MainWindow, "PuppyMacro restarts to show Italiano. Running loops and macros stop."));
        app.FindButton(app.MainWindow, "Later").Invoke();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("\"Language\": \"it\""),
            AppSession.Timeout).Success);
        Assert.True(app.Has(app.MainWindow, "Settings"));
    }

    [Theory]
    [InlineData("it")]
    [InlineData("ar")]
    public void The_app_starts_in_the_language_it_is_given(string language)
    {
        using var app = new AppSession(language: language);
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        string Text(string key) => PuppyMacro.Resources.Strings.ResourceManager.GetString(key, culture)!;

        Assert.Equal(Text("Loops_Add"), app.FindById(app.MainWindow, "AddLoopButton").Name);
        // Data stays as it is: the samples' names are not translated.
        Assert.True(app.Has(app.MainWindow, "Sample: click every second"));

        app.GoTo(Text("Rail_Settings"));
        Assert.True(app.Has(app.MainWindow, Text("Settings_Language")));
        var languages = app.FindById(app.MainWindow, "LanguageComboBox").AsComboBox();
        languages.Expand();
        Assert.Equal(Text("Settings_LanguageSystem"), languages.Items[0].Name);
        languages.Collapse();
    }

    [Fact]
    public void Export_offers_everything_or_the_chosen_parts()
    {
        using var app = new AppSession();
        app.GoTo("Settings");
        app.Expand("BackupGroup");

        // Export… opens the choice first; the file dialog comes only after its own Export…, never pressed here.
        app.FindButton(app.MainWindow, "Export…").Invoke();
        var everything = app.FindById(app.MainWindow, "EverythingButton").AsRadioButton();
        var history = app.FindById(app.MainWindow, "HistoryBox").AsCheckBox();
        Assert.True(everything.IsChecked);
        Assert.False(history.IsEnabled);
        Assert.True(history.IsChecked);

        app.FindById(app.MainWindow, "ChooseButton").Patterns.SelectionItem.Pattern.Select();
        Assert.True(Retry.WhileFalse(() => history.IsEnabled, AppSession.Timeout).Success);
        history.Toggle();
        Assert.False(history.IsChecked);

        app.FindButton(app.MainWindow, "Cancel").Invoke();
        Assert.True(Retry.WhileFalse(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("ChooseButton")) == null,
            AppSession.Timeout).Success);
    }

    [Fact]
    public void A_new_loop_is_saved_starts_and_stops()
    {
        using var app = new AppSession();

        // Add a loop that presses F24, a key no keyboard has: running it is harmless.
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E F24 loop";
        app.FindById(editor, "AddPressKeyButton").AsButton().Invoke(); // a Press key that waits for the key right away
        app.Find(editor, "Press the key or combination (Esc cancels)");
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
    public void Macro_groups_are_expanded_and_collapsed_all_at_once()
    {
        using var app = new AppSession();
        app.GoTo("Macros");
        app.EditItem("Sample: type and confirm");
        Window editor = app.Dialog("Edit macro");
        ListBox list = app.FindById(editor, "ActionList").AsListBox();

        // The open group "Type hi" (2 actions) and one more action: header and three rows.
        Assert.True(Retry.WhileFalse(() => list.Items.Length == 4, AppSession.Timeout).Success, $"{list.Items.Length} rows");
        list.Items[1].Select();

        // Every group open: only Collapse all groups is on. The selected action's group closes, its header is selected.
        AutomationElement collapse = MoreCommand(app, editor, "CollapseAllGroupsButton");
        Assert.False(MoreCommand(app, editor, "ExpandAllGroupsButton").IsEnabled, "Expand all groups is on with every group open");
        Assert.True(collapse.IsEnabled, "Collapse all groups is off with a group open");
        collapse.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => list.Items.Length == 2, AppSession.Timeout).Success, $"{list.Items.Length} rows");
        Assert.StartsWith("Group Type hi", list.Items[0].Name);
        Assert.True(list.Items[0].IsSelected, "the closed group's header is not selected");

        // Every group closed: only Expand all groups is on. The group stays selected as a whole: its header.
        AutomationElement expand = MoreCommand(app, editor, "ExpandAllGroupsButton");
        Assert.False(MoreCommand(app, editor, "CollapseAllGroupsButton").IsEnabled, "Collapse all groups is on with every group closed");
        Assert.True(expand.IsEnabled, "Expand all groups is off with a group closed");
        expand.AsButton().Invoke();
        Assert.True(Retry.WhileFalse(() => list.Items.Length == 4, AppSession.Timeout).Success, $"{list.Items.Length} rows");
        Assert.True(list.Items[0].IsSelected, "the group's header is not selected after Expand all groups");

        // No groups: both off.
        list.Items[0].Select();
        MoreCommand(app, editor, "UngroupButton").AsButton().Invoke();
        AutomationElement total = app.FindById(editor, "TotalText");
        Assert.True(Retry.WhileFalse(() => total.Name.StartsWith("3 actions,"), AppSession.Timeout).Success, total.Name);
        Assert.False(MoreCommand(app, editor, "ExpandAllGroupsButton").IsEnabled, "Expand all groups is on without groups");
        Assert.False(MoreCommand(app, editor, "CollapseAllGroupsButton").IsEnabled, "Collapse all groups is on without groups");
    }

    /// <summary>
    /// A command in the editor's ⋯ menu, opening it first when it is closed. The menu is a popup: its own
    /// top-level window, so the command is looked for among PuppyMacro's windows.
    /// </summary>
    private static AutomationElement MoreCommand(AppSession app, Window editor, string automationId)
    {
        AutomationElement? Shown() => app.Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId(automationId).And(cf.ByProcessId(app.ProcessId))) is { IsOffscreen: false } command
            ? command : null;
        if (Shown() is { } open)
            return open;
        // ⋯ is a ToggleButton bound to the bar's IsOverflowOpen (iNKORE's CommandBar template).
        app.FindById(editor, "MoreButton").AsToggleButton().Toggle();
        return Retry.WhileNull(Shown, AppSession.Timeout, throwOnTimeout: true, timeoutMessage: $"\"{automationId}\" not shown in ⋯").Result!;
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
        app.Find(action, "Press the key or combination (Esc cancels)"); // a new Press key waits for its key at once
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
        app.Find(action, "F24");
        AutomationElement hold = app.FindById(action, "HoldBox");
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
        Retry.WhileNull(() => owner.ModalWindows.FirstOrDefault(w => AppSession.HasTitle(w.Title, title)), AppSession.Timeout, throwOnTimeout: true,
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
    [Fact]
    public void Settings_Hotkeys_starts_with_Open_PuppyMacro_whose_hotkey_can_be_changed_and_is_saved()
    {
        using var app = new AppSession();
        app.GoTo("Settings");
        app.Expand("HotkeysGroup");

        // Open PuppyMacro, Overlay mode, Stop all, Record: one under the other.
        int[] tops = new[] { "OpenMainHotkeyField", "OverlayModeHotkeyField", "StopAllHotkeyField", "RecordHotkeyField" }
            .Select(id => app.FindById(app.MainWindow, id).BoundingRectangle.Top).ToArray();
        Assert.Equal(tops.OrderBy(t => t), tops);

        // Alt + Shift + A at first (the samples' settings are a new install's); changed to F24, it is saved.
        app.Find(app.FindById(app.MainWindow, "OpenMainHotkeyField"), "Change hotkey").AsButton().Invoke();
        app.Find(app.MainWindow, "Press the hotkey (Esc cancels)");
        PressF24();
        Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Replace(" ", "").Replace("\r", "").Replace("\n", "")
            .Contains("\"OpenMainHotkey\":{\"Vk\":135,"), AppSession.Timeout).Success, "the Open PuppyMacro hotkey is not F24");
    }

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
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E F24 loop";
        app.FindById(editor, "AddPressKeyButton").AsButton().Invoke();
        app.Find(editor, "Press the key or combination (Esc cancels)");
        Keyboard.Press(VirtualKeyShort.F24);
        Keyboard.Release(VirtualKeyShort.F24);
        app.Find(editor, "F24");
        Assert.True(app.HasRow(app.FindById(editor, "ActionsList"), "Press key", "F24"));
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
        // OpacitySlider is a ValueSlider: the Slider is inside it
        app.FindById(editor, "OpacitySlider").FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider))
            .Patterns.RangeValue.Pattern.SetValue(60);
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
        Assert.True(app.PanelRowIs(panel!, "Sample: click every second", "Disabled"));
        Assert.Null(app.PanelRow(panel!, "E2E F24 loop", 3));
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
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
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
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E app loop";
        app.FindById(editor, "AddPressKeyButton").AsButton().Invoke();
        app.Find(editor, "Press the key or combination (Esc cancels)");
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
    public void Specific_app_without_an_app_names_what_uses_the_hotkey_in_all_apps()
    {
        // F22: the seeded data gives F24 to Overlay mode.
        LoopDefinition here = Playback.Loop("E2E app loop", Vk.F22, Playback.Every(Vk.F24, 1, IntervalUnit.Seconds));
        here.AppExe = "e2e-app.exe";
        using var app = new AppSession(seed => seed.Settings.Loops.AddRange(new[]
        {
            Playback.Loop("E2E all apps loop", Vk.F22, Playback.Every(Vk.F24, 1, IntervalUnit.Seconds)),
            here,
        }));
        app.GoTo("Loops");
        app.EditItem("E2E app loop");
        Window editor = app.Dialog("Edit loop");

        // Without an app, turning off Specific app would not help: the error names the loop that has F22 for all apps.
        app.FindById(editor, "AppBox").AsComboBox().EditableText = "";
        app.Find(editor, "F22 is already used by the loop \"E2E all apps loop\". Choose the app it works in, or change the hotkey.");
        Assert.False(app.FindById(editor, "SaveButton").IsEnabled);

        app.FindById(editor, "AppBox").AsComboBox().EditableText = "e2e-app";
        Assert.True(Retry.WhileFalse(() => app.FindById(editor, "SaveButton").IsEnabled, AppSession.Timeout).Success,
            "Save stayed disabled with an app chosen");
        app.FindById(editor, "CancelButton").AsButton().Invoke();
    }

    [Fact]
    public void A_loop_cannot_take_the_keys_of_a_remap_for_the_same_apps()
    {
        LoopDefinition here = Playback.Loop("E2E app loop", Vk.F22, Playback.Every(Vk.F24, 1, IntervalUnit.Seconds));
        here.AppExe = "e2e-app.exe";
        using var app = new AppSession(seed =>
        {
            seed.Settings.Remaps.Add(new RemapDefinition
            {
                Source = HotkeyBinding.FromKey(Vk.F22), Target = HotkeyBinding.FromKey(Vk.F23), Note = "E2E all apps remap",
            });
            seed.Settings.Loops.Add(here);
        });
        app.GoTo("Loops");
        app.EditItem("E2E app loop");
        Window editor = app.Dialog("Edit loop");

        // For one app it shares the key with the remap for all apps; without an app, the error names the remap.
        Assert.True(app.FindById(editor, "SaveButton").IsEnabled);
        app.FindById(editor, "AppBox").AsComboBox().EditableText = "";
        app.Find(editor, "F22 is already used by the remap \"E2E all apps remap\". Choose the app it works in, or change the hotkey.");
        Assert.False(app.FindById(editor, "SaveButton").IsEnabled);

        // For all apps it cannot be saved: the remap would never work.
        app.FindById(editor, "AppSwitch").AsToggleButton().Toggle();
        app.Find(editor, "F22 is already used by the remap \"E2E all apps remap\".");
        Assert.False(app.FindById(editor, "SaveButton").IsEnabled);
        app.FindById(editor, "CancelButton").AsButton().Invoke();
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

    [Fact]
    public void The_loop_editor_adds_each_action_from_its_bar_with_its_options_and_Enabled_is_saved()
    {
        using var app = new AppSession();
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
        Window editor = app.Dialog("Add loop");
        app.FindById(editor, "NameBox").AsTextBox().Text = "E2E bar loop";

        // A Click opens with the loop's and the click's options.
        app.FindById(editor, "AddClickButton").AsButton().Invoke();
        foreach (string row in new[] { "While the loop runs", "Every", "Hold for", "Mode", "Times", "Pause between", "Position" })
            Assert.True(app.Has(editor, row), $"no \"{row}\" row");

        // A Paste text needs its text before the loop can be saved.
        app.FindById(editor, "AddPasteTextButton").AsButton().Invoke();
        Assert.True(app.Has(editor, "Press Enter after pasting"));
        Assert.True(app.Has(editor, "Enter the text for each Paste text."));
        Assert.False(app.FindById(editor, "SaveButton").IsEnabled);
        app.FindById(editor, "PasteTextBox").AsTextBox().Text = "hi";

        app.FindById(editor, "EnabledSwitch").AsToggleButton().Toggle(); // off
        Assert.True(Retry.WhileFalse(() => app.FindById(editor, "SaveButton").IsEnabled, AppSession.Timeout).Success);
        app.FindById(editor, "SaveButton").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => SavedLoop("E2E bar loop") is { } loop && !loop.Enabled
                && loop.Actions.Count == 2 && loop.Actions[0].IsClick && loop.Actions[1].Text == "hi", AppSession.Timeout).Success,
            "the loop was not saved disabled with its Click and Paste text");
    }

    private static Models.LoopDefinition? SavedLoop(string name)
    {
        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<Models.AppSettings>(File.ReadAllText(AppSession.SettingsFile), Seed.JsonOptions);
            return settings?.Loops.FirstOrDefault(l => l.Name == name);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null; // being written
        }
    }

    [Fact]
    public void Detect_takes_the_mouse_button_pressed_in_a_loop_click_and_in_a_macro_click()
    {
        using var app = new AppSession();

        // A loop's new Click: Detect, then the middle button (inside the editor; the press is taken, not clicked).
        app.Find(app.MainWindow, "Add loop").AsButton().Invoke(); // UI Automation: not a click, which a window in front could take
        Window loop = app.Dialog("Add loop");
        app.FindById(loop, "AddClickButton").AsButton().Invoke();
        DetectWith(app, loop, MouseButton.Middle, "Middle");
        loop.Close();

        // A macro's Click (the sample's): Detect, then the back button.
        app.GoTo("Macros");
        app.EditItem("Sample: click and scroll");
        Window editor = app.Dialog("Edit macro");
        app.FindById(editor, "ActionList").AsListBox().Items[0].Select();
        app.Find(editor, "Edit action").AsButton().Invoke();
        Window action = ChildDialog(editor, "Edit click");
        DetectWith(app, action, MouseButton.XButton1, "Back (X1)");
        app.FindById(action, "CancelButton").AsButton().Invoke();
    }

    /// <summary>Detect in <paramref name="window"/>'s mouse button field, then <paramref name="button"/> pressed over the window.</summary>
    private static void DetectWith(AppSession app, Window window, MouseButton button, string expected)
    {
        app.Find(window, "Detect the mouse button").AsButton().Invoke();
        app.Find(window, "Press a mouse button (Esc cancels)");
        System.Drawing.Rectangle place = window.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(place.X + place.Width / 2, place.Y + 60));
        Mouse.Click(button);
        ComboBox list = app.Find(window, "Mouse button").AsComboBox();
        Assert.True(Retry.WhileFalse(() => list.SelectedItem?.Text == expected, AppSession.Timeout).Success,
            $"the mouse button field shows \"{list.SelectedItem?.Text}\", not \"{expected}\"");
    }
    /// <summary>A check box of Import's or Export's list (in <paramref name="window"/>), by the item it shows.</summary>
    private static CheckBox ItemBox(Window window, string title) =>
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName(title).And(cf.ByControlType(ControlType.CheckBox))),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: $"check box \"{title}\" not found").Result!.AsCheckBox();

    [Fact]
    public void Export_lists_every_item_to_choose_with_search_and_each_card_menu_has_Export()
    {
        using var app = new AppSession();

        // The card menu's Export opens the file dialog at once: only looked at here (Move down closes the menu, no keys).
        app.CardButton("Sample: click every second", "More options").AsButton().Invoke();
        Assert.NotNull(app.MenuItem("Export"));
        app.MenuItem("Move down").AsMenuItem().Invoke();

        // The title bar's More options › Export: the choice first; the file dialog comes only after the dialog's own
        // Export…, never pressed here.
        app.FindById(app.MainWindow, "TitleMoreButton").AsButton().Invoke();
        app.MenuItem("Export").AsMenuItem().Invoke();
        // A window of its own, as the editors: the main window is hidden meanwhile and comes back after.
        Window export = app.Dialog("Export");
        CheckBox loop = ItemBox(export, "Sample: click every second");
        Assert.True(app.Has(export, "Sample: type and confirm"));
        Assert.True(app.HasRow(app.FindById(export, "ExportItemList"), "Sample: type and confirm"));
        Assert.False(loop.IsChecked);
        Assert.False(app.FindButton(export, "Export 0 items…").IsEnabled);

        loop.Toggle();
        Assert.True(app.FindButton(export, "Export 1 item…").IsEnabled);

        app.FindById(export, "ExportSearchBox").AsTextBox().Text = "hold";
        Assert.True(Retry.WhileFalse(() => export.FindFirstDescendant(cf =>
            cf.ByName("Sample: click every second").And(cf.ByControlType(ControlType.CheckBox))) == null, AppSession.Timeout).Success);
        Assert.False(ItemBox(export, "Sample: hold W").IsChecked);
        Assert.Equal("1 shown · 1 selected", app.FindById(export, "ExportCountText").Name);

        app.FindButton(export, "Cancel").Invoke();
        Assert.True(Retry.WhileFalse(() => !HasWindow(app, "Export"), AppSession.Timeout, ignoreException: true).Success);
        Assert.True(Retry.WhileFalse(() => !app.MainWindow.IsOffscreen, AppSession.Timeout).Success, "the main window did not come back");
    }

    [Fact]
    public void A_file_opened_from_File_Explorer_shows_Import_and_adds_the_checked_items_disabled()
    {
        using var app = new AppSession();
        // A loop that presses F24 with the hotkey F6, already taken by the first sample, and a macro with a sample's name.
        string file = Path.Combine(Path.GetTempPath(), $"E2E shared {Guid.NewGuid():N}.puppyitems");
        File.WriteAllText(file, """
            { "Format": 1, "App": "1.16.0", "Items": [
              { "Kind": "Loop", "Item": { "Name": "E2E shared loop", "Hotkey": { "Vk": 117 },
                "Actions": [ { "Type": "Key", "KeyVk": 135, "IntervalValue": 1, "IntervalUnit": "Seconds" } ] } },
              { "Kind": "Macro", "Item": { "Name": "Sample: type and confirm", "Actions": [ { "Type": "PressKey", "Vk": 135 } ] } } ] }
            """);
        try
        {
            // File Explorer starts PuppyMacro with the file: this second one hands it to the running one and exits.
            OpenFromFileExplorer(file);

            Window window = app.Dialog("Import");
            Button import = app.FindButton(window, "Import 2 items");
            Assert.True(app.Has(window, "You already have a macro with this name, so this one is called \"Sample: type and confirm (2)\"."));
            Assert.True(app.Has(window,
                "F6 is already used by the loop \"Sample: click every second\", so this comes in without a hotkey."));
            Assert.True(ItemBox(window, "E2E shared loop").IsChecked);
            Assert.True(app.HasRow(app.FindById(window, "ImportItemList"), "E2E shared loop"));
            import.Invoke();

            Assert.True(app.Has(app.MainWindow, "E2E shared loop"));
            Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains("E2E shared loop"), AppSession.Timeout).Success);
            LoopDefinition loop = Retry.WhileNull(() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSession.SettingsFile),
                Seed.JsonOptions)!.Loops.FirstOrDefault(l => l.Name == "E2E shared loop"), AppSession.Timeout).Result!;
            Assert.False(loop.Enabled);
            Assert.Null(loop.Hotkey);
            MacroDefinition macro = Retry.WhileNull(() => AppSession.SavedMacros().FirstOrDefault(m => m.Name == "Sample: type and confirm (2)"),
                AppSession.Timeout).Result!;
            Assert.False(macro.Enabled);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Starts a second PuppyMacro with <paramref name="file"/>, as File Explorer does: it hands it over and exits.</summary>
    private static void OpenFromFileExplorer(string file)
    {
        using Process second = Process.Start(AppSession.ExePath, $"\"{file}\"")!;
        Assert.True(second.WaitForExit((int)AppSession.Timeout.TotalMilliseconds), "the second PuppyMacro did not exit");
    }

    [Fact]
    public void A_remap_whose_hotkey_is_taken_comes_in_without_one_and_its_card_says_so()
    {
        using var app = new AppSession();
        // CapsLock is the source of a sample remap: this one, to F24, comes in without it.
        string file = Path.Combine(Path.GetTempPath(), $"E2E remap {Guid.NewGuid():N}.puppyitems");
        File.WriteAllText(file, """
            { "Format": 1, "App": "1.17.0", "Items": [
              { "Kind": "Remap", "Item": { "Source": { "Vk": 20 }, "Target": { "Vk": 135 }, "Note": "E2E remap" } } ] }
            """);
        try
        {
            OpenFromFileExplorer(file);

            Window window = app.Dialog("Import");
            Button import = app.FindButton(window, "Import 1 item");
            Assert.True(app.Has(window, "CapsLock is already remapped for all apps, so this comes in without a hotkey."));
            import.Invoke();

            // The Remap page opens on it: no hotkey (what to do is in its tooltip), its switch off.
            Assert.True(app.Has(app.MainWindow, "E2E remap"));
            Assert.True(app.Has(app.MainWindow, "No hotkey yet"));
            RemapDefinition remap = Retry.WhileNull(() => JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSession.SettingsFile),
                Seed.JsonOptions)!.Remaps.FirstOrDefault(r => r.Note == "E2E remap"), AppSession.Timeout).Result!;
            Assert.Null(remap.Source);
            Assert.Equal(135, remap.Target!.Vk);
            Assert.False(remap.Enabled);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Each_list_page_has_its_view_switches_with_its_buttons_keeps_it_and_opens_an_item_on_a_click()
    {
        using var app = new AppSession();

        // The defaults (the user's choice): Loops List, Macros Table, Remap List.
        app.GoTo("Loops");
        Assert.Equal(ToggleState.On, app.ViewButton("Loops", "List").ToggleState);
        Assert.True(app.HasRow(app.MainWindow, "Sample: click every second"));

        // Table, then Grid: the same items, the choice saved at once. Clicking the view in use keeps it.
        foreach (string view in new[] { "Table", "Grid" })
        {
            ToggleButton button = app.ViewButton("Loops", view);
            button.Toggle();
            Assert.True(Retry.WhileFalse(() => button.ToggleState == ToggleState.On, AppSession.Timeout).Success, $"{view} is not on");
            Assert.Equal(ToggleState.Off, app.ViewButton("Loops", "List").ToggleState);
            Assert.True(app.HasRow(app.MainWindow, "Sample: click every second"), $"no loop in {view}");
            Assert.True(Retry.WhileFalse(() => File.ReadAllText(AppSession.SettingsFile).Contains($"\"LoopsLayout\": \"{view}\""),
                AppSession.Timeout, ignoreException: true).Success, $"{view} was not saved");
        }
        ToggleButton grid = app.ViewButton("Loops", "Grid");
        grid.Toggle();
        Assert.True(Retry.WhileFalse(() => grid.ToggleState == ToggleState.On, AppSession.Timeout).Success, "clicking Grid again turned it off");

        // A click on an item, in each view, opens its editor (there is no Edit button).
        app.EditItem("Sample: click every second");
        app.Dialog("Edit loop").Close();
        app.GoTo("Macros");
        Assert.Equal(ToggleState.On, app.ViewButton("Macros", "Table").ToggleState);
        app.EditItem("Sample: type and confirm");
        app.Dialog("Edit macro").Close();
        app.GoTo("Remap");
        Assert.Equal(ToggleState.On, app.ViewButton("Remaps", "List").ToggleState);
        app.EditItem("Sample: Caps Lock to Esc");
        app.Dialog("Edit remap").Close();
    }

    [Fact]
    public void A_list_page_opens_in_the_view_kept_in_the_settings()
    {
        using var app = new AppSession(seed =>
        {
            seed.Settings.LoopsLayout = ItemsLayout.Table;
            seed.Settings.Loops.Add(Playback.Loop("E2E kept view", Vk.F24, Playback.Every(Vk.F24, 1, IntervalUnit.Seconds)));
        });

        app.GoTo("Loops");
        Assert.Equal(ToggleState.On, app.ViewButton("Loops", "Table").ToggleState);
        Assert.True(app.HasRow(app.MainWindow, "E2E kept view"));
    }

    [Fact]
    public void Two_files_opened_one_after_the_other_show_one_Import_with_the_last()
    {
        using var app = new AppSession();
        string first = Path.Combine(Path.GetTempPath(), $"E2E first {Guid.NewGuid():N}.puppyitems");
        string second = Path.Combine(Path.GetTempPath(), $"E2E second {Guid.NewGuid():N}.puppyitems");
        static string Loop(string name) => $$"""
            { "Format": 1, "App": "1.17.0", "Items": [
              { "Kind": "Loop", "Item": { "Name": "{{name}}",
                "Actions": [ { "Type": "Key", "KeyVk": 135, "IntervalValue": 1, "IntervalUnit": "Seconds" } ] } } ] }
            """;
        File.WriteAllText(first, Loop("E2E first loop"));
        File.WriteAllText(second, Loop("E2E second loop"));
        try
        {
            OpenFromFileExplorer(first);
            Window window = app.Dialog("Import");
            Assert.NotNull(ItemBox(window, "E2E first loop"));

            // The second file takes the first one's place in the same Import.
            OpenFromFileExplorer(second);
            Assert.NotNull(ItemBox(window, "E2E second loop"));
            Assert.True(Retry.WhileFalse(() => window.FindFirstDescendant(cf =>
                cf.ByName("E2E first loop").And(cf.ByControlType(ControlType.CheckBox))) == null, AppSession.Timeout).Success);

            app.FindButton(window, "Cancel").Invoke();
            Assert.True(Retry.WhileFalse(() => !HasWindow(app, "Import"), AppSession.Timeout, ignoreException: true).Success);

            // Nothing waits behind it: no other Import opens.
            Assert.False(Retry.WhileFalse(() => HasWindow(app, "Import"), TimeSpan.FromSeconds(3), ignoreException: true).Success);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>The window of a backup opened from File Explorer: a PuppyMacro started with <paramref name="file"/>.</summary>
    private static Window BackupWindow(AppSession app, Process backup, string file)
    {
        using var attached = FlaUI.Core.Application.Attach(backup.Id);
        Window window = Retry.WhileNull(() => attached.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w =>
            w.FindFirstDescendant(cf => cf.ByName("Restore this backup from Settings")) != null), AppSession.Timeout).Result
            ?? throw new InvalidOperationException("the backup's window did not open");
        Assert.True(app.Has(window,
            "Restoring a backup replaces all your loops, macros, remaps and settings. You can do it in Settings › Backup."));
        Assert.True(app.Has(window, Path.GetFileName(file)));
        return window;
    }

    /// <summary>A top-level window of this PuppyMacro has this title.</summary>
    private static bool HasWindow(AppSession app, string title) =>
        app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId)).Any(w => w.Name == title);

    [Fact]
    public void A_backup_opened_from_File_Explorer_only_says_where_it_opens()
    {
        using var app = new AppSession();
        // Only its name matters: the window never reads it.
        string file = Path.Combine(Path.GetTempPath(), $"E2E backup {Guid.NewGuid():N}.puppymacro");
        File.WriteAllText(file, "not read");
        try
        {
            using Process backup = Process.Start(AppSession.ExePath, $"\"{file}\"")!;
            Window window = BackupWindow(app, backup, file);

            app.FindButton(window, "Close").Invoke();
            Assert.True(backup.WaitForExit((int)AppSession.Timeout.TotalMilliseconds), "the backup's PuppyMacro did not exit");
            // The PuppyMacro already running is left as it was.
            Assert.False(app.MainWindow.IsOffscreen);
            Assert.True(app.Has(app.MainWindow, "Sample: click every second"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_backup_s_Open_Settings_shows_Settings_Backup_in_the_running_PuppyMacro()
    {
        using var app = new AppSession();
        string file = Path.Combine(Path.GetTempPath(), $"E2E backup {Guid.NewGuid():N}.puppymacro");
        File.WriteAllText(file, "not read");
        try
        {
            using Process backup = Process.Start(AppSession.ExePath, $"\"{file}\"")!;
            Window window = BackupWindow(app, backup, file);

            app.FindButton(window, "Open Settings").Invoke();
            Assert.True(backup.WaitForExit((int)AppSession.Timeout.TotalMilliseconds), "the backup's PuppyMacro did not exit");
            // The running PuppyMacro shows Settings with Backup open: its Import… and Export… (never pressed here).
            Assert.NotNull(app.FindButton(app.MainWindow, "Import…"));
            Assert.NotNull(app.FindButton(app.MainWindow, "Export…"));
            Assert.False(app.MainWindow.IsOffscreen);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
