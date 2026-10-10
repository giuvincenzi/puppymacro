using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string SettingsPath => _folder.File("settings.json");

    private AppSettings LoadJson(string json, out string? warning)
    {
        File.WriteAllText(SettingsPath, json);
        return new SettingsStore(SettingsPath).Load(out warning);
    }

    [Fact]
    public void First_start_creates_the_default_settings_file()
    {
        AppSettings settings = new SettingsStore(SettingsPath).Load(out string? warning);

        Assert.Null(warning);
        Assert.True(File.Exists(SettingsPath));
        Assert.True(settings.StopAllHotkey!.SameAs(new HotkeyBinding { Vk = 0x53, Alt = true, Shift = true }));      // Alt+Shift+S
        Assert.True(settings.OverlayModeHotkey!.SameAs(new HotkeyBinding { Vk = 0x57, Alt = true, Shift = true }));  // Alt+Shift+W
        Assert.Equal(AppSettings.DefaultRecordVk, settings.RecordHotkey!.Vk);
        Assert.True(settings.ShowOverlayPanel);
        Assert.Single(settings.Loops);
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings settings = AppSettings.CreateDefault();
        settings.Theme = AppTheme.Light;
        settings.SoundVolume = 35;
        settings.CheckForUpdates = false;
        settings.Remaps.Add(new RemapDefinition { SourceVk = 0x14, Target = HotkeyBinding.FromKey(0x1B), AppExe = "app.exe" });

        Assert.True(store.TrySave(settings, out _));
        AppSettings loaded = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(35, loaded.SoundVolume);
        Assert.False(loaded.CheckForUpdates);
        RemapDefinition remap = Assert.Single(loaded.Remaps);
        Assert.Equal(0x14, remap.SourceVk);
        Assert.Equal(0x1B, remap.Target!.Vk);
        Assert.Equal("app.exe", remap.AppExe);
    }

    [Fact]
    public void Version_1_0_files_are_converted()
    {
        AppSettings settings = LoadJson("""
            {
              "StopAllHotkeyVk": 121,
              "GameModeHotkeyVk": 122,
              "Loops": [ { "Name": "Old loop", "KeyVk": 1, "IntervalMs": 50, "HotkeyVk": 120 } ]
            }
            """, out _);

        Assert.Equal(121, settings.StopAllHotkey!.Vk);
        Assert.Equal(122, settings.OverlayModeHotkey!.Vk);
        Assert.Null(settings.StopAllHotkeyVk);
        Assert.Null(settings.LegacyOverlayModeHotkeyVk);
        LoopDefinition loop = Assert.Single(settings.Loops);
        LoopAction action = Assert.Single(loop.Actions);
        Assert.Equal(ActionType.Key, action.Type);
        Assert.Equal(1, action.KeyVk);
        Assert.Equal(50, action.IntervalMs);
        Assert.Equal(120, loop.Hotkey!.Vk);
        Assert.Null(loop.KeyVk);
        Assert.Null(loop.IntervalMs);
        Assert.Null(loop.HotkeyVk);
    }

    [Fact]
    public void Loops_without_an_app_work_in_all_apps_and_an_empty_app_means_all_apps()
    {
        AppSettings settings = LoadJson("""
            {
              "Loops": [
                { "Name": "Old loop", "Actions": [ { "Type": "Key", "KeyVk": 1 } ] },
                { "Name": "Empty app", "Actions": [ { "Type": "Key", "KeyVk": 1 } ], "AppExe": " " },
                { "Name": "Game", "Actions": [ { "Type": "Key", "KeyVk": 1 } ], "AppExe": " Game.exe " }
              ]
            }
            """, out _);

        Assert.Null(settings.Loops[0].AppExe);
        Assert.Null(settings.Loops[1].AppExe);
        Assert.Equal("Game.exe", settings.Loops[2].AppExe);
    }

    [Fact]
    public void Files_before_schema_3_turn_on_clicking_in_the_overlay_panel()
    {
        AppSettings settings = LoadJson("""{ "SchemaVersion": 2, "ClickItemsInPanel": false }""", out _);

        Assert.True(settings.ClickItemsInPanel);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Newer_files_keep_clicking_in_the_overlay_panel_off()
    {
        AppSettings settings = LoadJson($$"""{ "SchemaVersion": {{AppSettings.CurrentSchemaVersion}}, "ClickItemsInPanel": false }""", out _);

        Assert.False(settings.ClickItemsInPanel);
    }

    [Fact]
    public void Files_without_the_updates_setting_check_for_updates()
    {
        AppSettings settings = LoadJson("""{ "SchemaVersion": 4 }""", out _);

        Assert.True(settings.CheckForUpdates);
    }

    [Fact]
    public void The_macro_editor_size_is_kept_and_invalid_sizes_are_dropped()
    {
        AppSettings kept = LoadJson("""{ "MacroEditorWidth": 800, "MacroEditorHeight": 1000 }""", out _);
        AppSettings dropped = LoadJson("""{ "MacroEditorWidth": -5, "MacroEditorHeight": 0 }""", out _);
        AppSettings missing = LoadJson("""{ "SchemaVersion": 4 }""", out _);

        Assert.Equal(800, kept.MacroEditorWidth);
        Assert.Equal(1000, kept.MacroEditorHeight);
        Assert.Null(dropped.MacroEditorWidth);
        Assert.Null(dropped.MacroEditorHeight);
        Assert.Null(missing.MacroEditorWidth);
    }

    [Fact]
    public void The_main_window_size_is_kept_and_invalid_sizes_are_dropped()
    {
        AppSettings kept = LoadJson("""{ "WindowWidth": 1600, "WindowHeight": 1000 }""", out _);
        AppSettings dropped = LoadJson("""{ "WindowWidth": -1, "WindowHeight": 1000 }""", out _);
        AppSettings half = LoadJson("""{ "WindowWidth": 1600 }""", out _);

        Assert.Equal(1600, kept.WindowWidth);
        Assert.Equal(1000, kept.WindowHeight);
        Assert.Null(dropped.WindowWidth);
        Assert.Null(dropped.WindowHeight);
        Assert.Null(half.WindowWidth);
    }

    [Fact]
    public void Files_before_schema_5_drop_the_width_of_the_one_column_macro_editor()
    {
        AppSettings settings = LoadJson("""{ "SchemaVersion": 4, "MacroEditorWidth": 720, "MacroEditorHeight": 1000 }""", out _);

        Assert.Null(settings.MacroEditorWidth);
        Assert.Equal(1000, settings.MacroEditorHeight);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        AppSettings settings = LoadJson("""
            {
              "OverlayPanelOpacity": 5,
              "SoundVolume": 150,
              "Loops": [ { "Name": "Fast", "Actions": [ { "KeyVk": 65, "IntervalValue": 1, "IntervalUnit": "Milliseconds" } ] } ]
            }
            """, out _);

        Assert.Equal(AppSettings.MinOpacity, settings.OverlayPanelOpacity);
        Assert.Equal(100, settings.SoundVolume);
        Assert.Equal(LoopAction.MinIntervalMs, settings.Loops[0].Actions[0].IntervalMs);
    }

    [Fact]
    public void Invalid_remaps_are_removed_and_blank_app_names_mean_all_apps()
    {
        AppSettings settings = LoadJson("""
            {
              "Remaps": [
                { "SourceVk": 0, "Target": { "Vk": 27 } },
                { "SourceVk": 20 },
                { "SourceVk": 20, "Target": { "Vk": 27 }, "AppExe": "  " }
              ]
            }
            """, out _);

        RemapDefinition remap = Assert.Single(settings.Remaps);
        Assert.Null(remap.AppExe);
        Assert.NotEqual(Guid.Empty, remap.Id);
    }

    [Fact]
    public void Loops_get_a_name_and_a_known_sound()
    {
        AppSettings settings = LoadJson("""{ "Loops": [ { "Name": "   ", "SoundName": "Missing" } ] }""", out _);

        LoopDefinition loop = Assert.Single(settings.Loops);
        Assert.Equal("Loop", loop.Name);
        Assert.Equal(SoundService.Names[0], loop.SoundName);
        Assert.NotEqual(Guid.Empty, loop.Id);
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_replaced_by_defaults()
    {
        AppSettings settings = LoadJson("{ not json", out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("settings.invalid.json", warning);
        Assert.Equal("{ not json", File.ReadAllText(_folder.File("settings.invalid.json")));
        Assert.Equal(0x53, settings.StopAllHotkey!.Vk);
        Assert.Single(settings.Loops);
    }

    [Fact]
    public void Files_before_schema_6_move_the_old_settings_names_to_the_overlay()
    {
        AppSettings settings = LoadJson("""
            {
              "SchemaVersion": 5,
              "StopAllHotkey": { "Vk": 121 },
              "GameModeHotkey": { "Vk": 122, "Ctrl": true },
              "GameModeOpacity": 60,
              "GameModeX": 300,
              "GameModeY": 40,
              "ExpandedSettingsGroups": [ "Hotkeys", "GameModePanel" ],
              "Loops": [
                { "Name": "With button", "FloatingButton": { "Enabled": true, "Label": "WB" } },
                { "Name": "Without button" }
              ]
            }
            """, out _);

        // Existing users keep their hotkeys: F10 and Ctrl+F11, not the new defaults.
        Assert.True(settings.StopAllHotkey!.SameAs(HotkeyBinding.FromKey(121)));
        Assert.True(settings.OverlayModeHotkey!.SameAs(new HotkeyBinding { Vk = 122, Ctrl = true }));
        Assert.Equal(60, settings.OverlayPanelOpacity);
        Assert.Equal(300, settings.OverlayPanelX);
        Assert.Equal(40, settings.OverlayPanelY);
        Assert.True(settings.ShowOverlayPanel);
        Assert.Equal(new[] { "Hotkeys", "Overlay" }, settings.ExpandedSettingsGroups);
        // Floating buttons keep the opacity they had: the panel's.
        Assert.All(settings.Loops, loop => Assert.Equal(60, loop.FloatingButton.Opacity));
        Assert.Equal(5, settings.LoadedSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);

        string saved = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("GameMode", saved);
        Assert.Contains("\"OverlayModeHotkey\"", saved);
    }

    [Fact]
    public void Current_files_keep_floating_buttons_without_opacity_at_the_default()
    {
        AppSettings settings = LoadJson($$"""
            {
              "SchemaVersion": {{AppSettings.CurrentSchemaVersion}},
              "OverlayPanelOpacity": 40,
              "ShowOverlayPanel": false,
              "Loops": [ { "Name": "New", "FloatingButton": { "Enabled": true } } ]
            }
            """, out _);

        Assert.False(settings.ShowOverlayPanel);
        Assert.Null(settings.Loops[0].FloatingButton.Opacity);
        Assert.Equal(FloatingButton.DefaultOpacity, settings.Loops[0].FloatingButton.EffectiveOpacity);
    }

    [Fact]
    public void Floating_button_opacity_is_clamped()
    {
        AppSettings settings = LoadJson("""
            { "Loops": [ { "Name": "A", "FloatingButton": { "Opacity": 3 } }, { "Name": "B", "FloatingButton": { "Opacity": 300 } } ] }
            """, out _);

        Assert.Equal(AppSettings.MinOpacity, settings.Loops[0].FloatingButton.Opacity);
        Assert.Equal(AppSettings.MaxOpacity, settings.Loops[1].FloatingButton.Opacity);
    }
}
