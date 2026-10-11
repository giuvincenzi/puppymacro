using System;
using System.Collections.Generic;
using System.IO;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The options shared by loop actions and macro actions: a key with Ctrl, Alt, Shift and Win, Hold for, Times,
/// pause, double click and a fixed point. Older files must load and save exactly as before.
/// </summary>
public sealed class ActionOptionsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string? NoConflict(HotkeyBinding binding, string? appExe) => null;

    private static readonly HotkeyBinding LeftCtrlC = new() { Vk = 0x43, Ctrl = true, CtrlSide = ModifierSide.Left };

    private static LoopDefinition LoopWith(LoopAction action) => new() { Name = "Loop", Actions = { action } };

    // ---- Models ----

    [Fact]
    public void A_key_with_modifiers_is_kept_in_flat_fields_and_read_back_as_a_binding()
    {
        var loop = new LoopAction { Key = LeftCtrlC };
        var macro = new MacroAction { Type = MacroActionType.PressKey, Key = LeftCtrlC };

        Assert.Equal(0x43, loop.KeyVk);
        Assert.True(loop.Ctrl);
        Assert.True(loop.Key.SameAs(LeftCtrlC));
        Assert.Equal(0x43, macro.Vk);
        Assert.True(macro.Key.SameAs(LeftCtrlC));
        Assert.True(macro.Clone().Key.SameAs(LeftCtrlC));
        Assert.True(loop.Clone().Key.SameAs(LeftCtrlC));
    }

    [Fact]
    public void A_loop_action_without_the_new_fields_behaves_as_before()
    {
        var action = new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 40 };

        Assert.True(action.IsClick);
        Assert.Equal(LoopAction.DefaultHoldMs, action.EffectiveHoldMs);
        Assert.Equal(1, action.Times);
        Assert.False(action.IsDoubleClick);
        Assert.False(action.HasFixedPoint);
        Assert.False(action.HasModifiers);
    }

    [Fact]
    public void Normalize_keeps_options_only_where_they_belong()
    {
        var text = new LoopAction { Type = ActionType.Text, Text = "hi", Ctrl = true, HoldMs = 80, Repeat = 3, RepeatPauseMs = 10, ClickCount = 2, X = 1, Y = 2 };
        text.Normalize();
        Assert.False(text.HasModifiers);
        Assert.Null(text.HoldMs);
        Assert.Null(text.Repeat);
        Assert.Null(text.RepeatPauseMs);
        Assert.Null(text.ClickCount);
        Assert.Null(text.X);

        var click = new LoopAction { Type = ActionType.Key, KeyVk = 0x01, Ctrl = true, ClickCount = 2, X = 10, Y = null, Repeat = 1 };
        click.Normalize();
        Assert.False(click.HasModifiers); // a click has none
        Assert.Equal(2, click.ClickCount);
        Assert.Null(click.X);             // a point needs both X and Y
        Assert.Null(click.Repeat);        // once is the default

        var held = new LoopAction { Type = ActionType.Key, Key = LeftCtrlC, HoldDown = true };
        held.Normalize();
        Assert.False(held.HasModifiers);  // Hold down holds the key alone, as the runner does

        var key = new LoopAction { Type = ActionType.Key, Key = LeftCtrlC, ClickCount = 2, X = 1, Y = 1 };
        key.Normalize();
        Assert.True(key.Key.SameAs(LeftCtrlC));
        Assert.Null(key.ClickCount);
        Assert.Null(key.X);
    }

    // ---- Files ----

    private AppSettings Load(string json)
    {
        string path = _folder.File("settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path).Load(out _);
    }

    [Fact]
    public void An_older_loop_file_loads_and_saves_without_the_new_fields()
    {
        AppSettings settings = Load("""
            {
              "SchemaVersion": 7,
              "Loops": [ { "Name": "Old", "Actions": [
                { "Type": "Key", "KeyVk": 1, "IntervalValue": 1, "IntervalUnit": "Seconds" },
                { "Type": "Key", "KeyVk": 87, "HoldDown": true },
                { "Type": "Text", "Text": "Hello", "EnterAfter": true, "IntervalValue": 30, "IntervalUnit": "Seconds" }
              ] } ]
            }
            """);

        List<LoopAction> actions = settings.Loops[0].Actions;
        Assert.True(actions[0].IsClick);
        Assert.False(actions[0].HasFixedPoint);
        Assert.Equal(87, actions[1].KeyVk);
        Assert.True(actions[1].HoldDown);
        Assert.Equal("Hello", actions[2].Text);

        string path = _folder.File("settings.json");
        Assert.True(new SettingsStore(path).TrySave(settings, out _));
        string saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Loops"]![0]!["Actions"]!.ToJsonString();
        foreach (string field in new[] { "HoldMs", "\"Repeat\"", "RepeatPauseMs", "ClickCount", "\"Ctrl\"", "CtrlSide" })
            Assert.DoesNotContain(field, saved);
    }

    [Fact]
    public void A_macro_editor_width_kept_before_schema_7_grows_with_its_right_column_once()
    {
        AppSettings old = Load("""{ "SchemaVersion": 6, "MacroEditorWidth": 1200 }""");
        Assert.Equal(1320, old.MacroEditorWidth);

        AppSettings current = Load("""{ "SchemaVersion": 7, "MacroEditorWidth": 1300 }""");
        Assert.Equal(1300, current.MacroEditorWidth);
    }

    [Fact]
    public void The_new_loop_options_are_saved_and_loaded()
    {
        string path = _folder.File("settings.json");
        var store = new SettingsStore(path);
        AppSettings settings = AppSettings.CreateDefault();
        settings.Loops[0].Actions = new()
        {
            new LoopAction { Type = ActionType.Key, Key = LeftCtrlC, HoldMs = 80, Repeat = 3, RepeatPauseMs = 120, IntervalValue = 5, IntervalUnit = IntervalUnit.Seconds },
            new LoopAction { Type = ActionType.Key, KeyVk = 0x01, ClickCount = 2, X = 640, Y = 360, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds },
        };

        Assert.True(store.TrySave(settings, out _));
        AppSettings loaded = store.Load(out _);

        LoopAction key = loaded.Loops[0].Actions[0];
        Assert.True(key.Key.SameAs(LeftCtrlC));
        Assert.Equal(80, key.HoldMs);
        Assert.Equal(3, key.Times);
        Assert.Equal(120, key.EffectiveRepeatPauseMs);
        LoopAction click = loaded.Loops[0].Actions[1];
        Assert.True(click.IsDoubleClick);
        Assert.Equal(640, click.X);
        Assert.Equal(360, click.Y);
    }

    [Fact]
    public void A_macro_file_without_modifiers_loads_with_none_and_other_types_drop_them()
    {
        var library = new MacroLibrary(_folder.Path);
        var macro = new MacroDefinition
        {
            Name = "M",
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Key = LeftCtrlC },
                new MacroAction { Type = MacroActionType.Click, Vk = 0x01, Ctrl = true },
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 },
            },
        };
        Assert.True(library.TrySave(macro, out _));
        library.Load(Array.Empty<Guid>(), out _);

        MacroDefinition loaded = Assert.Single(library.Macros);
        Assert.True(loaded.Actions[0].Key.SameAs(LeftCtrlC));
        Assert.False(loaded.Actions[1].HasModifiers);
        Assert.False(loaded.Actions[2].HasModifiers);

        // A macro without combinations writes no modifier fields: its file stays as before.
        var plain = new MacroDefinition { Name = "Plain", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        Assert.True(library.TrySave(plain, out _));
        string file = File.ReadAllText(Path.Combine(_folder.Path, $"{plain.Id}.json"));
        Assert.Equal(0, CountOf(file, "Ctrl"));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- Code view: loops ----

    [Fact]
    public void A_loop_with_the_new_options_reads_back_unchanged()
    {
        LoopDefinition loop = LoopWith(new LoopAction { Type = ActionType.Key, Key = LeftCtrlC, HoldMs = 80, Repeat = 3, RepeatPauseMs = 120 });
        loop.Actions.Add(new LoopAction { Type = ActionType.Key, KeyVk = 0x01, ClickCount = 2, X = 640, Y = 360 });
        string text = LoopJson.Serialize(loop);

        LoopDefinition? read = LoopJson.Parse(text, loop.Id, NoConflict, out var problems);

        Assert.Empty(problems);
        Assert.Equal(text, LoopJson.Serialize(read!));
    }

    [Theory]
    [InlineData("hold", "Actions[0].Ctrl:")]
    [InlineData("click", "Actions[0].Ctrl:")]
    [InlineData("text", "Actions[0].Repeat:")]
    [InlineData("point", "Actions[0].X:")]
    [InlineData("count", "Actions[0].ClickCount:")]
    [InlineData("side", "Actions[0].AltSide:")]
    [InlineData("keyclick", "Actions[0].X:")]
    public void A_loop_action_takes_only_its_own_options(string kind, string expected)
    {
        LoopAction action = kind switch
        {
            "hold" => new LoopAction { Type = ActionType.Key, Key = LeftCtrlC, HoldDown = true },
            "click" => new LoopAction { Type = ActionType.Key, KeyVk = 0x01, Ctrl = true, IntervalValue = 40 },
            "text" => new LoopAction { Type = ActionType.Text, Text = "hi", Repeat = 2, IntervalValue = 40 },
            "point" => new LoopAction { Type = ActionType.Key, KeyVk = 0x01, X = 5, IntervalValue = 40 },
            "count" => new LoopAction { Type = ActionType.Key, KeyVk = 0x01, ClickCount = 3, IntervalValue = 40 },
            "side" => new LoopAction { Type = ActionType.Key, KeyVk = 0x43, AltSide = ModifierSide.Left, IntervalValue = 40 },
            _ => new LoopAction { Type = ActionType.Key, KeyVk = 0x43, X = 1, Y = 1, IntervalValue = 40 },
        };
        LoopDefinition loop = LoopWith(action);

        Assert.Null(LoopJson.Parse(LoopJson.Serialize(loop), loop.Id, NoConflict, out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith(expected));
    }

    [Fact]
    public void The_hotkey_cannot_be_a_key_or_combination_the_loop_presses()
    {
        var press = new LoopAction { Type = ActionType.Key, Key = LeftCtrlC };
        var middle = new LoopAction { Type = ActionType.Key, KeyVk = KeyNames.VK_MBUTTON };

        Assert.True(LoopJson.HotkeyIsPressed(new HotkeyBinding { Vk = 0x43, Ctrl = true }, new[] { press }));
        Assert.False(LoopJson.HotkeyIsPressed(new HotkeyBinding { Vk = 0x43, Ctrl = true, CtrlSide = ModifierSide.Right }, new[] { press }));
        Assert.False(LoopJson.HotkeyIsPressed(HotkeyBinding.FromKey(0x43), new[] { press }));
        Assert.True(LoopJson.HotkeyIsPressed(HotkeyBinding.FromKey(KeyNames.VK_MBUTTON), new[] { middle }));
        Assert.False(LoopJson.HotkeyIsPressed(null, new[] { middle }));
    }

    // ---- Code view: macro actions ----

    private static MacroAction? ParseAction(MacroAction action, out List<CodeProblem> problems) =>
        MacroJson.ParseAction(MacroJson.Serialize(action), new Guid?[] { null }, new Dictionary<Guid, string>(), out problems);

    [Fact]
    public void A_macro_press_key_with_a_combination_reads_back_unchanged()
    {
        var action = new MacroAction { Type = MacroActionType.PressKey, Key = LeftCtrlC };
        MacroAction? read = ParseAction(action, out var problems);

        Assert.Empty(problems);
        Assert.True(read!.Key.SameAs(LeftCtrlC));
        Assert.Equal(MacroJson.Serialize(action), MacroJson.Serialize(read));
    }

    [Fact]
    public void Only_a_macro_press_key_has_modifiers_and_key_actions_take_no_mouse_button()
    {
        Assert.Null(ParseAction(new MacroAction { Type = MacroActionType.KeyDown, Vk = 0x43, Shift = true }, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Shift:") && p.Message.Contains("one key"));

        Assert.Null(ParseAction(new MacroAction { Type = MacroActionType.Click, Vk = 0x01, Ctrl = true }, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Ctrl:"));

        Assert.Null(ParseAction(new MacroAction { Type = MacroActionType.PressKey, Vk = 0x01 }, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Vk:") && p.Message.Contains("mouse buttons"));

        // A modifier alone is a key: Key down Left Ctrl.
        Assert.NotNull(ParseAction(new MacroAction { Type = MacroActionType.KeyDown, Vk = KeyNames.VK_LCONTROL }, out problems));
        Assert.Empty(problems);
    }
}
