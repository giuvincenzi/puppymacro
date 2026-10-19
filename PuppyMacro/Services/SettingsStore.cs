using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>Reads and writes settings.json (the path given, AppPaths.SettingsFile in the data folder).</summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    public string FilePath => _path;

    /// <summary>How long Load tries again to read a file another program holds open (an antivirus scan, a backup tool).</summary>
    internal static TimeSpan ReadRetry { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// settings.json was there but could not be read at start (another program held it): the app runs on the defaults and
    /// nothing is saved, so the user's file is never replaced by them.
    /// </summary>
    public bool Unread { get; private set; }

    /// <summary>Loads settings. On first run or invalid content, returns defaults and sets <paramref name="warning"/> if needed.</summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;

        if (!File.Exists(_path))
        {
            var defaults = AppSettings.CreateDefault();
            TrySave(defaults, out _);
            return defaults;
        }

        // A file that can't be opened is not invalid content: tried again for a while, then left as it is.
        string? json = null;
        Exception? readError = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (json == null)
        {
            try
            {
                json = File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readError = ex;
                if (clock.Elapsed >= ReadRetry)
                    break;
                System.Threading.Thread.Sleep(200);
            }
        }
        if (json == null)
        {
            Unread = true;
            // Read before the language is set (it is a setting): the defaults follow Windows' language, as this text does.
            warning = string.Format(CultureInfo.CurrentCulture, Strings.App_SettingsUnread, readError!.Message);
            return AppSettings.CreateDefault();
        }

        try
        {
            // A null where none can be (a list's entry) is left out, never a crash (data.md).
            var settings = CodeJson.ReadFile<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
            // Version 1.0 wrote no SchemaVersion: such a file is of the first schema, not of the current one (the property's
            // default), so every migration since runs on it.
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty(nameof(AppSettings.SchemaVersion), out _))
                    settings.SchemaVersion = 1;
            }
            Sanitize(settings);
            TrySave(settings, out _); // writes v1.0 files back in the current format
            return settings;
        }
        catch (JsonException ex)
        {
            string backup = Path.ChangeExtension(_path, ".invalid.json");
            try { File.Copy(_path, backup, overwrite: true); } catch { /* best effort */ }
            // Read before the language is set (it is a setting): the defaults follow Windows' language, as this text does.
            warning = string.Format(CultureInfo.CurrentCulture, Strings.App_SettingsReset, Path.GetFileName(backup), ex.Message);
            var defaults = AppSettings.CreateDefault();
            TrySave(defaults, out _);
            return defaults;
        }
    }

    /// <summary>
    /// The settings as the file has them, never written (a backup opened from File Explorer: another PuppyMacro may be running
    /// and own the file); a missing or unreadable file gives the defaults.
    /// </summary>
    public AppSettings Peek()
    {
        try
        {
            if (File.Exists(_path) && CodeJson.ReadFile<AppSettings>(File.ReadAllText(_path), JsonOptions) is { } settings)
            {
                Sanitize(settings);
                return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return AppSettings.CreateDefault();
    }

    public bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
        if (Unread)
        {
            error = Strings.Settings_NotSavedUnread;
            return false;
        }
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Fixes invalid values and converts v1.0 files to the current format.</summary>
    private static void Sanitize(AppSettings settings)
    {
        settings.Loops ??= new();
        settings.LoadedSchemaVersion = settings.SchemaVersion;

        // Schema 6: "game mode" became "overlay mode"; the settings were saved under their old names.
        if (settings.LegacyOverlayModeHotkey is { IsSet: true } && settings.OverlayModeHotkey is not { IsSet: true })
            settings.OverlayModeHotkey = settings.LegacyOverlayModeHotkey;
        if (settings.LegacyOverlayPanelOpacity is int legacyOpacity)
            settings.OverlayPanelOpacity = legacyOpacity;
        if (settings.LegacyOverlayPanelX is double legacyX && settings.LegacyOverlayPanelY is double legacyY)
        {
            settings.OverlayPanelX = legacyX;
            settings.OverlayPanelY = legacyY;
        }
        settings.LegacyOverlayModeHotkey = null;
        settings.LegacyOverlayPanelOpacity = null;
        settings.LegacyOverlayPanelX = null;
        settings.LegacyOverlayPanelY = null;

        // v1.0 stored global hotkeys as plain key codes.
        if (settings.StopAllHotkey == null || !settings.StopAllHotkey.IsSet)
            settings.StopAllHotkey = settings.StopAllHotkeyVk is > 0 and int stopVk
                ? HotkeyBinding.FromKey(stopVk) : AppSettings.DefaultStopAllHotkey();
        if (settings.OverlayModeHotkey == null || !settings.OverlayModeHotkey.IsSet)
            settings.OverlayModeHotkey = settings.LegacyOverlayModeHotkeyVk is > 0 and int legacyVk
                ? HotkeyBinding.FromKey(legacyVk) : AppSettings.DefaultOverlayModeHotkey();
        if (settings.RecordHotkey == null || !settings.RecordHotkey.IsSet)
            settings.RecordHotkey = HotkeyBinding.FromKey(AppSettings.DefaultRecordVk);
        settings.MacroOrder ??= new();
        if (settings.MacroEditorWidth is not (> 0 and < 100_000))
            settings.MacroEditorWidth = null;
        if (settings.MacroEditorHeight is not (> 0 and < 100_000))
            settings.MacroEditorHeight = null;
        if (settings.WindowWidth is not (> 0 and < 100_000) || settings.WindowHeight is not (> 0 and < 100_000))
            settings.WindowWidth = settings.WindowHeight = null;
        settings.StopAllHotkeyVk = null;
        settings.LegacyOverlayModeHotkeyVk = null;

        foreach (var loop in settings.Loops)
        {
            if (loop.Id == Guid.Empty)
                loop.Id = Guid.NewGuid();
            loop.Name = string.IsNullOrWhiteSpace(loop.Name) ? "Loop" : loop.Name.Trim();
            loop.Actions ??= new();

            // v1.0: one key and one interval per loop.
            if (loop.Actions.Count == 0 && loop.KeyVk is > 0 and int keyVk)
            {
                loop.Actions.Add(new LoopAction
                {
                    Type = ActionType.Key,
                    KeyVk = keyVk,
                    IntervalValue = loop.IntervalMs ?? 40,
                    IntervalUnit = IntervalUnit.Milliseconds,
                });
            }
            if ((loop.Hotkey == null || !loop.Hotkey.IsSet) && loop.HotkeyVk is > 0 and int hotkeyVk)
                loop.Hotkey = HotkeyBinding.FromKey(hotkeyVk);
            loop.KeyVk = null;
            loop.IntervalMs = null;
            loop.HotkeyVk = null;
            loop.FloatingButton ??= new();
            loop.FloatingButton.Sanitize();
            loop.AppExe = AppScope.Normalize(loop.AppExe);

            foreach (var action in loop.Actions)
            {
                action.Text ??= "";
                action.Normalize();
                double ms = action.IntervalMs;
                if (double.IsNaN(ms) || ms < LoopAction.MinIntervalMs || ms > LoopAction.MaxIntervalMs)
                {
                    action.IntervalValue = Math.Clamp(double.IsNaN(ms) ? 40 : ms, LoopAction.MinIntervalMs, LoopAction.MaxIntervalMs);
                    action.IntervalUnit = IntervalUnit.Milliseconds;
                }
            }
        }

        settings.OverlayPanelOpacity = Math.Clamp(settings.OverlayPanelOpacity,
            AppSettings.MinOpacity, AppSettings.MaxOpacity);

        // v1.5: clicking items in the overlay panel is on by default; turn it on once for older files.
        if (settings.SchemaVersion < 3)
            settings.ClickItemsInPanel = true;

        // Schema 5: the macro editor has two columns and opens wider; a width kept for the one-column editor is dropped.
        if (settings.SchemaVersion < 5)
            settings.MacroEditorWidth = null;

        // Schema 6: each floating button has its own opacity; existing ones keep the panel's,
        // which they used before. Macro files are filled by MacroLibrary.FillButtonOpacity.
        if (settings.SchemaVersion < 6)
        {
            foreach (var loop in settings.Loops)
                loop.FloatingButton.Opacity ??= settings.OverlayPanelOpacity;
        }

        // Schema 7: the macro editor opens 240 wider, 120 for its right column and 120 for the list of actions; a
        // width kept from before grows as much.
        if (settings.SchemaVersion < 7 && settings.MacroEditorWidth is double macroWidth)
            settings.MacroEditorWidth = macroWidth + 240;

        settings.SoundVolume = Math.Clamp(settings.SoundVolume, 0, 100);
        // A language this version is not translated into (a newer version's, or written by hand) follows Windows.
        settings.Language = AppLanguages.Normalize(settings.Language);
        // A list view this version does not have (a newer version's) gives that page's default view.
        var defaults = new AppSettings();
        if (!Enum.IsDefined(settings.LoopsLayout))
            settings.LoopsLayout = defaults.LoopsLayout;
        if (!Enum.IsDefined(settings.MacrosLayout))
            settings.MacrosLayout = defaults.MacrosLayout;
        if (!Enum.IsDefined(settings.RemapsLayout))
            settings.RemapsLayout = defaults.RemapsLayout;
        settings.Remaps ??= new();
        // Schema 7: a remap's source is a key or a combination (Source); the single key of older files
        // (SourceVk) becomes a Source with no modifiers. Hotkeys without sides take either side (ModifierSide.Any).
        foreach (var remap in settings.Remaps)
        {
            if (remap.SourceVk is int sourceVk)
                remap.Source ??= HotkeyBinding.FromKey(sourceVk);
            remap.SourceVk = null;
        }
        // A remap without a hotkey comes only from Import (its hotkey was taken): it stays, always off, until the user
        // gives it one in the editor.
        settings.Remaps.RemoveAll(r => r.Target is not { IsSet: true });
        foreach (var remap in settings.Remaps)
        {
            if (remap.Source is not { IsSet: true })
            {
                remap.Source = null;
                remap.Enabled = false;
            }
            if (remap.Id == Guid.Empty)
                remap.Id = Guid.NewGuid();
            remap.Note ??= "";
            remap.AppExe = AppScope.Normalize(remap.AppExe);
        }
        settings.ExpandedSettingsGroups ??= new();
        if (settings.ExpandedSettingsGroups.Remove("GameModePanel"))
            settings.ExpandedSettingsGroups.Add("Overlay");
        foreach (var loop in settings.Loops)
        {
            if (!SoundService.Names.Contains(loop.SoundName))
                loop.SoundName = SoundService.Names[0];
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    /// <summary>
    /// Schema 8 added the Open PuppyMacro hotkey: a file from before (<see cref="AppSettings.LoadedSchemaVersion"/> &lt; 8)
    /// gets <see cref="AppSettings.DefaultOpenMainHotkey"/> unless a loop, macro, remap or other global hotkey already uses
    /// it (global hotkeys come first, so it would take their key away): then it has none, and the user chooses one in
    /// Settings. Called once the macros are loaded; true when it changed the settings (save them).
    /// </summary>
    public static bool AddOpenMainHotkey(AppSettings settings, MacroLibrary macros)
    {
        if (settings.LoadedSchemaVersion >= 8 || settings.OpenMainHotkey is { IsSet: true })
            return false;
        HotkeyBinding binding = AppSettings.DefaultOpenMainHotkey();
        bool taken = HotkeyConflicts.Find(binding, settings, macros, ignoreGlobal: "OpenMain") != null
            || settings.Remaps.Any(r => binding.Overlaps(r.Source));
        if (taken)
            return false;
        settings.OpenMainHotkey = binding;
        return true;
    }
}
