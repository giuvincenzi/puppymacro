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

    private static string? NoConflict(HotkeyBinding binding) => null;

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

    // ---- Remap ----

    private static string? NoClash(int sourceVk, string? app) => null;

    [Fact]
    public void A_remap_reads_back_unchanged()
    {
        var remap = new RemapDefinition { SourceVk = 0x14, Target = HotkeyBinding.FromKey(0x1B), AppExe = "notepad.exe", Note = "Caps to Esc" };
        string text = RemapJson.Serialize(remap);

        RemapDefinition? read = RemapJson.Parse(text, remap.Id, NoClash, out var problems);

        Assert.Empty(problems);
        Assert.Equal(text, RemapJson.Serialize(read!));
    }

    [Fact]
    public void A_remap_follows_the_remap_editor_rules()
    {
        var click = new RemapDefinition { SourceVk = KeyNames.VK_LBUTTON, Target = HotkeyBinding.FromKey(KeyNames.VK_LBUTTON) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(click), click.Id, NoClash, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Target:") && p.Message.Contains("different"));
        Assert.Contains(problems, p => p.Message.StartsWith("AppExe:") && p.Message.Contains("specific app"));

        var noExe = new RemapDefinition { SourceVk = 0x14, Target = HotkeyBinding.FromKey(0x1B), AppExe = "notepad" };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(noExe), noExe.Id, NoClash, out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("AppExe:") && p.Message.Contains(".exe"));

        var taken = new RemapDefinition { SourceVk = 0x14, Target = HotkeyBinding.FromKey(0x1B) };
        Assert.Null(RemapJson.Parse(RemapJson.Serialize(taken), taken.Id, (_, _) => "Caps Lock is already remapped for all apps.", out problems));
        Assert.Contains(problems, p => p.Message == "SourceVk: Caps Lock is already remapped for all apps.");
    }
}
