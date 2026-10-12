using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Resources;
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

    /// <summary>Set by the create_release_pr workflow (scripts/dev.ps1 -Screenshots): a development build that looks like a release (no strip, no DEV). An environment
    /// variable, not an argument: the script starts PuppyMacro with Start-Process, which waits for nothing.</summary>
    public const string ScreenshotsVariable = "PUPPYMACRO_SCREENSHOTS";

    /// <summary>A Debug build started with <see cref="ScreenshotsVariable"/> set to 1 (scripts/dev.ps1 -Screenshots).</summary>
    /// <remarks>Declared before <see cref="DisplayTitle"/>: static initializers run in declaration order.</remarks>
    public static bool IsScreenshotMode { get; } =
        IsDevBuild && Environment.GetEnvironmentVariable(ScreenshotsVariable) == "1";

    /// <summary>The development build's marks (the strip, DEV in titles and in the overlay panel) are shown: not in
    /// <see cref="IsScreenshotMode"/>. Its data folder and samples stay a development build's (<see cref="IsDevBuild"/>).</summary>
    public static bool ShowsDevMarks { get; } = IsDevBuild && !IsScreenshotMode;

    /// <summary>Set by the end-to-end tests and make screenshots: a development build in this language (a code of
    /// <see cref="AppLanguages.All"/>, "en"), whatever the setting and Windows' language, so their texts are known.</summary>
    public const string LanguageVariable = "PUPPYMACRO_LANGUAGE";

    /// <summary>Windows' display language and regional format, as they were when PuppyMacro started.</summary>
    private static readonly CultureInfo WindowsLanguage = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo WindowsFormat = CultureInfo.CurrentCulture;

    /// <summary>The language of the texts since PuppyMacro started (a code of <see cref="AppLanguages.All"/>).</summary>
    public static string LanguageInUse { get; private set; } = AppLanguages.English;

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
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartupService.Apply(false);
                FileTypes.Unregister();
            })
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
        // A .puppyitems file opened from File Explorer (FileTypes): Import shows it.
        string? itemsFile = e.Args.FirstOrDefault(a => a.EndsWith(ItemFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (itemsFile != null)
            itemsFile = Path.GetFullPath(itemsFile);

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
            if (itemsFile != null)
                OpenFileChannel.Send(itemsFile);
            Shutdown();
            return;
        }

        _showWindowRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(_showWindowRequest,
            (_, _) => Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.ShowFromTray()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        OpenFileChannel.Listen(path => Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.OpenItemsFile(path)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // The migration runs before the settings say the language: its warning is written once the language is set.
        Exception? migrationError = null;
#if DEBUG
        DevSampleData.WriteIfEmpty();
#else
        try
        {
            AppPaths.MigrateFromExeFolder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            migrationError = ex;
        }
#endif

        var store = new SettingsStore(AppPaths.SettingsFile);
        AppSettings settings = store.Load(out string? loadWarning);
        // Before any window: x:Static texts are read when a window is built, and a binding formats with the
        // FrameworkElement.Language it has then.
        ApplyLanguage(settings.Language);
        string? warning = migrationError != null
            ? string.Format(CultureInfo.CurrentCulture, Strings.App_MigrationFailed, AppPaths.DataFolder, migrationError.Message)
            : loadWarning;

        var macros = new MacroLibrary(AppPaths.MacrosFolder);
        macros.Load(settings.MacroOrder, out string? macroWarning);
        warning ??= macroWarning;
        if (settings.LoadedSchemaVersion < 6 && !macros.FillButtonOpacity(settings.OverlayPanelOpacity))
            warning ??= Strings.App_MacrosNotUpdated;

        // Keep the Run entry pointing at this exe (new versions live in new folders).
        // The development build never touches it: the entry belongs to the installed app.
        if (settings.StartWithWindows && !IsDevBuild)
            StartupService.Apply(true);
        // The same for the icons and the open command of PuppyMacro's files: they belong to the installed app.
        if (!IsDevBuild)
            FileTypes.Register();

        var window = new MainWindow(store, settings, macros);
        MainWindow = window;
        ApplyTheme(settings.Theme);
        if (startInTray && itemsFile == null)
            window.StartInTray();
        else
            window.Show();
        if (itemsFile != null)
            window.OpenItemsFile(itemsFile);

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

    /// <summary>The language of the texts with this Language setting (on this Windows).</summary>
    public static string LanguageFor(string? setting) => AppLanguages.Resolve(setting, WindowsLanguage);

    /// <summary>
    /// Sets the language of the texts and the format of numbers and dates for every thread, once at start (changing the
    /// Language setting restarts PuppyMacro). WPF formats bindings with FrameworkElement.Language, which is en-US unless set.
    /// </summary>
    private static void ApplyLanguage(string? setting)
    {
        string? forced = IsDevBuild ? AppLanguages.Normalize(Environment.GetEnvironmentVariable(LanguageVariable)) : null;
        LanguageInUse = LanguageFor(forced ?? setting);

        CultureInfo texts = CultureInfo.GetCultureInfo(LanguageInUse);
        CultureInfo format = AppLanguages.FormatCulture(LanguageInUse, WindowsFormat);
        CultureInfo.DefaultThreadCurrentUICulture = texts;
        CultureInfo.DefaultThreadCurrentCulture = format;
        CultureInfo.CurrentUICulture = texts;
        CultureInfo.CurrentCulture = format;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(format.IetfLanguageTag)));
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
            MessageBox.Show(string.Format(CultureInfo.CurrentCulture, Strings.App_UnexpectedError, e.Exception.Message), "PuppyMacro",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
