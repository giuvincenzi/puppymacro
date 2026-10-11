using System;
using System.Windows;

namespace PuppyMacro.Views;

/// <summary>
/// The "Show in overlay" expander of the loop and macro editors: shown in overlay mode, hidden there while
/// disabled, and clicks that also reach the app behind.
/// </summary>
public partial class OverlayOptionsEditor
{
    // True while loading: the switch's event then comes from the code, not from the user.
    private bool _loading;

    public OverlayOptionsEditor()
    {
        InitializeComponent();
    }

    /// <summary>Raised when Show in overlay is switched on or off.</summary>
    public event Action? ShowChanged;

    public bool ShowInOverlay => ShowSwitch.IsOn;
    public bool HideWhenDisabled => HideWhenDisabledSwitch.IsOn;
    public bool ClickPassesThrough => PassThroughSwitch.IsOn;

    public void Load(bool showInOverlay, bool hideWhenDisabled, bool clickPassesThrough)
    {
        _loading = true;
        ShowSwitch.IsOn = showInOverlay;
        HideWhenDisabledSwitch.IsOn = hideWhenDisabled;
        PassThroughSwitch.IsOn = clickPassesThrough;
        _loading = false;
        OverlayExpander.IsExpanded = false;
        ShowChanged?.Invoke();
    }

    private void OnShowSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        OverlayExpander.IsExpanded = ShowSwitch.IsOn;
        ShowChanged?.Invoke();
    }
}
