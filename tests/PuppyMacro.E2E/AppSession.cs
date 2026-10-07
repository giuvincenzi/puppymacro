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
    private static string ExePath
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

    public AutomationElement Find(AutomationElement root, string name) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), Timeout, throwOnTimeout: true,
            timeoutMessage: $"\"{name}\" not found").Result!;

    public AutomationElement FindById(AutomationElement root, string automationId) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Timeout, throwOnTimeout: true,
            timeoutMessage: $"\"{automationId}\" not found").Result!;

    public bool Has(AutomationElement root, string name) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(3)).Result != null;

    public void GoTo(string page)
    {
        Find(MainWindow, page).AsRadioButton().IsChecked = true;
    }

    /// <summary>A window of PuppyMacro with this title (dialogs are children of the main window).</summary>
    public Window Dialog(string title) =>
        Retry.WhileNull(() => MainWindow.ModalWindows.FirstOrDefault(w => w.Title == title), Timeout, throwOnTimeout: true,
            timeoutMessage: $"window \"{title}\" not found").Result!;

    public void Dispose()
    {
        Automation.Dispose();
        if (!_app.HasExited)
            _app.Kill();
        _app.Dispose();
    }
}
