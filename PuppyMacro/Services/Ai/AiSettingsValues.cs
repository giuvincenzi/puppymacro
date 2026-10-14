using PuppyMacro.Models;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The settings an AI app can read (get_settings) and ask to change (update_settings, each value optional: null keeps
/// it). Start with Windows, Allow AI access, backups and updates are left to the user, and so is Language: it applies only
/// after PuppyMacro restarts, which would end the AI app's connection. The views of the list pages (List, Table, Grid:
/// LoopsLayout, MacrosLayout, RemapsLayout) stay out too: they only change how the user's own window looks.
/// </summary>
public sealed class AiSettingsValues
{
    public AppTheme? Theme { get; set; }
    public bool? CloseToTray { get; set; }
    public HotkeyBinding? StopAllHotkey { get; set; }
    public HotkeyBinding? OverlayModeHotkey { get; set; }
    public HotkeyBinding? RecordHotkey { get; set; }
    public HotkeyBinding? OpenMainHotkey { get; set; }
    public bool? ShowOverlayPanel { get; set; }
    public bool? ClickItemsInPanel { get; set; }
    public int? OverlayPanelOpacity { get; set; }
    public int? SoundVolume { get; set; }

    internal static AiSettingsValues From(AppSettings s) => new()
    {
        Theme = s.Theme,
        CloseToTray = s.CloseToTray,
        StopAllHotkey = s.StopAllHotkey?.Clone(),
        OverlayModeHotkey = s.OverlayModeHotkey?.Clone(),
        RecordHotkey = s.RecordHotkey?.Clone(),
        OpenMainHotkey = s.OpenMainHotkey?.Clone(),
        ShowOverlayPanel = s.ShowOverlayPanel,
        ClickItemsInPanel = s.ClickItemsInPanel,
        OverlayPanelOpacity = s.OverlayPanelOpacity,
        SoundVolume = s.SoundVolume,
    };
}
