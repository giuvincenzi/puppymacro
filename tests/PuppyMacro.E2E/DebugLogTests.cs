using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>A macro with Debug on keeps the log of its last run: View log in the card's More options shows it. make e2e category=Macros</summary>
[Trait("Category", "Macros")]
public class DebugLogTests
{
    [Fact]
    public void A_macro_with_Debug_on_shows_the_log_of_its_last_run()
    {
        // After a second and a half: started with Play, the macro waits for the target window to come back in front.
        MacroDefinition macro = Macro("E2E debug", null, Press(Vk.F22, delayMs: 1500));
        macro.Debug = true;
        using var app = new AppSession(seed => seed.Macros.Add(macro));
        app.GoTo("Macros");
        using var target = new TargetWindow();

        PressCard(app, target, "E2E debug", "Play");
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 15, "the macro did not play");
        WaitIdle(app, "E2E debug");
        // The run's log is written by the debug writer: on disk a moment after the run, in debug\user\macro.
        string debug = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "PuppyMacro Dev", "debug", "user", "macro");
        Assert.True(Retry.WhileFalse(() => System.IO.Directory.Exists(debug) && System.IO.Directory.GetFiles(debug, "log.txt", System.IO.SearchOption.AllDirectories)
                .Any(f => System.IO.File.ReadAllText(f) is var text && text.Contains(macro.Id.ToString("D")) && text.Contains("Ends:")), AppSession.Timeout).Success,
            $"no ended run of the macro in {debug}");

        app.CardButton("E2E debug", "More options").AsButton().Invoke();
        app.MenuItem("View log").AsMenuItem().Invoke();
        // Owned by the main window, which stays visible: UI Automation lists it under the main window.
        Window log = Retry.WhileNull(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Log of E2E debug").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))?.AsWindow()
                ?? app.Automation.GetDesktop().FindFirstChild(cf => cf.ByName("Log of E2E debug").And(cf.ByProcessId(app.ProcessId)))?.AsWindow(),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the log window did not open").Result!;
        TextBox text = app.FindById(log, "LogBox").AsTextBox();
        Assert.True(Retry.WhileFalse(() => text.Text.Contains("#1 Press F22") && text.Text.Contains("Ends: finished"), AppSession.Timeout).Success,
            $"the log: {text.Text}");
        log.Close();
    }

    [Fact]
    public void Settings_shows_the_debug_data_with_Delete_all()
    {
        using var app = new AppSession();
        app.GoTo("Settings");

        app.Find(app.MainWindow, "Debug data");
        app.Expand("DebugDataGroup");
        app.FindButton(app.MainWindow, "Delete all");
    }
}
