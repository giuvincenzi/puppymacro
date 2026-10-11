using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using iNKORE.UI.WPF.Modern;
using Velopack;

namespace PuppyMacro;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    // Starting PuppyMacro while it runs signals this event: the running instance shows its window.
    private const string ShowWindowEventName = "PuppyMacro.ShowWindow";
    private EventWaitHandle? _showWindowRequest;
    private RegisteredWaitHandle? _showWindowWait;

    /// <summary>"PuppyMacro v1.1.0", from the version in PuppyMacro.csproj.</summary>
    /// <summary>Debug build (make dev): marked as DEV in the window and the tray.</summary>
    /// <remarks>Declared before <see cref="DisplayTitle"/>: static initializers run in declaration order.</remarks>
    public static bool IsDevBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Set by make screenshots: a development build that looks like a release (no strip, no DEV). An environment
    /// variable, not an argument: the script starts PuppyMacro with Start-Process, which waits for nothing.</summary>
    public const string ScreenshotsVariable = "PUPPYMACRO_SCREENSHOTS";

    /// <summary>A Debug build started with <see cref="ScreenshotsVariable"/> set to 1 (make screenshots).</summary>
    /// <remarks>Declared before <see cref="DisplayTitle"/>: static initializers run in declaration order.</remarks>
    public static bool IsScreenshotMode { get; } =
        IsDevBuild && Environment.GetEnvironmentVariable(ScreenshotsVariable) == "1";

    /// <summary>The development build's marks (the strip, DEV in titles and in the overlay panel) are shown: not in
    /// <see cref="IsScreenshotMode"/>. Its data folder and samples stay a development build's (<see cref="IsDevBuild"/>).</summary>
    public static bool ShowsDevMarks { get; } = IsDevBuild && !IsScreenshotMode;

    public static string DisplayTitle { get; } = BuildDisplayTitle();

    private static string BuildDisplayTitle()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        string title = version == null ? "PuppyMacro" : $"PuppyMacro v{version.Major}.{version.Minor}.{version.Build}";
        return ShowsDevMarks ? $"{title} DEV" : title;
    }

    public const string RestartArgument = "--restart";

    /// <summary>The mutex a running PuppyMacro holds (one at a time; the AI bridge checks it to know PuppyMacro is open).</summary>
    public const string SingleInstanceName = "PuppyMacro.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Started by an AI app as its MCP server: only the bridge to the running PuppyMacro, no window, no update
        // applied (that would restart PuppyMacro from here).
        if (Environment.GetCommandLineArgs().Skip(1).Contains(AiBridge.Argument))
        {
            Environment.ExitCode = AiBridge.Run();
            return;
        }

        // Handles install, update and uninstall hooks (it exits the process for those),
        // and applies an update that was downloaded but not applied yet.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => StartupService.Apply(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startInTray = e.Args.Contains(StartupService.TrayArgument);
        bool restarting = e.Args.Contains(RestartArgument);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out _ownsMutex);
        if (!_ownsMutex && restarting)
        {
            // After Import, the previous instance is still closing: wait for it.
            try
            {
                _ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
        }
        if (!_ownsMutex)
        {
            // Started with Windows while already running: nothing to do.
            if (!startInTray)
                ShowRunningInstance();
            Shutdown();
            return;
        }

        _showWindowRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(_showWindowRequest,
            (_, _) => Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        string? warning = null;
#if DEBUG
        DevSampleData.WriteIfEmpty();
#else
        try
        {
            AppPaths.MigrateFromExeFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"Your loops and macros could not be copied to {AppPaths.DataFolder}.\n\nDetails: {ex.Message}";
        }
#endif

        var store = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings = store.Load(out string? loadWarning);
        warning ??= loadWarning;

        var macros = new MacroLibrary(AppPaths.MacrosFolder);
        macros.Load(settings.MacroOrder, out string? macroWarning);
        warning ??= macroWarning;
        if (settings.LoadedSchemaVersion < 6 && !macros.FillButtonOpacity(settings.OverlayPanelOpacity))
            warning ??= "Some macro files could not be updated to this version.";

        // Keep the Run entry pointing at this exe (new versions live in new folders).
        // The development build never touches it: the entry belongs to the installed app.
        if (settings.StartWithWindows && !IsDevBuild)
            StartupService.Apply(true);

        var window = new MainWindow(store, settings, macros);
        MainWindow = window;
        ApplyTheme(settings.Theme);
        if (startInTray)
            window.StartInTray();
        else
            window.Show();

        if (warning != null)
            MessageBox.Show(warning, "PuppyMacro", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Asks the running instance to show its window. This process was started by the user, so it
    /// may bring a window to the front; it lets the running instance do it.
    /// </summary>
    private static void ShowRunningInstance()
    {
        foreach (Process running in Process.GetProcessesByName("PuppyMacro"))
        {
            if (running.Id != Environment.ProcessId)
                NativeMethods.AllowSetForegroundWindow((uint)running.Id);
            running.Dispose();
        }
        if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out EventWaitHandle? request))
        {
            using (request)
                request.Set();
        }
    }

    /// <summary>Starts a new instance (used after Import) and closes this one.</summary>
    public static void Restart()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ExecutablePath, RestartArgument)
        {
            UseShellExecute = false,
        });
        Current.Shutdown();
    }

    /// <summary>Applies System, Light or Dark. System (no theme set) also follows later Windows theme changes.</summary>
    public static void ApplyTheme(AppTheme mode) =>
        ThemeManager.Current.ApplicationTheme = mode switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => null,
        };

    /// <summary>The theme in use, also when it follows Windows.</summary>
    public static bool IsDarkTheme => ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;

    private static bool _showingError;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Console.Error.WriteLine(e.Exception);
        // The message box keeps the dispatcher running: an error thrown again while drawing (layout)
        // would open a box inside the box until the stack overflows. One box at a time.
        if (_showingError)
            return;
        _showingError = true;
        try
        {
            MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "PuppyMacro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowWait?.Unregister(null);
        _showWindowRequest?.Dispose();
        if (_ownsMutex)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
