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
// Windows Forms prepared once before the tests, so a test's wait for its target window is only the window (TargetWindow).
[assembly: AssemblyFixture(typeof(PuppyMacro.E2E.WindowsFormsReady))]

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
    /// <param name="language">
    /// The language of the app (PUPPYMACRO_LANGUAGE): English by default, whatever Windows' language, so the
    /// tests find controls by their English text.
    /// </param>
    /// <param name="inTray">
    /// The app starts in the system tray (the seed's Start in the system tray): it is waited for until it is idle, with no
    /// <see cref="MainWindow"/> until <see cref="OpenWithHotkey"/>.
    /// </param>
    public AppSession(Action<Seed>? seed = null, string language = "en", bool inTray = false)
    {
        TargetWindow.LastOpening = null; // this test's own, in its .uia.txt
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

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        start.Environment["PUPPYMACRO_LANGUAGE"] = language;
        _app = Application.Launch(start);
        Automation = new UIA3Automation();
        if (inTray)
            _app.WaitWhileBusy(TimeSpan.FromSeconds(30)); // started: a window it would show is there by now
        else
            MainWindow = TakeMainWindow(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The main window, taken again until it shows the first page (its Add loop button, by name in every language): the
    /// window exists before its content, and an element taken that early can keep showing no content.
    /// </summary>
    private Window TakeMainWindow(TimeSpan timeout) => Retry.WhileNull(() =>
        {
            Window? window = _app.GetMainWindow(Automation, TimeSpan.FromSeconds(1));
            return window?.FindFirstDescendant(cf => cf.ByAutomationId("AddLoopButton")) != null ? window : null;
        }, timeout, throwOnTimeout: true, ignoreException: true,
        timeoutMessage: "the main window did not show its content").Result!;

    /// <summary>Whether the main window is on the screen (not in the system tray).</summary>
    public bool MainWindowShown() =>
        NotYet(() => _app.GetMainWindow(Automation, TimeSpan.FromSeconds(1))) is { IsAvailable: true, IsOffscreen: false };

    /// <summary>
    /// Started <c>inTray</c>: presses <paramref name="openHotkey"/> (the seed's Open PuppyMacro) in <paramref name="target"/>
    /// until the main window shows, as the hotkeys work only once PuppyMacro has started them, then takes it as
    /// <see cref="MainWindow"/>.
    /// </summary>
    public void OpenWithHotkey(TargetWindow target, int openHotkey)
    {
        Assert.True(Retry.WhileFalse(() =>
        {
            if (MainWindowShown())
                return true;
            target.Activate();
            target.Tap(openHotkey);
            return Retry.WhileFalse(MainWindowShown, TimeSpan.FromSeconds(2)).Success;
        }, TimeSpan.FromSeconds(30)).Success, "the Open PuppyMacro hotkey did not open the main window from the tray");
        MainWindow = TakeMainWindow(Timeout);
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

    public Window MainWindow { get; private set; } = null!;

    public int ProcessId => _app.ProcessId;

    /// <summary>UIA_E_TIMEOUT: UI Automation got no answer from the window in time.</summary>
    private const int UiaTimeout = unchecked((int)0x80131505);

    /// <summary>
    /// <paramref name="find"/>, or null when UI Automation timed out (<see cref="UiaTimeout"/>): PuppyMacro's UI thread was
    /// busy, for example building a window, so the helpers below look again at their next try, until their own timeout. A
    /// test failed with "Operation timed out" while the editor it waited for was already on the screen. Any other error
    /// still fails the test.
    /// </summary>
    private static T? NotYet<T>(Func<T?> find) where T : class
    {
        try
        {
            return find();
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == UiaTimeout)
        {
            return null;
        }
    }

    public AutomationElement Find(AutomationElement root, string name)
    {
        AutomationElement? found = Retry.WhileNull(() => NotYet(() => root.FindFirstDescendant(cf => cf.ByName(name))), Timeout).Result;
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

    /// <summary>
    /// A command of an editor's bar by its x:Name: on the bar when it fits, else in ⋯, which opens (the bar moves the
    /// commands that don't fit into ⋯, and on the runner's 1024 × 768 screen most of them don't; a closed ⋯ shows none).
    /// </summary>
    public AutomationElement EditorCommand(Window editor, string automationId)
    {
        AutomationElement? Shown() => NotYet(() => Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId(automationId).And(cf.ByProcessId(ProcessId))) is { IsOffscreen: false } command
            ? command : null);
        if (Shown() is { } open)
            return open;
        // ⋯ is a ToggleButton bound to the bar's IsOverflowOpen (iNKORE's CommandBar template).
        FindById(editor, "MoreButton").AsToggleButton().Toggle();
        return Retry.WhileNull(Shown, Timeout, throwOnTimeout: true, timeoutMessage: $"\"{automationId}\" not shown on the bar nor in ⋯").Result!;
    }

    /// <summary>A button by its name (a text with the same words is skipped).</summary>
    public Button FindButton(AutomationElement root, string name) =>
        Retry.WhileNull(() => NotYet(() => root.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)))),
            Timeout, throwOnTimeout: true, timeoutMessage: $"button \"{name}\" not found").Result!.AsButton();

    public AutomationElement FindById(AutomationElement root, string automationId) =>
        Retry.WhileNull(() => NotYet(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId))), Timeout, throwOnTimeout: true,
            timeoutMessage: $"\"{automationId}\" not found").Result!;

    public bool Has(AutomationElement root, string name) =>
        Retry.WhileNull(() => NotYet(() => root.FindFirstDescendant(cf => cf.ByName(name))), TimeSpan.FromSeconds(3)).Result != null;

    /// <summary>
    /// An overlay panel row by its item's name, or null after <paramref name="seconds"/>. The row's accessible name is
    /// "name, state" (ui.md): the row is found by it, not by its texts, which UI Automation does not always show.
    /// </summary>
    public AutomationElement? PanelRow(AutomationElement panel, string item, double seconds = 10) =>
        Retry.WhileNull(() => FindPanelRow(panel, item), TimeSpan.FromSeconds(seconds), ignoreException: true).Result;

    /// <summary>Waits until the overlay panel row of <paramref name="item"/> shows <paramref name="state"/> ("Disabled", "Idle").</summary>
    public bool PanelRowIs(AutomationElement panel, string item, string state) =>
        Retry.WhileFalse(() => FindPanelRow(panel, item)?.Name == $"{item}, {state}", Timeout, ignoreException: true).Success;

    /// <summary>
    /// Whether <paramref name="root"/> has a list row (a DataItem, or a ListItem in a ListView) whose accessible name holds
    /// every one of <paramref name="texts"/>: rows are named by what they show (ui.md), never by their item's class.
    /// </summary>
    public bool HasRow(AutomationElement root, params string[] texts) =>
        Retry.WhileNull(() => root.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)
                .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)))
            .FirstOrDefault(row => texts.All(text => row.Name.Contains(text, StringComparison.Ordinal))), Timeout,
            ignoreException: true).Result != null;

    private static AutomationElement? FindPanelRow(AutomationElement panel, string item) =>
        panel.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
            .FirstOrDefault(row => row.Name.StartsWith(item + ", ", StringComparison.Ordinal));

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

    /// <summary>
    /// The row of <paramref name="itemText"/> in the page's list, table or grid: the one whose accessible name it is (a
    /// loop's or macro's name, a remap's note or keys; ui.md).
    /// </summary>
    public AutomationElement ItemRow(string itemText) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(
                cf => cf.ByName(itemText).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem)
                    .Or(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)))), Timeout,
            ignoreException: true).Result ?? throw new TimeoutException($"no list row \"{itemText}\". {Describe(MainWindow)}");

    /// <summary>The button named <paramref name="buttonName"/> (Play, Stop, More options) on the row of <paramref name="itemText"/>.</summary>
    public AutomationElement CardButton(string itemText, string buttonName) => FindButton(ItemRow(itemText), buttonName);

    /// <summary>
    /// Opens the editor of the list item showing <paramref name="itemText"/>, in any view: a click on the item near its left
    /// end (its icon and name, away from its buttons). A real click: the library's SettingsCard has no Invoke pattern.
    /// </summary>
    public void EditItem(string itemText)
    {
        System.Drawing.Rectangle row = ItemRow(itemText).BoundingRectangle;
        MainWindow.SetForeground();
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(row.Left + 40, row.Top + row.Height / 2));
    }

    /// <summary>The List, Table or Grid button of a page ("Loops", "Macros", "Remaps"), by its x:Name.</summary>
    public ToggleButton ViewButton(string page, string view) => FindById(MainWindow, page + view + "Button").AsToggleButton();

    /// <summary>
    /// A window of PuppyMacro with this title, or with this title and " - " what it edits ("Edit macro" finds
    /// "Edit macro - Sample: type and confirm"). While an editor is open the main window is hidden, so the
    /// editors are looked for among this PuppyMacro's top-level windows, not as children of the main window.
    /// </summary>
    public Window Dialog(string title) =>
        Retry.WhileNull(() => NotYet(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(ProcessId))
                .FirstOrDefault(w => HasTitle(w.Name, title))?.AsWindow()),
            Timeout, throwOnTimeout: true, timeoutMessage: $"window \"{title}\" not found").Result!;

    /// <summary>The window title is <paramref name="title"/>, or it followed by " - " and what the window edits.</summary>
    public static bool HasTitle(string? windowTitle, string title) =>
        windowTitle == title || windowTitle?.StartsWith(title + " - ", StringComparison.Ordinal) == true;

    /// <summary>An item of an open menu (a menu is a top-level window of its own) of this PuppyMacro.</summary>
    public AutomationElement MenuItem(string name) =>
        Retry.WhileNull(() => NotYet(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(ProcessId))
                .Select(w => w.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem))))
                .FirstOrDefault(i => i != null)),
            Timeout, throwOnTimeout: true, timeoutMessage: $"menu item \"{name}\" not found").Result!;

    /// <summary>A top-level window of this PuppyMacro (overlay panel, floating buttons), or null after a timeout.</summary>
    /// <remarks>
    /// Taken again until it shows its content, as <see cref="MainWindow"/>: an element taken right after its window appears
    /// can keep showing no children (a test then did not find the overlay panel's rows that were on the screen). A window
    /// that exists but shows no children until the timeout is returned as it is.
    /// </remarks>
    public AutomationElement? TopWindow(string title)
    {
        AutomationElement? found = null;
        AutomationElement? withContent = Retry.WhileNull(() =>
        {
            AutomationElement? window = Automation.GetDesktop().FindFirstChild(cf => cf.ByName(title).And(cf.ByProcessId(ProcessId)));
            found = window ?? found;
            return window?.FindFirstChild() != null ? window : null;
        }, Timeout, ignoreException: true).Result;
        return withContent ?? found;
    }

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
        SaveScreenshot();
        SaveUiaTree();
        Automation.Dispose();
        if (!_app.HasExited)
            _app.Kill();
        _app.Dispose();
    }

    /// <summary>
    /// On GitHub (the test workflow sets E2E_SCREENSHOTS to a folder): the whole screen as the test leaves it, before
    /// PuppyMacro closes, as Class.Method.png, for every test: the workflow puts all of them in the group's artifact and
    /// the failed tests' ones also apart (its README.txt, written by test.yml's describe_group_result).
    /// </summary>
    private static void SaveScreenshot()
    {
        string? folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS");
        string? type = TestContext.Current.TestClass?.TestClassName;
        string? method = TestContext.Current.TestMethod?.MethodName;
        if (string.IsNullOrEmpty(folder) || type == null || method == null)
            return;
        try
        {
            Directory.CreateDirectory(folder);
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var image = new System.Drawing.Bitmap(screen.Width, screen.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(image))
                graphics.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
            image.Save(Path.Combine(folder, $"{type}.{method}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            Console.Error.WriteLine($"Screenshot not saved: {ex.Message}");
        }
    }

    /// <summary>
    /// With <see cref="SaveScreenshot"/>: what UI Automation sees of every window of this PuppyMacro, as Class.Method.uia.txt,
    /// one element per line (control type, "name", [AutomationId], offscreen), indented by depth. It tells what a screenshot
    /// cannot: whether an element the test looked for was there, with another name, or not yet.
    /// </summary>
    private void SaveUiaTree()
    {
        string? folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS");
        string? type = TestContext.Current.TestClass?.TestClassName;
        string? method = TestContext.Current.TestMethod?.MethodName;
        if (string.IsNullOrEmpty(folder) || type == null || method == null)
            return;
        const int maxLines = 5000;
        var lines = new List<string>(MachineState()) { "" };
        void Write(AutomationElement element, int depth)
        {
            if (lines.Count >= maxLines)
                return;
            try
            {
                var p = element.Properties;
                lines.Add($"{new string(' ', depth * 2)}{p.ControlType.ValueOrDefault} \"{p.Name.ValueOrDefault}\"" +
                          $" [{p.AutomationId.ValueOrDefault}]{(p.IsOffscreen.ValueOrDefault ? " offscreen" : "")}");
                foreach (AutomationElement child in element.FindAllChildren())
                    Write(child, depth + 1);
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.ElementNotAvailableException
                                           or System.Runtime.InteropServices.COMException or TimeoutException)
            {
                lines.Add($"{new string(' ', depth * 2)}(not readable: {ex.Message})");
            }
        }
        try
        {
            Directory.CreateDirectory(folder);
            foreach (AutomationElement window in Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(ProcessId)))
                Write(window, 0);
            if (lines.Count >= maxLines)
                lines.Add($"(cut at {maxLines} lines)");
            File.WriteAllLines(Path.Combine(folder, $"{type}.{method}.uia.txt"), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            Console.Error.WriteLine($"UI Automation tree not saved: {ex.Message}");
        }
    }

    /// <summary>
    /// The first lines of Class.Method.uia.txt: when, the machine's memory, and whether PuppyMacro was responding (its UI
    /// thread handles window messages) and how much memory it used. A slow or short-of-memory runner, or a PuppyMacro that
    /// hangs, shows here.
    /// </summary>
    private List<string> MachineState()
    {
        const double mb = 1024 * 1024;
        var lines = new List<string> { $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}" };
        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        lines.Add(Native.GlobalMemoryStatusEx(ref memory)
            ? $"Machine memory: {memory.dwMemoryLoad}% in use, {memory.ullAvailPhys / mb:0} MB free of {memory.ullTotalPhys / mb:0} MB; " +
              $"paging file {memory.ullAvailPageFile / mb:0} MB free of {memory.ullTotalPageFile / mb:0} MB"
            : "Machine memory: not readable");
        lines.Add($"Target window: {TargetWindow.LastOpening ?? "not opened in this test"}");
        try
        {
            using Process app = Process.GetProcessById(ProcessId);
            lines.Add($"PuppyMacro (process {ProcessId}): {(app.Responding ? "responding" : "NOT RESPONDING")}, " +
                      $"{app.WorkingSet64 / mb:0} MB in use, {app.Threads.Count} threads, " +
                      $"{app.TotalProcessorTime.TotalSeconds:0.0} s of CPU since it started");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            lines.Add($"PuppyMacro (process {ProcessId}): not running ({ex.Message})");
        }
        return lines;
    }
}

/// <summary>
/// The data a test starts from, written to the dev data folder before the app starts (the samples
/// are added only to an empty folder). Global hotkeys are F keys, as tests cannot press modifiers:
/// Stop all F19, Overlay mode F24, Record F18, no Open PuppyMacro (a test that needs it sets one, as OverlayTests' F14).
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

    /// <summary>Images of the macros' conditions, by their Id (the SHA-256 of the PNG), written in the data folder's images folder.</summary>
    public Dictionary<string, byte[]> Images { get; } = new();

    internal void Write()
    {
        Directory.CreateDirectory(AppSession.MacrosFolder);
        Settings.MacroOrder = Macros.Select(m => m.Id).ToList();
        File.WriteAllText(AppSession.SettingsFile, JsonSerializer.Serialize(Settings, JsonOptions));
        foreach (MacroDefinition macro in Macros)
            File.WriteAllText(Path.Combine(AppSession.MacrosFolder, macro.Id + ".json"), JsonSerializer.Serialize(macro, JsonOptions));
        string images = Path.Combine(AppSession.DataFolder, "images");
        Directory.CreateDirectory(images);
        foreach (var (id, png) in Images)
            File.WriteAllBytes(Path.Combine(images, id + ".png"), png);
    }
}
