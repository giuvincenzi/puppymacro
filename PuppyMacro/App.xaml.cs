using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Wpf.Ui.Appearance;

namespace PuppyMacro;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private static bool _watchingSystemTheme;

    /// <summary>"PuppyMacro v1.1.0", from the version in PuppyMacro.csproj.</summary>
    public static string DisplayTitle { get; } = BuildDisplayTitle();

    private static string BuildDisplayTitle()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        return version == null ? "PuppyMacro" : $"PuppyMacro v{version.Major}.{version.Minor}.{version.Build}";
    }

    public const string RestartArgument = "--restart";

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
            if (!startInTray)
                MessageBox.Show("PuppyMacro is already running. Open it from the system tray.", "PuppyMacro",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        string? warning = null;
        try
        {
            AppPaths.MigrateFromExeFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"Your loops and macros could not be copied to {AppPaths.DataFolder}.\n\nDetails: {ex.Message}";
        }

        var store = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings = store.Load(out string? loadWarning);
        warning ??= loadWarning;

        var macros = new MacroLibrary(AppPaths.MacrosFolder);
        macros.Load(settings.MacroOrder, out string? macroWarning);
        warning ??= macroWarning;

        // Keep the Run entry pointing at this exe (new versions live in new folders).
        if (settings.StartWithWindows)
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
        if (_ownsMutex)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
