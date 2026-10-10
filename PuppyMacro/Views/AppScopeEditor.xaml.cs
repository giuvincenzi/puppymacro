using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PuppyMacro.Models;

namespace PuppyMacro.Views;

/// <summary>
/// The "Specific app" expander of the loop, macro and remap editors: off, the item works in all apps; on, only
/// while the chosen app is in front (<see cref="AppScope"/>). The app comes from the list of running apps, Pick
/// (click its window) or Browse… (its .exe).
/// </summary>
public partial class AppScopeEditor
{
    // True while loading: the controls' events then come from the code, not from the user.
    private bool _loading = true;

    public AppScopeEditor()
    {
        InitializeComponent();
        AppBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnAppTextChanged));
        UpdateHelp();
        _loading = false;
    }

    /// <summary>Raised when the switch or the app changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Loops and macros stop when another app comes in front; remaps just stop applying. Changes the help text.
    /// </summary>
    public bool StopsWhenAppChanges { get; set; }

    internal void Load(string? appExe)
    {
        _loading = true;
        AppSwitch.IsOn = appExe != null;
        AppBox.Text = appExe ?? "";
        AppExpander.IsExpanded = appExe != null;
        _loading = false;
        Update();
    }

    /// <summary>
    /// The chosen app's .exe name (".exe" added when missing); null with the switch off (all apps); "" with the
    /// switch on and no app yet (see <see cref="ValidationError"/>).
    /// </summary>
    internal string? AppExe
    {
        get
        {
            if (!AppSwitch.IsOn)
                return null;
            string name = (AppBox.Text ?? "").Trim();
            if (name.Length == 0)
                return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
    }

    /// <summary>Why the card cannot be saved as it is, or null.</summary>
    internal string? ValidationError =>
        AppExe == "" ? "Choose the app it works in, or turn off Specific app." : null;

    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppExpander.IsExpanded = AppSwitch.IsOn;
        Update();
    }

    private void OnAppTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            Update();
    }

    private void Update()
    {
        string? app = AppExe;
        AppExpander.Description = app switch
        {
            null => "Works in all apps",
            "" => "Choose the app it works in",
            _ => $"Works only while {app} is in front",
        };
        UpdateHelp();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHelp() =>
        HelpText.Text = "Choose a running app, click its window with Pick, or browse for its .exe. It works only while that app is in front"
            + (StopsWhenAppChanges ? "; it stops when another app comes in front." : ".");

    /// <summary>Lists the .exe names of the apps that have a window open.</summary>
    private void OnAppDropDownOpened(object? sender, EventArgs e)
    {
        string current = AppBox.Text;
        var names = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .Select(p => p.ProcessName + ".exe")
            .Where(n => !n.Equals("PuppyMacro.exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppBox.ItemsSource = names;
        AppBox.Text = current;
    }

    /// <summary>
    /// The editor moves off screen while the user clicks a window of the app (a modal dialog cannot be hidden
    /// safely; the main window is already hidden while an editor is open), then comes back.
    /// </summary>
    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        Window? editor = Window.GetWindow(this);
        double left = editor?.Left ?? 0;
        if (editor != null)
            editor.Left = SystemParameters.VirtualScreenLeft - editor.ActualWidth - 200;

        var picker = new PickPointWindow(pickApp: true);
        bool picked = picker.ShowDialog() == true;

        if (editor != null)
        {
            editor.Left = left;
            editor.Activate();
        }
        if (picked && !string.IsNullOrEmpty(picker.ResultApp))
            AppBox.Text = picker.ResultApp;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the app",
            Filter = "Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AppBox.Text = Path.GetFileName(dialog.FileName);
    }
}
