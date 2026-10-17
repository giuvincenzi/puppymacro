using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>Items as AI apps read and change them: compact JSON (get_item) and JSON Patch (patch_item).</summary>
public class AiJsonTests
{
    private static string? NoClash(HotkeyBinding binding, string? app) => null;

    /// <summary>A macro with a bit of everything: groups, several action types, a hotkey, a floating button, an app.</summary>
    internal static MacroDefinition BigMacro()
    {
        var group = new MacroGroup { Name = "Slot 1" };
        var macro = new MacroDefinition
        {
            Name = "Salvage",
            Hotkey = new HotkeyBinding { Vk = 0x75, Ctrl = true },
            Repeat = RepeatMode.Times,
            RepeatCount = 3,
            AppExe = "Diablo IV.exe",
            FloatingButton = { Enabled = true, Label = "SV", X = 100, Y = 200 },
            Groups = { group },
        };
        for (int i = 0; i < 30; i++)
        {
            Guid? inGroup = i < 5 ? group.Id : null; // a group's actions are consecutive
            macro.Actions.Add(new MacroAction { Type = MacroActionType.Click, Vk = 1, X = 400 + i, Y = 300, DelayMs = 120, GroupId = inGroup });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.PressKey, Vk = 0x20, DelayMs = 80, Name = $"Destroy {i}", GroupId = inGroup });
        }
        macro.Actions.Add(new MacroAction { Type = MacroActionType.PasteText, Text = "gg", EnterAfter = true });
        macro.Actions.Add(new MacroAction { Type = MacroActionType.MoveTo, X = 10, Y = 20, Smooth = true });
        return macro;
    }

    [Fact]
    public void A_compact_macro_reads_back_as_the_same_macro_and_is_much_shorter()
    {
        MacroDefinition macro = BigMacro();

        string compact = AiJson.Compact(macro);
        MacroDefinition? back = MacroJson.Parse(compact, macro.Id, NoClash, out List<CodeProblem> problems);

        Assert.True(back != null, string.Join("; ", problems.Select(p => p.Message)));
        Assert.Equal(MacroJson.Serialize(macro), MacroJson.Serialize(back));
        Assert.DoesNotContain("\n", compact);
        Assert.True(compact.Length * 3 < MacroJson.Serialize(macro).Length, $"{compact.Length} vs {MacroJson.Serialize(macro).Length}");
    }

    [Fact]
    public void Fields_with_their_default_are_left_out_and_the_others_kept()
    {
        var loop = new LoopDefinition { Name = "Spam", Enabled = false, Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = 250 } } };

        JsonObject compact = JsonNode.Parse(AiJson.Compact(loop))!.AsObject();

        Assert.False(compact["Enabled"]!.GetValue<bool>()); // not the default: kept
        Assert.False(compact.ContainsKey("ShowInOverlay")); // the default: left out
        Assert.False(compact.ContainsKey("FloatingButton")); // all its fields are the defaults
        Assert.Equal(0x87, compact["Actions"]![0]!["KeyVk"]!.GetValue<int>());
        LoopDefinition? back = LoopJson.Parse(AiJson.Compact(loop), loop.Id, NoClash, out _);
        Assert.Equal(LoopJson.Serialize(loop), LoopJson.Serialize(back!));
    }

    [Fact]
    public void A_compact_remap_reads_back_as_the_same_remap()
    {
        var remap = new RemapDefinition { Source = new HotkeyBinding { Vk = 0x58, Ctrl = true }, Target = HotkeyBinding.FromKey(0x41), Note = "Cut is A" };

        RemapDefinition? back = RemapJson.Parse(AiJson.Compact(remap), remap.Id, NoClash, out _);

        Assert.Equal(RemapJson.Serialize(remap), RemapJson.Serialize(back!));
    }

    private static JsonElement Ops(object operations) => JsonSerializer.SerializeToElement(operations);

    [Fact]
    public void A_patch_adds_removes_replaces_moves_copies_and_tests()
    {
        JsonNode doc = JsonNode.Parse("""{"Name":"M","Actions":[{"DelayMs":0},{"DelayMs":1},{"DelayMs":2}]}""")!;

        JsonNode result = AiJson.Patch(doc, Ops(new object[]
        {
            new { op = "test", path = "/Name", value = "M" },
            new { op = "replace", path = "/Actions/1/DelayMs", value = 250 },
            new { op = "add", path = "/Actions/-", value = new { DelayMs = 3 } },
            new { op = "add", path = "/Actions/0", value = new { DelayMs = 9 } },
            new { op = "remove", path = "/Actions/3" },
            new { op = "move", from = "/Actions/0", path = "/Actions/-" },
            new { op = "copy", from = "/Name", path = "/Note" },
        }));

        Assert.Equal("""{"Name":"M","Actions":[{"DelayMs":0},{"DelayMs":250},{"DelayMs":3},{"DelayMs":9}],"Note":"M"}""", result.ToJsonString());
        Assert.Equal("""{"Name":"M","Actions":[{"DelayMs":0},{"DelayMs":1},{"DelayMs":2}]}""", doc.ToJsonString()); // untouched
    }

    [Theory]
    [InlineData("""[{"op":"replace","path":"/Actions/7/DelayMs","value":1}]""", "not an index")]
    [InlineData("""[{"op":"replace","path":"/Nope","value":1}]""", "no field")]
    [InlineData("""[{"op":"remove","path":"Actions/0"}]""", "JSON Pointer")]
    [InlineData("""[{"op":"test","path":"/Name","value":"X"}]""", "not the one given")]
    [InlineData("""[{"op":"rename","path":"/Name"}]""", "must be add")]
    [InlineData("""[{"op":"replace","path":"/Name"}]""", "needs a \"value\"")]
    [InlineData("""[{"op":"move","from":"/Actions","path":"/Actions/0"}]""", "inside itself")]
    [InlineData("""[]""", "list of operations")]
    public void A_wrong_patch_says_which_operation_and_why(string patch, string reason)
    {
        JsonNode doc = JsonNode.Parse("""{"Name":"M","Actions":[{"DelayMs":0}]}""")!;
        using JsonDocument operations = JsonDocument.Parse(patch);

        FormatException error = Assert.Throws<FormatException>(() => AiJson.Patch(doc, operations.RootElement));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Set_where_changes_every_matching_action_and_only_those()
    {
        MacroDefinition macro = BigMacro();
        JsonNode doc = JsonNode.Parse(MacroJson.Serialize(macro))!;

        (JsonNode result, int count) = AiJson.SetWhere(doc, Ops(new { DelayMs = 120 }), Ops(new { DelayMs = 1200 }), null);
        MacroDefinition? after = MacroJson.Parse(result.ToJsonString(), macro.Id, NoClash, out _);

        Assert.Equal(30, count);
        Assert.All(after!.Actions.Where(a => a.Type == MacroActionType.Click), a => Assert.Equal(1200, a.DelayMs));
        Assert.All(after.Actions.Where(a => a.Type == MacroActionType.PressKey), a => Assert.Equal(80, a.DelayMs));
    }

    [Fact]
    public void Set_where_can_stay_in_one_group()
    {
        MacroDefinition macro = BigMacro();
        JsonNode doc = JsonNode.Parse(MacroJson.Serialize(macro))!;

        (_, int count) = AiJson.SetWhere(doc, Ops(new { Type = "Click" }), Ops(new { DelayMs = 5 }), "slot 1");

        Assert.Equal(5, count); // the group holds the first 5 clicks
    }

    [Theory]
    [InlineData("""{"DelayMs":999}""", """{"DelayMs":1}""", null, "no action matches")]
    [InlineData("""{"DelayMs":120}""", """{"Nope":1}""", null, "no field \"Nope\"")]
    [InlineData("""{"DelayMs":120}""", """{}""", null, "\"set\" must be")]
    [InlineData("""{"DelayMs":120}""", """{"DelayMs":1}""", "Missing", "no group is named")]
    public void Set_where_says_why_it_cannot(string where, string set, string? group, string reason)
    {
        JsonNode doc = JsonNode.Parse(MacroJson.Serialize(BigMacro()))!;
        using JsonDocument w = JsonDocument.Parse(where), s = JsonDocument.Parse(set);

        FormatException error = Assert.Throws<FormatException>(() => AiJson.SetWhere(doc, w.RootElement, s.RootElement, group));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void SetWhere_also_changes_the_actions_inside_then_and_else()
    {
        var macro = new MacroDefinition
        {
            Name = "M",
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Vk = 65, DelayMs = 800 },
                new MacroAction
                {
                    Type = MacroActionType.If,
                    If = new MacroIf
                    {
                        Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" } },
                        Then = { new MacroAction { Type = MacroActionType.PressKey, Vk = 66, DelayMs = 800 } },
                        Else = { new MacroAction { Type = MacroActionType.PressKey, Vk = 67, DelayMs = 800 } },
                    },
                },
            },
        };
        var document = System.Text.Json.Nodes.JsonNode.Parse(MacroJson.Serialize(macro))!;
        using var where = System.Text.Json.JsonDocument.Parse("{\"DelayMs\": 800}");
        using var set = System.Text.Json.JsonDocument.Parse("{\"DelayMs\": 1200}");

        var (result, count) = AiJson.SetWhere(document, where.RootElement, set.RootElement, null);

        Assert.Equal(3, count);
        Assert.Equal(1200, result["Actions"]![1]!["If"]!["Else"]![0]!["DelayMs"]!.GetValue<double>());
    }
}
