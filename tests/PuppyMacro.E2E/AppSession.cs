using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using PuppyMacro.Models;
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

    /// <param name="seed">
    /// Fills the data the app starts from instead of the samples: <see cref="Seed"/> starts with no
    /// loops, macros or remaps and with F keys as global hotkeys.
    /// </param>
    public AppSession(Action<Seed>? seed = null)
    {
        foreach (Process running in Process.GetProcessesByName("PuppyMacro"))
        {
            running.Kill();
            running.WaitForExit();
        }
        if (Directory.Exists(DataFolder))
            Directory.Delete(DataFolder, recursive: true);
        if (seed != null)
        {
            var data = new Seed();
            seed(data);
            data.Write();
        }

        _app = Application.Launch(ExePath);
        Automation = new UIA3Automation();
        // The window exists before its content, and an element taken that early can keep showing
        // no content: take it again until it shows the side rail.
        MainWindow = Retry.WhileNull(() =>
            {
                Window? window = _app.GetMainWindow(Automation, TimeSpan.FromSeconds(1));
                return window?.FindFirstDescendant(cf => cf.ByName("Loops")) != null ? window : null;
            }, TimeSpan.FromSeconds(30), throwOnTimeout: true, ignoreException: true,
            timeoutMessage: "the main window did not show its content").Result!;
    }

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuppyMacro Dev");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string MacrosFolder => Path.Combine(DataFolder, "macros");

    /// <summary>The macros in the data folder, as the app saved them.</summary>
    public static List<MacroDefinition> SavedMacros() => Directory.Exists(MacrosFolder)
        ? Directory.GetFiles(MacrosFolder, "*.json").Select(f => JsonSerializer.Deserialize<MacroDefinition>(File.ReadAllText(f), Seed.JsonOptions)!).ToList()
        : new List<MacroDefinition>();

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
            string exe = Path.Combine(dir.FullName, "PuppyMacro", "bin", "Debug", "net10.0-windows10.0.18362.0", "win-x64", "PuppyMacro.exe");
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

    /// <summary>A button by its name (a text with the same words is skipped).</summary>
    public Button FindButton(AutomationElement root, string name) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))),
            Timeout, throwOnTimeout: true, timeoutMessage: $"button \"{name}\" not found").Result!.AsButton();

    public AutomationElement FindById(AutomationElement root, string automationId) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Timeout, throwOnTimeout: true,
            timeoutMessage: $"\"{automationId}\" not found").Result!;

    public bool Has(AutomationElement root, string name) =>
        Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(3)).Result != null;

    /// <summary>Selects a page in the side rail (its ListView items support SelectionItem).</summary>
    public void GoTo(string page)
    {
        Find(MainWindow, page).Patterns.SelectionItem.Pattern.Select();
    }

    /// <summary>
    /// Opens a Settings group (SettingsExpander, found by x:Name). It has no ExpandCollapse pattern: its
    /// header is a toggle button with the AutomationId "ExpanderToggleButton".
    /// </summary>
    public void Expand(string groupId)
    {
        var header = FindById(FindById(MainWindow, groupId), "ExpanderToggleButton").AsToggleButton();
        if (header.ToggleState != FlaUI.Core.Definitions.ToggleState.On)
            header.Toggle();
    }

    /// <summary>The button named <paramref name="buttonName"/> on the list card showing <paramref name="itemText"/>.</summary>
    public AutomationElement CardButton(string itemText, string buttonName)
    {
        AutomationElement text = Find(MainWindow, itemText);
        double y = text.BoundingRectangle.Y;
        // The card's buttons are on the same row as its text.
        return MainWindow
            .FindAllDescendants(cf => cf.ByName(buttonName).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)))
            .First(b => Math.Abs(b.BoundingRectangle.Y + b.BoundingRectangle.Height / 2 - y) < 40);
    }

    /// <summary>Opens the editor of the list item showing <paramref name="itemText"/> with its card's Edit button.</summary>
    public void EditItem(string itemText) => CardButton(itemText, "Edit").AsButton().Invoke();

    /// <summary>
    /// A window of PuppyMacro with this title. While an editor is open the main window is hidden, so the
    /// editors are looked for among this PuppyMacro's top-level windows, not as children of the main window.
    /// </summary>
    public Window Dialog(string title) =>
        Retry.WhileNull(() => Automation.GetDesktop().FindFirstChild(cf => cf.ByName(title).And(cf.ByProcessId(ProcessId)))?.AsWindow(),
            Timeout, throwOnTimeout: true, timeoutMessage: $"window \"{title}\" not found").Result!;

    /// <summary>A top-level window of this PuppyMacro (overlay panel, floating buttons), or null after a timeout.</summary>
    public AutomationElement? TopWindow(string title) =>
        Retry.WhileNull(() => Automation.GetDesktop().FindFirstChild(cf => cf.ByName(title).And(cf.ByProcessId(ProcessId))),
            Timeout).Result;

    /// <summary>
    /// After leaving overlay mode with its hotkey or the panel's exit button: waits until
    /// <paramref name="overlayWindow"/> (the overlay panel or a floating button) is gone, then checks that the
    /// main window stayed in the tray.
    /// </summary>
    public void AssertLeftOverlayModeInTray(AutomationElement overlayWindow)
    {
        Assert.True(Retry.WhileFalse(() => !overlayWindow.IsAvailable || overlayWindow.IsOffscreen, Timeout).Success,
            "overlay mode did not end");
        TargetWindow.Quiet(1);
        Assert.True(!MainWindow.IsAvailable || MainWindow.IsOffscreen, "the main window came back");
    }

    public void Dispose()
    {
        Automation.Dispose();
        if (!_app.HasExited)
            _app.Kill();
        _app.Dispose();
    }
}

/// <summary>
/// The data a test starts from, written to the dev data folder before the app starts (the samples
/// are added only to an empty folder). Global hotkeys are F keys, as tests cannot press modifiers:
/// Stop all F19, Overlay mode F24, Record F18.
/// </summary>
public sealed class Seed
{
    /// <summary>The data files' format (CodeJson.Options in the app).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Settings { get; } = new()
    {
        StopAllHotkey = HotkeyBinding.FromKey(Vk.F19),
        OverlayModeHotkey = HotkeyBinding.FromKey(Vk.F24),
        RecordHotkey = HotkeyBinding.FromKey(Vk.F18),
        TrayNoticeShown = true,
        CheckForUpdates = false,
    };

    public List<MacroDefinition> Macros { get; } = new();

    internal void Write()
    {
        Directory.CreateDirectory(AppSession.MacrosFolder);
        Settings.MacroOrder = Macros.Select(m => m.Id).ToList();
        File.WriteAllText(AppSession.SettingsFile, JsonSerializer.Serialize(Settings, JsonOptions));
        foreach (MacroDefinition macro in Macros)
            File.WriteAllText(Path.Combine(AppSession.MacrosFolder, macro.Id + ".json"), JsonSerializer.Serialize(macro, JsonOptions));
    }
}
