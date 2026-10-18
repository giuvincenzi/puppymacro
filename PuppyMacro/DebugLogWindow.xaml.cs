using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>A macro's Debug log: its last run (<see cref="RunLog"/>), with Copy and Open folder (the pictures are there).</summary>
public partial class DebugLogWindow
{
    private readonly string _folder;

    internal DebugLogWindow(Guid macroId, string macroName)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = string.Format(CultureInfo.CurrentCulture, Strings.DebugLog_Title, macroName);
        _folder = RunLog.FolderFor(macroId);
        string? log = RunLog.Read(macroId);
        LogBox.Text = log ?? Strings.DebugLog_None;
        CopyButton.IsEnabled = log != null;
        OpenFolderButton.IsEnabled = Directory.Exists(_folder);
        Loaded += (_, _) =>
        {
            LogBox.Focus();
            LogBox.ScrollToEnd();
        };
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holds the clipboard: the user can select the text and copy it.
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
