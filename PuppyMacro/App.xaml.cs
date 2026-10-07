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
using Velopack;
using Wpf.Ui.Appearance;

namespace PuppyMacro;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    // Starting PuppyMacro while it runs signals this event: the running instance shows its window.
    private const string ShowWindowEventName = "PuppyMacro.ShowWindow";
    private EventWaitHandle? _showWindowRequest;
    private RegisteredWaitHandle? _showWindowWait;
    private static bool _watchingSystemTheme;

    /// <summary>"PuppyMacro v1.1.0", from the version in PuppyMacro.csproj.</summary>
    /// <summary>Debug build (make dev): marked as DEV in the window and the tray.</summary>
    /// <remarks>Declared before <see cref="DisplayTitle"/>: static initializers run in declaration order.</remarks>
    public static bool IsDevBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static string DisplayTitle { get; } = BuildDisplayTitle();

    private static string BuildDisplayTitle()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        string title = version == null ? "PuppyMacro" : $"PuppyMacro v{version.Major}.{version.Minor}.{version.Build}";
        return IsDevBuild ? $"{title} DEV" : title;
    }

    public const string RestartArgument = "--restart";

    [STAThread]
    private static void Main()
    {
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

        _singleInstance = new Mutex(initiallyOwned: true, "PuppyMacro.SingleInstance", out _ownsMutex);
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

        // Keep the Run entry pointing at this exe (new versions live in new folders).
        // The development build never touches it: the entry belongs to the installed app.
        if (settings.StartWithWindows && !IsDevBuild)
            StartupService.Apply(true);

        var window = new MainWindow(store, settings, macros);
        MainWindow = window;
        ApplyTheme(settings.Theme, window);
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

    /// <summary>Applies System, Light or Dark. System also follows later Windows theme changes.</summary>
    public static void ApplyTheme(AppTheme mode, Window mainWindow)
    {
        if (mode == AppTheme.System)
        {
            if (!_watchingSystemTheme)
            {
                SystemThemeWatcher.Watch(mainWindow);
                _watchingSystemTheme = true;
            }
            ApplicationThemeManager.ApplySystemTheme();
            return;
        }

        if (_watchingSystemTheme && mainWindow.IsLoaded)
        {
            SystemThemeWatcher.UnWatch(mainWindow);
            _watchingSystemTheme = false;
        }
        ApplicationThemeManager.Apply(mode == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "PuppyMacro", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
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
