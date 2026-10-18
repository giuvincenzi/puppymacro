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

        // No run yet: no log in the card's menu.
        app.CardButton("E2E debug", "More options").AsButton().Invoke();
        Assert.NotNull(app.MenuItem("Edit"));
        Assert.Null(Retry.WhileNull(() => app.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .Select(w => w.FindFirstDescendant(cf => cf.ByName("View log"))).FirstOrDefault(i => i != null), System.TimeSpan.FromSeconds(1)).Result);
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

        PressCard(app, target, "E2E debug", "Play");
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 15, "the macro did not play");
        WaitIdle(app, "E2E debug");

        app.CardButton("E2E debug", "More options").AsButton().Invoke();
        app.MenuItem("View log").AsMenuItem().Invoke();
        // Owned by the main window, which stays visible: UI Automation lists it under the main window.
        Window log = Retry.WhileNull(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("Log of E2E debug").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window)))?.AsWindow()
                ?? app.Automation.GetDesktop().FindFirstChild(cf => cf.ByName("Log of E2E debug").And(cf.ByProcessId(app.ProcessId)))?.AsWindow(),
            AppSession.Timeout, throwOnTimeout: true, timeoutMessage: "the log window did not open").Result!;
        TextBox text = app.FindById(log, "LogBox").AsTextBox();
        Assert.True(Retry.WhileFalse(() => text.Text.Contains("#1 Press F22") && text.Text.Contains("Macro ends: finished"), AppSession.Timeout).Success,
            $"the log: {text.Text}");
        log.Close();
    }
}
