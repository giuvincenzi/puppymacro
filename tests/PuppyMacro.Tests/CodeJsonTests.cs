using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The Code views of one action, a loop and a remap (the whole macro: <see cref="MacroJsonTests"/>).</summary>
public class CodeJsonTests
{
    private static readonly Guid GroupA = Guid.NewGuid(), GroupB = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> Names = new() { [GroupA] = "Open", [GroupB] = "Close" };

    private static MacroAction Key(Guid? group) => new() { Type = MacroActionType.PressKey, Vk = 0x41, GroupId = group };

    private static string? NoConflict(HotkeyBinding binding, string? appExe) => null;

    // ---- One action and its group ----

    [Fact]
    public void An_action_can_join_the_group_before_or_after_it_but_not_split_one()
    {
        // A A B B: the action at index 1, the last of A, can stay in A, leave it or join B.
        var actions = new List<MacroAction> { Key(GroupA), Key(GroupA), Key(GroupB), Key(GroupB) };

        Assert.Equal(new HashSet<Guid?> { null, GroupA, GroupB }, MacroJson.AllowedGroupIds(actions, 1, replacing: true).ToHashSet());
        // Inside A (A A A): leaving the group would split it.
        var middle = new List<MacroAction> { Key(GroupA), Key(GroupA), Key(GroupA) };
        Assert.Equal(new Guid?[] { GroupA }, MacroJson.AllowedGroupIds(middle, 1, replacing: true));
        // Inserted between A and B: none, A or B.
        Assert.Equal(3, MacroJson.AllowedGroupIds(actions, 2, replacing: false).Count);
    }

    [Fact]
    public void An_action_reads_back_and_its_group_must_be_allowed()
    {
        string text = MacroJson.Serialize(Key(GroupA));
        Assert.NotNull(MacroJson.ParseAction(text, new Guid?[] { GroupA, null }, Names, out var problems));
        Assert.Empty(problems);

        Assert.Null(MacroJson.ParseAction(text, new Guid?[] { null, GroupB }, Names, out problems));
        CodeProblem problem = Assert.Single(problems);
        Assert.StartsWith("GroupId:", problem.Message);
        Assert.Contains("(Close)", problem.Message);
    }

