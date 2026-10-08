using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>Reads and writes settings.json next to the exe.</summary>
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

        try
        {
            string json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
            Sanitize(settings);
            TrySave(settings, out _); // writes v1.0 files back in the current format
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            string backup = Path.ChangeExtension(_path, ".invalid.json");
            try { File.Copy(_path, backup, overwrite: true); } catch { /* best effort */ }
            warning = $"settings.json could not be read and was reset to defaults.\nA copy was saved as {Path.GetFileName(backup)}.\n\nDetails: {ex.Message}";
            var defaults = AppSettings.CreateDefault();
            TrySave(defaults, out _);
            return defaults;
        }
    }

    public bool TrySave(AppSettings settings, out string? error)
    {
        error = null;
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

            foreach (var action in loop.Actions)
            {
                action.Text ??= "";
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

        settings.SoundVolume = Math.Clamp(settings.SoundVolume, 0, 100);
        settings.Remaps ??= new();
        settings.Remaps.RemoveAll(r => r.SourceVk == 0 || r.Target is not { IsSet: true });
        foreach (var remap in settings.Remaps)
        {
            if (remap.Id == Guid.Empty)
                remap.Id = Guid.NewGuid();
            remap.Note ??= "";
            if (string.IsNullOrWhiteSpace(remap.AppExe))
                remap.AppExe = null;
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
}
