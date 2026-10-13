using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// A backup (.puppymacro) opened from File Explorer (FileTypes): says that it is restored from Settings › Backup, as it
/// replaces the data, and closes. Nothing else of PuppyMacro starts here (App.OnStartup): Open Settings starts PuppyMacro
/// on Settings › Backup (<see cref="App.BackupSettingsArgument"/>), or has the running one show it.
/// </summary>
public partial class BackupFileWindow
{
    internal BackupFileWindow(string file)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        MessageText.Text = Message;
        FileText.Text = Path.GetFileName(file);
    }

    /// <summary>"Restoring a backup replaces all your loops, macros, remaps and settings. You can do it in Settings › Backup.",
    /// with the page's own names.</summary>
    internal static string Message =>
        string.Format(CultureInfo.CurrentCulture, Strings.Share_DropBackupText, Strings.Settings_Title, Strings.Settings_Backup);

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, App.BackupSettingsArgument) { UseShellExecute = false })?.Dispose();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
