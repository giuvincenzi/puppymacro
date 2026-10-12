using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using PuppyMacro.Resources;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Export's choices: Everything (an identical copy) or the chosen parts, each with its size.</summary>
public partial class ExportOptionsView : UserControl
{
    internal ExportOptionsView(IReadOnlyDictionary<BackupParts, long> sizes)
    {
        InitializeComponent();
        EverythingText.Text = Describe(Strings.Export_EverythingDescription, sizes.Values.Sum());
        DataText.Text = Describe(Strings.Export_PartDataDescription, sizes[BackupParts.Data]);
        HistoryText.Text = Describe(Strings.Export_PartHistoryDescription, sizes[BackupParts.History]);
        AiActivityText.Text = Describe(Strings.Export_PartAiActivityDescription, sizes[BackupParts.AiActivity]);
        LogsText.Text = Describe(Strings.Export_PartLogsDescription, sizes[BackupParts.Logs]);
    }

    /// <summary>A part's description with its size as {0}.</summary>
    private static string Describe(string text, long bytes) => string.Format(CultureInfo.CurrentCulture, text, Size(bytes));

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

    /// <summary>Bytes as Windows Explorer shows them (B, KB, MB, GB of 1024), in the language and format of the UI.</summary>
    internal static string Size(long bytes)
    {
        string[] units = [Strings.Export_SizeB, Strings.Export_SizeKB, Strings.Export_SizeMB, Strings.Export_SizeGB];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Format(CultureInfo.CurrentCulture, units[0], bytes)
            : string.Format(CultureInfo.CurrentCulture, units[unit], value.ToString("0.#", CultureInfo.CurrentCulture));
    }
}
