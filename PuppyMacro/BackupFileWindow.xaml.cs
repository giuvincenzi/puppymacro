using System.Globalization;
using System.IO;
using System.Windows;
using PuppyMacro.Resources;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// A backup (.puppymacro) opened from File Explorer (FileTypes): says that it opens from Settings › Backup, as it replaces all
/// the data, and closes. Nothing else of PuppyMacro starts (App.OnStartup).
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

    /// <summary>"A backup replaces all your data, so you open it from Settings › Backup.", with the page's own names.</summary>
    internal static string Message =>
        string.Format(CultureInfo.CurrentCulture, Strings.Share_DropBackupText, Strings.Settings_Title, Strings.Settings_Backup);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