    [Fact]
    public void An_action_follows_the_action_window_rules()
    {
        var action = new MacroAction { Type = MacroActionType.Click, Vk = 0x41, ClickCount = 3 };

        Assert.Null(MacroJson.ParseAction(MacroJson.Serialize(action), new Guid?[] { null }, Names, out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith("Vk:") && p.Message.Contains("mouse button"));
        Assert.Contains(problems, p => p.Message.StartsWith("ClickCount:"));
    }

    [Fact]
    public void The_action_schema_suggests_the_allowed_groups_by_name()
    {
        JsonObject schema = JsonNode.Parse(CodeSchema.ForAction(new Guid?[] { null, GroupA }, Names, GroupA))!.AsObject();
        JsonObject group = schema["properties"]!["GroupId"]!.AsObject();

        Assert.Equal(2, group["enum"]!.AsArray().Count);
        Assert.Contains("Group \"Open\"", group["enumDescriptions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(GroupA.ToString(), group["default"]!.GetValue<string>());
    }

    // ---- Loop ----

    private static LoopDefinition Loop() => new()
    {
        Name = "Spam",
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 40 } },
    };

    [Fact]
    public void A_loop_reads_back_unchanged()
    {
        LoopDefinition loop = Loop();
        string text = LoopJson.Serialize(loop);

        LoopDefinition? read = LoopJson.Parse(text, loop.Id, NoConflict, out var problems);

        Assert.Empty(problems);
        Assert.Equal(text, LoopJson.Serialize(read!));
    }

    [Fact]
    public void A_loop_follows_the_loop_editor_rules()
    {
        LoopDefinition loop = Loop();
        loop.Name = " ";
        loop.Actions.Add(new LoopAction { Type = ActionType.Text, Text = "", IntervalValue = 1, IntervalUnit = IntervalUnit.Milliseconds });
        loop.Hotkey = HotkeyBinding.FromKey(0x01); // the key it presses, without a modifier
        loop.Mode = ActivationMode.Toggle;

        Assert.Null(LoopJson.Parse(LoopJson.Serialize(loop), loop.Id, NoConflict, out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith("Name:"));
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[1].Text:"));
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[1].IntervalValue:"));
        Assert.Contains(problems, p => p.Message.StartsWith("Hotkey:") && p.Message.Contains("Left and right click"));
        Assert.Contains(problems, p => p.Message.StartsWith("Hotkey:") && p.Message.Contains("one of the keys"));
    }

    [Fact]
    public void A_loop_keeps_its_Id_and_has_no_version_1_fields()
    {
        LoopDefinition loop = Loop();
        string text = LoopJson.Serialize(loop).Replace("\"Name\": \"Spam\",", "\"Name\": \"Spam\",\n  \"KeyVk\": 65,");

        Assert.Null(LoopJson.Parse(text, Guid.NewGuid(), NoConflict, out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith("Id:"));
        Assert.Contains(problems, p => p.Message.StartsWith("KeyVk:"));
        JsonObject schema = JsonNode.Parse(CodeSchema.ForLoop(loop.Id))!.AsObject();
        Assert.False(schema["properties"]!.AsObject().ContainsKey("KeyVk"));
        Assert.Equal(loop.Id.ToString(), schema["properties"]!["Id"]!["default"]!.GetValue<string>());
    }

    [Fact]
    public void A_loop_for_a_specific_app_names_its_exe_and_its_hotkey_is_checked_in_that_app()
    {
        LoopDefinition loop = Loop();
        loop.AppExe = "notepad";
        Assert.Null(LoopJson.Parse(LoopJson.Serialize(loop), loop.Id, NoConflict, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("AppExe:") && p.Message.Contains(".exe"));

        loop.AppExe = " notepad.exe ";
        loop.Hotkey = HotkeyBinding.FromKey(0x79);
        string? checkedApp = "unset";
        LoopDefinition? read = LoopJson.Parse(LoopJson.Serialize(loop), loop.Id, (_, app) => { checkedApp = app; return null; }, out problems);
        Assert.Empty(problems);
        Assert.Equal("notepad.exe", read!.AppExe);
        Assert.Equal("notepad.exe", checkedApp);
    }

    // ---- Remap ----

    private static string? NoClash(HotkeyBinding source, string? app) => null;

    [Fact]
    public void A_remap_reads_back_unchanged()
    {
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(0x14), Target = HotkeyBinding.FromKey(0x1B), AppExe = "notepad.exe", Note = "Caps to Esc" };
        string text = RemapJson.Serialize(remap);

        RemapDefinition? read = RemapJson.Parse(text, remap.Id, NoClash, out var problems);

        Assert.Empty(problems);
        Assert.Equal(text, RemapJson.Serialize(read!));
    }

    [Fact]
    public void A_remap_follows_the_remap_editor_rules()
    {
        var click = new RemapDefinition { Source = HotkeyBinding.FromKey(KeyNames.VK_LBUTTON), Target = HotkeyBinding.FromKey(KeyNames.VK_LBUTTON) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(click), click.Id, NoClash, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Target:") && p.Message.Contains("different"));
        Assert.Contains(problems, p => p.Message.StartsWith("AppExe:") && p.Message.Contains("specific app"));

        var noExe = new RemapDefinition { Source = HotkeyBinding.FromKey(0x14), Target = HotkeyBinding.FromKey(0x1B), AppExe = "notepad" };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(noExe), noExe.Id, NoClash, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("AppExe:") && p.Message.Contains(".exe"));

        var taken = new RemapDefinition { Source = HotkeyBinding.FromKey(0x14), Target = HotkeyBinding.FromKey(0x1B) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(taken), taken.Id, (_, _) => "Caps Lock is already remapped for all apps.", out problems));
        Assert.Contains(problems, p => p.Message == "Source: Caps Lock is already remapped for all apps.");
    }

    [Fact]
    public void A_remap_from_a_combination_or_a_modifier_alone_reads_back_unchanged()
    {
        var combination = new RemapDefinition
        {
            Source = new HotkeyBinding { Vk = 0x41, Alt = true, AltSide = ModifierSide.Left },
            Target = new HotkeyBinding { Vk = 0x25, Ctrl = true, CtrlSide = ModifierSide.Right },
        };
        string text = RemapJson.Serialize(combination);
        Assert.Contains("\"AltSide\": \"Left\"", text);
        Assert.DoesNotContain("ShiftSide", text); // either side is the default, left out
        RemapDefinition? read = RemapJson.Parse(text, combination.Id, NoClash, out var problems);
        Assert.Empty(problems);
        Assert.True(read!.Source!.SameAs(combination.Source));
        Assert.True(read.Target!.SameAs(combination.Target));

        var rightAlt = new RemapDefinition { Source = HotkeyBinding.FromKey(KeyNames.VK_RMENU), Target = HotkeyBinding.FromKey(KeyNames.VK_LCONTROL) };
        Assert.NotNull(RemapJson.Parse(RemapJson.Serialize(rightAlt), rightAlt.Id, NoClash, out problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void A_remap_checks_modifiers_alone_sides_and_the_old_source_key()
    {
        var eitherAlt = new RemapDefinition { Source = HotkeyBinding.FromKey(0x12), Target = HotkeyBinding.FromKey(0x0D) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(eitherAlt), eitherAlt.Id, NoClash, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Source.Vk:") && p.Message.Contains("left or right key"));

        var modifierWithModifier = new RemapDefinition { Source = new HotkeyBinding { Vk = KeyNames.VK_RMENU, Ctrl = true }, Target = HotkeyBinding.FromKey(0x0D) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(modifierWithModifier), modifierWithModifier.Id, NoClash, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Source:") && p.Message.Contains("no other modifier"));

        var sideWithoutModifier = new RemapDefinition { Source = new HotkeyBinding { Vk = 0x41, CtrlSide = ModifierSide.Left }, Target = HotkeyBinding.FromKey(0x0D) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(sideWithoutModifier), sideWithoutModifier.Id, NoClash, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Source.CtrlSide:"));

        var old = new RemapDefinition { SourceVk = 0x14, Target = HotkeyBinding.FromKey(0x1B) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(old), old.Id, NoClash, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("SourceVk:") && p.Message.Contains("Not used any more"));
        Assert.Contains(problems, p => p.Message.StartsWith("Source:"));
    }
}
