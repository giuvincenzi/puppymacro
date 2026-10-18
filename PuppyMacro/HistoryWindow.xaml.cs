using System;
using System.Collections.Generic;
using System.Globalization;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>The History of a Library component (the editors' History view): Restore puts a version back at once.</summary>
public partial class HistoryWindow
{
    internal HistoryWindow(string name, IReadOnlyList<HistoryVersion> versions)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = string.Format(CultureInfo.CurrentCulture, Strings.Library_HistoryTitle, name);
        HistoryPanel.Load(versions);
        HistoryPanel.RestoreRequested += version => RestoreRequested?.Invoke(version);
    }

    internal event Action<HistoryVersion>? RestoreRequested;
}
