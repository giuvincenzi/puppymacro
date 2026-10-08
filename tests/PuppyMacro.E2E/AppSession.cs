using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

// One PuppyMacro at a time: the tests share the app and its data folder.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace PuppyMacro.E2E;

/// <summary>
/// Starts the development build (PuppyMacro/bin/Debug, built by make e2e) with fresh sample
/// data, like make dev, and closes it at the end. Any running PuppyMacro is closed first.
/// </summary>
public sealed class AppSession : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Application _app;

    public AppSession()
    {
        foreach (Process running in Process.GetProcessesByName("PuppyMacro"))
        {
            running.Kill();
            running.WaitForExit();
        }
        if (Directory.Exists(DataFolder))
            Directory.Delete(DataFolder, recursive: true);

        _app = Application.Launch(ExePath);
        Automation = new UIA3Automation();
        MainWindow = Retry.WhileNull(() => _app.GetMainWindow(Automation), Timeout, throwOnTimeout: true).Result!;
    }

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuppyMacro Dev");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    /// <summary>The dev build next to this repository's tests folder.</summary>
    public static string ExePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PuppyMacro.sln")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("PuppyMacro.sln not found above " + AppContext.BaseDirectory);
            string exe = Path.Combine(dir.FullName, "PuppyMacro", "bin", "Debug", "net10.0-windows", "win-x64", "PuppyMacro.exe");
            if (!File.Exists(exe))
                throw new InvalidOperationException("Build the development build first (make e2e does it): " + exe);
            return exe;
        }
    }

    public UIA3Automation Automation { get; }

    public Window MainWindow { get; }

    public int ProcessId => _app.ProcessId;

    public AutomationElement Find(AutomationElement root, string name)
    {
        AutomationElement? found = Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), Timeout).Result;
        return found ?? throw new TimeoutException($"\"{name}\" not found. {Describe(root)}");
    }

    /// <summary>State of a window, for failure messages.</summary>
    private static string Describe(AutomationElement root)
    {
        try
        {
            string names = string.Join(" | ", root.FindAllChildren().Select(c => $"{c.ControlType}:{c.Name}").Take(15));
            return $"Window \"{root.Name}\" offscreen={root.IsOffscreen} available={root.IsAvailable} children=[{names}]";
        }
        catch (Exception ex)
        {
            return "Window state not readable: " + ex.Message;
        }
    }

    public AutomationElement FindById(AutomationElement root, string automationId) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Timeout, throwOnTimeout: true,
            timeoutMessage: $"\"{automationId}\" not found").Result!;

    public bool Has(AutomationElement root, string name) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(3)).Result != null;

    public void GoTo(string page)
    {
        Find(MainWindow, page).AsRadioButton().IsChecked = true;
    }

    /// <summary>Opens the "…" menu of the list item showing <paramref name="itemText"/> and selects <paramref name="menuItem"/>.</summary>
    public void ItemMenu(string itemText, string menuItem)
    {
        AutomationElement text = Find(MainWindow, itemText);
        double y = text.BoundingRectangle.Y;
        // The "…" button is the right-most unnamed button on the item's row.
        AutomationElement more = MainWindow
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .Where(b => string.IsNullOrEmpty(b.Name) && string.IsNullOrEmpty(b.AutomationId)
                        && Math.Abs(b.BoundingRectangle.Y + b.BoundingRectangle.Height / 2 - y) < 40)
            .OrderBy(b => b.BoundingRectangle.X)
            .Last();
        more.Click();
        Retry.WhileNull(() => Automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem).And(cf.ByName(menuItem))),
            Timeout, throwOnTimeout: true, timeoutMessage: $"menu item \"{menuItem}\" not found").Result!.Click();
    }

    /// <summary>A window of PuppyMacro with this title (dialogs are children of the main window).</summary>
    public Window Dialog(string title) =>
        Retry.WhileNull(() => MainWindow.ModalWindows.FirstOrDefault(w => w.Title == title), Timeout, throwOnTimeout: true,
            timeoutMessage: $"window \"{title}\" not found").Result!;

    /// <summary>A top-level window of this PuppyMacro (overlay panel, floating buttons), or null after a timeout.</summary>
    public AutomationElement? TopWindow(string title) =>
        Retry.WhileNull(() => Automation.GetDesktop().FindFirstChild(cf => cf.ByName(title).And(cf.ByProcessId(ProcessId))),
            Timeout).Result;

    public void Dispose()
    {
        Automation.Dispose();
        if (!_app.HasExited)
            _app.Kill();
        _app.Dispose();
    }
}
