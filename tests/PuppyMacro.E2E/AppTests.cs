using System.IO;
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
