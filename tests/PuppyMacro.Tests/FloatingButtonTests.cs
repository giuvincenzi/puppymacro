using System;
using System.IO;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class FloatingButtonTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    // show, hide when disabled, enabled -> shown in the overlay
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, true)]   // disabled: shown as disabled
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]   // hidden while disabled
    [InlineData(false, false, true, false)]  // Show in overlay off
    public void The_overlay_shows_an_item_by_its_options(bool show, bool hideWhenDisabled, bool enabled, bool shown)
    {
        var item = new PuppyMacro.Views.LoopItemViewModel(new LoopDefinition
        {
            Name = "Spam",
            Enabled = enabled,
            ShowInOverlay = show,
            HideInOverlayWhenDisabled = hideWhenDisabled,
            FloatingButton = { Enabled = true },
        });

        Assert.Equal(shown, PuppyMacro.Views.OverlayItems.IsShown(item));
        // Its floating button follows (Toggle only).
        Assert.Equal(shown, PuppyMacro.Views.FloatingButtons.HasButton(item));
    }

    [Theory]
    [InlineData("Click every second", "CE")]
    [InlineData("Sample: hold W", "SH")]
    [InlineData("Mining", "MI")]
    [InlineData("w", "W")]
    [InlineData("  ", "")]
    public void The_default_label_comes_from_the_name(string name, string expected)
    {
        Assert.Equal(expected, FloatingButton.DefaultLabel(name));
    }

    [Theory]
    [InlineData(FloatingButtonSize.Small, 36)]
    [InlineData(FloatingButtonSize.Medium, 48)]
    [InlineData(FloatingButtonSize.Large, 60)]
    public void Each_size_has_its_diameter(FloatingButtonSize size, double diameter)
    {
        Assert.Equal(diameter, new FloatingButton { Size = size }.Diameter);
    }

    [Fact]
    public void Sanitize_trims_the_label_and_drops_invalid_values()
    {
        var button = new FloatingButton { Label = " ABC ", Size = (FloatingButtonSize)9, X = double.NaN, Y = 10 };

        button.Sanitize();

        Assert.Equal("AB", button.Label);
        Assert.Equal(FloatingButtonSize.Medium, button.Size);
        Assert.Null(button.X);
        Assert.Null(button.Y);
    }

    [Fact]
    public void Clone_and_CopyFrom_keep_the_floating_button_separate()
    {
        var loop = new LoopDefinition { FloatingButton = { Enabled = true, Label = "CL", Size = FloatingButtonSize.Large, X = 640, Y = 448 } };

        LoopDefinition copy = loop.Clone();
        copy.FloatingButton.X = 1;
        var target = new LoopDefinition();
        target.CopyFrom(loop);

        Assert.Equal(640, loop.FloatingButton.X);
        Assert.NotSame(loop.FloatingButton, target.FloatingButton);
        Assert.True(target.FloatingButton.Enabled);
        Assert.Equal("CL", target.FloatingButton.Label);
        Assert.Equal(FloatingButtonSize.Large, target.FloatingButton.Size);
        Assert.Equal(448, target.FloatingButton.Y);

        var macro = new MacroDefinition { FloatingButton = { Enabled = true, Label = "TC" } };
        var macroTarget = new MacroDefinition();
        macroTarget.CopyFrom(macro.Clone());
        Assert.Equal("TC", macroTarget.FloatingButton.Label);
        Assert.NotSame(macro.FloatingButton, macroTarget.FloatingButton);
    }

    [Fact]
    public void A_settings_file_from_before_floating_buttons_loads_with_them_off()
    {
        string path = _folder.File("settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 4,
              "Loops": [ { "Name": "Old loop", "Actions": [ { "Type": "Key", "KeyVk": 1 } ] } ]
            }
            """);

        AppSettings settings = new SettingsStore(path).Load(out string? warning);

        Assert.Null(warning);
        LoopDefinition loop = Assert.Single(settings.Loops);
        Assert.NotNull(loop.FloatingButton);
        Assert.False(loop.FloatingButton.Enabled);
        Assert.Equal(FloatingButtonSize.Medium, loop.FloatingButton.Size);
        Assert.Null(loop.FloatingButton.X);
    }

    [Fact]
    public void A_null_floating_button_in_settings_is_replaced()
    {
        string path = _folder.File("settings.json");
        File.WriteAllText(path, """{ "Loops": [ { "Name": "Loop", "FloatingButton": null } ] }""");

        AppSettings settings = new SettingsStore(path).Load(out _);

        Assert.False(Assert.Single(settings.Loops).FloatingButton.Enabled);
    }

    [Fact]
    public void Floating_button_settings_are_saved_and_loaded_back()
    {
        string path = _folder.File("settings.json");
        var store = new SettingsStore(path);
        AppSettings settings = AppSettings.CreateDefault();
        settings.Loops[0].FloatingButton = new FloatingButton { Enabled = true, Label = "LC", Size = FloatingButtonSize.Small, X = 100, Y = 200 };

        Assert.True(store.TrySave(settings, out _));
        FloatingButton loaded = store.Load(out _).Loops[0].FloatingButton;

        Assert.True(loaded.Enabled);
        Assert.Equal("LC", loaded.Label);
        Assert.Equal(FloatingButtonSize.Small, loaded.Size);
        Assert.Equal(100, loaded.X);
        Assert.Equal(200, loaded.Y);
    }

    [Fact]
    public void A_macro_file_from_before_floating_buttons_loads_and_a_saved_one_keeps_it()
    {
        Guid id = Guid.NewGuid();
        File.WriteAllText(_folder.File($"{id}.json"), $$"""{ "Id": "{{id}}", "Name": "Old macro", "Actions": [] }""");
        var library = new MacroLibrary(_folder.Path);
        library.Load(Array.Empty<Guid>(), out string? warning);

        Assert.Null(warning);
        MacroDefinition old = Assert.Single(library.Macros);
        Assert.False(old.FloatingButton.Enabled);

        old.FloatingButton = new FloatingButton { Enabled = true, Label = "OM", Size = FloatingButtonSize.Large };
        Assert.True(library.TrySave(old, out _));
        var reloaded = new MacroLibrary(_folder.Path);
        reloaded.Load(Array.Empty<Guid>(), out _);

        FloatingButton button = Assert.Single(reloaded.Macros).FloatingButton;
        Assert.True(button.Enabled);
        Assert.Equal("OM", button.Label);
        Assert.Equal(FloatingButtonSize.Large, button.Size);
    }
}
