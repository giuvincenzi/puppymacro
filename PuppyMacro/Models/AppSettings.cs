using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PuppyMacro.Models;

public enum ActivationMode
{
    /// <summary>Press the hotkey to start, press it again to stop.</summary>
    Toggle,

    /// <summary>Runs only while the hotkey is held down.</summary>
    Hold,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum ActionType
{
    /// <summary>Presses a key or mouse button.</summary>
    Key,

    /// <summary>Pastes a text through the clipboard.</summary>
    Text,
}

public enum IntervalUnit
{
    Milliseconds,
    Seconds,
    Minutes,
    Hours,
}

public enum LoopFilter
{
    All,
    Enabled,
    Disabled,
    Running,
}

/// <summary>Which Ctrl, Alt, Shift or Win of a hotkey counts: either one, or only the left or the right one.</summary>
public enum ModifierSide
{
    Any,
    Left,
    Right,
}

/// <summary>
/// A key (or mouse button) plus optional Ctrl, Alt, Shift and Win modifiers, each with its side
/// (<see cref="ModifierSide"/>; files without one: <see cref="ModifierSide.Any"/>). A remap's source or target can
/// also be a modifier key alone, by its left or right code (<see cref="Vk"/> 0xA0 to 0xA5, 0x5B, 0x5C).
/// </summary>
public sealed class HotkeyBinding
{
    public int Vk { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModifierSide CtrlSide { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModifierSide AltSide { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModifierSide ShiftSide { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModifierSide WinSide { get; set; }

    [JsonIgnore]
    public bool IsSet => Vk != 0;

    [JsonIgnore]
    public bool HasModifiers => Ctrl || Alt || Shift || Win;

    public static HotkeyBinding FromKey(int vk) => new() { Vk = vk };

    /// <summary>The same key, modifiers and sides.</summary>
    public bool SameAs(HotkeyBinding? other) =>
        other != null && other.Vk == Vk && other.Ctrl == Ctrl && other.Alt == Alt
        && other.Shift == Shift && other.Win == Win
        && Side(Ctrl, CtrlSide) == Side(other.Ctrl, other.CtrlSide) && Side(Alt, AltSide) == Side(other.Alt, other.AltSide)
        && Side(Shift, ShiftSide) == Side(other.Shift, other.ShiftSide) && Side(Win, WinSide) == Side(other.Win, other.WinSide);

    /// <summary>
    /// The keys pressed (<paramref name="pressed"/>: each held modifier with the side held, <see cref="ModifierSide.Any"/>
    /// when both are) start this hotkey: the same key and modifiers, and each modifier on a side this hotkey accepts.
    /// </summary>
    public bool Matches(HotkeyBinding pressed) =>
        IsSet && pressed.Vk == Vk && pressed.Ctrl == Ctrl && pressed.Alt == Alt && pressed.Shift == Shift && pressed.Win == Win
        && Accepts(Ctrl, CtrlSide, pressed.CtrlSide) && Accepts(Alt, AltSide, pressed.AltSide)
        && Accepts(Shift, ShiftSide, pressed.ShiftSide) && Accepts(Win, WinSide, pressed.WinSide);

    /// <summary>Some key presses would start both: the same key and modifiers, on sides that are not opposite.</summary>
    public bool Overlaps(HotkeyBinding? other) =>
        other != null && other.IsSet && other.Vk == Vk && other.Ctrl == Ctrl && other.Alt == Alt
        && other.Shift == Shift && other.Win == Win
        && SidesOverlap(Ctrl, CtrlSide, other.CtrlSide) && SidesOverlap(Alt, AltSide, other.AltSide)
        && SidesOverlap(Shift, ShiftSide, other.ShiftSide) && SidesOverlap(Win, WinSide, other.WinSide);

    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();

    /// <summary>A side counts only with its modifier.</summary>
    private static ModifierSide Side(bool held, ModifierSide side) => held ? side : ModifierSide.Any;

    private static bool Accepts(bool held, ModifierSide side, ModifierSide pressed) =>
        !held || side == ModifierSide.Any || side == pressed;

    private static bool SidesOverlap(bool held, ModifierSide a, ModifierSide b) =>
        !held || a == ModifierSide.Any || b == ModifierSide.Any || a == b;
}

/// <summary>One thing a loop repeats: a key or a text, each on its own interval.</summary>
public sealed class LoopAction
{
    public const double MinIntervalMs = 10;
    public const double MaxIntervalMs = 24 * 60 * 60 * 1000; // 24 h

    public ActionType Type { get; set; } = ActionType.Key;
    public int KeyVk { get; set; }
    public string Text { get; set; } = "";
    public bool EnterBefore { get; set; }
    public bool EnterAfter { get; set; }

    /// <summary>Key rows only: keep the key held down while the loop runs instead of repeating it.</summary>
    public bool HoldDown { get; set; }

    public double IntervalValue { get; set; } = 40;
    public IntervalUnit IntervalUnit { get; set; } = IntervalUnit.Milliseconds;

    [JsonIgnore]
    public double IntervalMs => ToMilliseconds(IntervalValue, IntervalUnit);

    public static double ToMilliseconds(double value, IntervalUnit unit) => unit switch
    {
        IntervalUnit.Seconds => value * 1000,
        IntervalUnit.Minutes => value * 60_000,
        IntervalUnit.Hours => value * 3_600_000,
        _ => value,
    };

    public LoopAction Clone() => (LoopAction)MemberwiseClone();
}

public enum FloatingButtonSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Round button shown in overlay mode that starts and stops one loop or macro.</summary>
public sealed class FloatingButton
{
    public const int MaxLabelLength = 2;

    public bool Enabled { get; set; }

    /// <summary>One or two characters shown in the button.</summary>
    public string Label { get; set; } = "";

    public FloatingButtonSize Size { get; set; } = FloatingButtonSize.Medium;

    /// <summary>Top-left of the button (WPF units, like the overlay panel). Null = centered on the main screen.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }

    public const int DefaultOpacity = 85;

    /// <summary>
    /// Background opacity of the button, in percent. Null only in files written before schema 6,
    /// until the first start fills it with the overlay panel's opacity; then null means
    /// <see cref="DefaultOpacity"/>.
    /// </summary>
    public int? Opacity { get; set; }

    [JsonIgnore]
    public int EffectiveOpacity => Opacity ?? DefaultOpacity;

    /// <summary>Diameter in WPF units.</summary>
    [JsonIgnore]
    public double Diameter => DiameterOf(Size);

    public static double DiameterOf(FloatingButtonSize size) => size switch
    {
        FloatingButtonSize.Small => 36,
        FloatingButtonSize.Large => 60,
        _ => 48,
    };

    /// <summary>
    /// Label proposed for a name: the first letters of its first two words ("Click every
    /// second" → "CE"), or the first two letters of a single word ("Mining" → "MI").
    /// </summary>
    public static string DefaultLabel(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in name ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            words.Add(current.ToString());

        string label = words.Count switch
        {
            0 => "",
            1 => words[0].Length > MaxLabelLength ? words[0][..MaxLabelLength] : words[0],
            _ => $"{words[0][0]}{words[1][0]}",
        };
        return label.ToUpperInvariant();
    }

    /// <summary>Trims the label to <see cref="MaxLabelLength"/> characters and drops invalid values.</summary>
    public void Sanitize()
    {
        Label = (Label ?? "").Trim();
        if (Label.Length > MaxLabelLength)
            Label = Label[..MaxLabelLength];
        if (!Enum.IsDefined(Size))
            Size = FloatingButtonSize.Medium;
        if (X is double x && (double.IsNaN(x) || double.IsInfinity(x)))
            X = null;
        if (Y is double y && (double.IsNaN(y) || double.IsInfinity(y)))
            Y = null;
        if (X == null || Y == null)
            X = Y = null;
        if (Opacity is int opacity)
            Opacity = Math.Clamp(opacity, AppSettings.MinOpacity, AppSettings.MaxOpacity);
    }

    public FloatingButton Clone() => (FloatingButton)MemberwiseClone();
}

/// <summary>One user-defined loop.</summary>
public sealed class LoopDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<LoopAction> Actions { get; set; } = new();
    public ActivationMode Mode { get; set; } = ActivationMode.Toggle;
    public HotkeyBinding? Hotkey { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Plays the chosen sound's "in" version on start and "out" version on stop.</summary>
    public bool SoundEnabled { get; set; }

    /// <summary>One of <c>SoundService.Names</c>; kept even while sounds are off.</summary>
    public string SoundName { get; set; } = "Chime";

    public FloatingButton FloatingButton { get; set; } = new();

    /// <summary>Executable name (e.g. "Diablo IV.exe") it works in; null = all apps (<see cref="AppScope"/>).</summary>
    public string? AppExe { get; set; }

    /// <summary>Shown in overlay mode: its row in the overlay panel or its floating button.</summary>
    public bool ShowInOverlay { get; set; } = true;

    /// <summary>Left out of the overlay while it is disabled (otherwise shown as disabled).</summary>
    public bool HideInOverlayWhenDisabled { get; set; }

    /// <summary>A click on it in the overlay also reaches the window under it.</summary>
    public bool OverlayClickPassesThrough { get; set; }

    // ---- v1.0 fields, read once and converted by SettingsStore ----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeyVk { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IntervalMs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HotkeyVk { get; set; }

    public LoopDefinition Clone()
    {
        var copy = (LoopDefinition)MemberwiseClone();
        copy.Actions = Actions.ConvertAll(a => a.Clone());
        copy.Hotkey = Hotkey?.Clone();
        copy.FloatingButton = FloatingButton.Clone();
        return copy;
    }

    public void CopyFrom(LoopDefinition other)
    {
        Name = other.Name;
        Actions = other.Actions.ConvertAll(a => a.Clone());
        Mode = other.Mode;
        Hotkey = other.Hotkey?.Clone();
        Enabled = other.Enabled;
        SoundEnabled = other.SoundEnabled;
        SoundName = other.SoundName;
        FloatingButton = other.FloatingButton.Clone();
        AppExe = other.AppExe;
        ShowInOverlay = other.ShowInOverlay;
        HideInOverlayWhenDisabled = other.HideInOverlayWhenDisabled;
        OverlayClickPassesThrough = other.OverlayClickPassesThrough;
    }
}

/// <summary>Everything persisted in settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 7;
    public const int DefaultRecordVk = 0x77;    // F8

    /// <summary>Stop all for new installs: Alt+Shift+S. Existing files keep their hotkey.</summary>
    public static HotkeyBinding DefaultStopAllHotkey() => new() { Vk = 0x53, Alt = true, Shift = true };

    /// <summary>Overlay mode for new installs: Alt+Shift+W. Existing files keep their hotkey.</summary>
    public static HotkeyBinding DefaultOverlayModeHotkey() => new() { Vk = 0x57, Alt = true, Shift = true };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Schema version of the file as it was read, before <see cref="SchemaVersion"/> is updated. Not saved.</summary>
    [JsonIgnore]
    public int LoadedSchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Dark by default, even when Windows uses the light theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public HotkeyBinding? StopAllHotkey { get; set; }
    public HotkeyBinding? OverlayModeHotkey { get; set; }
    public HotkeyBinding? RecordHotkey { get; set; }

    /// <summary>Order of the macros (files in the macros folder).</summary>
    public List<Guid> MacroOrder { get; set; } = new();

    /// <summary>Loops and macros in the overlay panel can be clicked to start or stop them.</summary>
    public bool ClickItemsInPanel { get; set; } = true;

    /// <summary>Last choice of the "Record mouse movement" checkbox.</summary>
    public bool RecordMouseMovement { get; set; } = true;

    /// <summary>Closing the window keeps PuppyMacro running in the system tray.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Start in the system tray when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Check GitHub for a new version at startup and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The "still running in the system tray" notification was shown once.</summary>
    public bool TrayNoticeShown { get; set; }

    public List<RemapDefinition> Remaps { get; set; } = new();
    public LoopFilter Filter { get; set; } = LoopFilter.All;

    /// <summary>Opacity range of the overlay panel and of the floating buttons, in percent.</summary>
    public const int MinOpacity = 20;
    public const int MaxOpacity = 100;

    /// <summary>Overlay mode shows the overlay panel. Off: only the floating buttons.</summary>
    public bool ShowOverlayPanel { get; set; } = true;

    /// <summary>Background opacity of the overlay panel, in percent.</summary>
    public int OverlayPanelOpacity { get; set; } = 85;

    /// <summary>Overlay panel position (WPF units, from the main screen's top-left). Null = default.</summary>
    public double? OverlayPanelX { get; set; }
    public double? OverlayPanelY { get; set; }

    /// <summary>Size of the macro editor (WPF units), kept between openings. Null = default size.</summary>
    public double? MacroEditorWidth { get; set; }
    public double? MacroEditorHeight { get; set; }

    /// <summary>Volume of loop sounds, in percent.</summary>
    public int SoundVolume { get; set; } = 70;

    /// <summary>Settings groups left open by the user.</summary>
    public List<string> ExpandedSettingsGroups { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>Size of the main window (WPF units), kept between sessions. Null = default size.</summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public List<LoopDefinition> Loops { get; set; } = new();

    // ---- v1.0 fields, read once and converted by SettingsStore ----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StopAllHotkeyVk { get; set; }

    [JsonPropertyName("GameModeHotkeyVk")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyOverlayModeHotkeyVk { get; set; }

    // ---- Names used before schema 6, read once and converted by SettingsStore ----
    [JsonPropertyName("GameModeHotkey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HotkeyBinding? LegacyOverlayModeHotkey { get; set; }

    [JsonPropertyName("GameModeOpacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyOverlayPanelOpacity { get; set; }

    [JsonPropertyName("GameModeX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyOverlayPanelX { get; set; }

    [JsonPropertyName("GameModeY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyOverlayPanelY { get; set; }

    /// <summary>First-run settings: a single demo loop.</summary>
    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            StopAllHotkey = DefaultStopAllHotkey(),
            OverlayModeHotkey = DefaultOverlayModeHotkey(),
            RecordHotkey = HotkeyBinding.FromKey(DefaultRecordVk),
        };
        settings.Loops.Add(new LoopDefinition
        {
            Name = "Left click spam",
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 40 } },
            Mode = ActivationMode.Toggle,
            Hotkey = HotkeyBinding.FromKey(0x78), // F9
            Enabled = true,
        });
        return settings;
    }
}

/// <summary>When <see cref="Source"/> is pressed, <see cref="Target"/> is sent instead.</summary>
public sealed class RemapDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Key, mouse button or combination pressed by the user, or a modifier key alone (by its side).</summary>
    public HotkeyBinding? Source { get; set; }

    /// <summary>Key, mouse button or combination sent instead, or a modifier key alone (by its side).</summary>
    public HotkeyBinding? Target { get; set; }

    /// <summary>Executable name (e.g. "Diablo IV.exe"); null = all apps (<see cref="AppScope"/>).</summary>
    public string? AppExe { get; set; }

    public string Note { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // ---- Before schema 7 the source was a single key; read once and converted by SettingsStore ----
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceVk { get; set; }

    public RemapDefinition Clone()
    {
        var copy = (RemapDefinition)MemberwiseClone();
        copy.Source = Source?.Clone();
        copy.Target = Target?.Clone();
        return copy;
    }

    public void CopyFrom(RemapDefinition other)
    {
        Source = other.Source?.Clone();
        Target = other.Target?.Clone();
        AppExe = other.AppExe;
        Note = other.Note;
        Enabled = other.Enabled;
    }
}

/// <summary>
/// The app a loop, macro or remap works in (<c>AppExe</c>): an executable file name like "Diablo IV.exe",
/// compared without case; null = all apps.
/// </summary>
public static class AppScope
{
    /// <summary>The saved form: trimmed, null when empty.</summary>
    public static string? Normalize(string? appExe) =>
        string.IsNullOrWhiteSpace(appExe) ? null : appExe.Trim();

    /// <summary>True when an item for <paramref name="appExe"/> works while <paramref name="foregroundExe"/> is in front.</summary>
    public static bool Allows(string? appExe, string? foregroundExe) =>
        appExe == null || Same(appExe, foregroundExe);

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
