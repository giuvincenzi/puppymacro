using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Export's choices: Everything (an identical copy) or the chosen parts, each with its size.</summary>
public partial class ExportOptionsView : UserControl
{
    internal ExportOptionsView(IReadOnlyDictionary<BackupParts, long> sizes)
    {
        InitializeComponent();
        EverythingText.Text = $"An identical copy, to move PuppyMacro to another PC · {Size(sizes.Values.Sum())}";
        DataText.Text = $"Always included · {Size(sizes[BackupParts.Data])}";
        HistoryText.Text = $"Earlier versions of each loop, macro and remap · {Size(sizes[BackupParts.History])}";
        AiActivityText.Text = $"What AI apps asked, with Restore · {Size(sizes[BackupParts.AiActivity])}";
        LogsText.Text = $"For troubleshooting · {Size(sizes[BackupParts.Logs])}";
    }

    /// <summary>The parts to export: all of them with Everything, otherwise Data and the checked ones.</summary>
    public BackupParts Parts
    {
        get
        {
            if (WhatBox.SelectedIndex == 0)
                return BackupParts.All;
            var parts = BackupParts.Data;
            if (HistoryBox.IsChecked == true)
                parts |= BackupParts.History;
            if (AiActivityBox.IsChecked == true)
                parts |= BackupParts.AiActivity;
            if (LogsBox.IsChecked == true)
                parts |= BackupParts.Logs;
            return parts;
        }
    }

    private void OnWhatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartsPanel != null)
            PartsPanel.IsEnabled = WhatBox.SelectedIndex == 1;
    }

    /// <summary>Bytes as Windows Explorer shows them (B, KB, MB, GB of 1024), in English like the rest of the UI.</summary>
    internal static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
