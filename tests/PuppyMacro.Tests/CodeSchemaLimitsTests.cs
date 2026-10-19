using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The JSON Schema of the Code views (<see cref="CodeSchema"/>) describes the same limits as the checks: a value just
/// outside a field's limits is refused by both, the limit itself accepted by both.
/// </summary>
public class CodeSchemaLimitsTests
{
    private static string? NoConflict(HotkeyBinding binding, string? appExe) => null;

    private static readonly Guid Image = Guid.NewGuid();

    private static MacroDefinition Macro() => new()
    {
        Name = "Limits",
        Actions =
        {
            new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 },
            new MacroAction { Type = MacroActionType.Click, Vk = 1, X = 10, Y = 10 },
            new MacroAction { Type = MacroActionType.MoveTo, X = 10, Y = 10, Smooth = true },
            new MacroAction { Type = MacroActionType.Scroll, ScrollSteps = 3 },
            new MacroAction
            {
                Type = MacroActionType.If,
                If = new MacroIf
                {
                    Wait = ConditionWait.UpTo,
                    WaitSeconds = 5,
                    Conditions =
                    {
                        new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 1, Color = "#112233", Tolerance = 5 },
                        new MacroCondition
                        {
                            Id = Image, Kind = ConditionKind.Image, Name = "Button", Similarity = 90, Area = SearchArea.Drawn,
                            AreaX = 0, AreaY = 0, AreaWidth = 100, AreaHeight = 100,
                            Images = new List<ConditionImage> { new() { Id = new string('a', 64), X = 5, Y = 5 } },
                        },
                    },
                },
            },
        },
    };

    private static LoopDefinition Loop() => new()
    {
        Name = "Limits",
        Actions =
        {
            new LoopAction { Type = ActionType.Key, KeyVk = 0x41, IntervalValue = 100, HoldMs = 20, Repeat = 2, RepeatPauseMs = 10 },
            new LoopAction { Type = ActionType.Key, KeyVk = 1, IntervalValue = 100, ClickCount = 2, X = 5, Y = 5 },
        },
    };

    /// <summary>Paths into the item's JSON ("Actions/1/X") and a value: limit-1 and limit+1, then the limits themselves.</summary>
    public static TheoryData<string, string, double, bool> MacroCases() => new()
    {
        { "macro", "RepeatCount", 0, false }, { "macro", "RepeatCount", 100_001, false }, { "macro", "RepeatCount", 100_000, true },
        { "macro", "FloatingButton/Opacity", 19, false }, { "macro", "FloatingButton/Opacity", 101, false }, { "macro", "FloatingButton/Opacity", 20, true },
        { "macro", "Actions/0/DelayMs", -1, false }, { "macro", "Actions/0/DelayMs", 86_400_001, false }, { "macro", "Actions/0/DelayMs", 86_400_000, true },
        { "macro", "Actions/0/HoldMs", 0.5, false }, { "macro", "Actions/0/HoldMs", 60_001, false }, { "macro", "Actions/0/HoldMs", 1, true },
        { "macro", "Actions/0/Repeat", 0, false }, { "macro", "Actions/0/Repeat", 100_001, false }, { "macro", "Actions/0/Repeat", 100_000, true },
        { "macro", "Actions/0/RepeatPauseMs", -1, false }, { "macro", "Actions/0/RepeatPauseMs", 86_400_001, false },
        { "macro", "Actions/1/ClickCount", 0, false }, { "macro", "Actions/1/ClickCount", 3, false }, { "macro", "Actions/1/ClickCount", 2, true },
        { "macro", "Actions/1/X", -100_001, false }, { "macro", "Actions/1/Y", 100_001, false }, { "macro", "Actions/1/X", -100_000, true },
        { "macro", "Actions/2/SmoothSpeed", 0, false }, { "macro", "Actions/2/SmoothSpeed", 6, false }, { "macro", "Actions/2/SmoothSpeed", 5, true },
        { "macro", "Actions/3/ScrollSteps", 0, false }, { "macro", "Actions/3/ScrollSteps", 1001, false }, { "macro", "Actions/3/ScrollSteps", 1000, true },
        { "macro", "Actions/4/If/WaitSeconds", 0.05, false }, { "macro", "Actions/4/If/WaitSeconds", 86_401, false }, { "macro", "Actions/4/If/WaitSeconds", 0.1, true },
        { "macro", "Actions/4/If/Conditions/0/Tolerance", -1, false }, { "macro", "Actions/4/If/Conditions/0/Tolerance", 101, false },
        { "macro", "Actions/4/If/Conditions/0/X", 100_001, false },
        { "macro", "Actions/4/If/Conditions/1/Similarity", 49, false }, { "macro", "Actions/4/If/Conditions/1/Similarity", 101, false },
        { "macro", "Actions/4/If/Conditions/1/Similarity", 50, true },
        { "macro", "Actions/4/If/Conditions/1/AreaWidth", 7, false }, { "macro", "Actions/4/If/Conditions/1/AreaHeight", 100_001, false },
        { "macro", "Actions/4/If/Conditions/1/AreaWidth", 8, true },
        { "macro", "Actions/4/If/Conditions/1/Images/0/X", -100_001, false },
        { "loop", "Actions/0/HoldMs", 0.5, false }, { "loop", "Actions/0/HoldMs", 60_001, false }, { "loop", "Actions/0/HoldMs", 60_000, true },
        { "loop", "Actions/0/Repeat", 0, false }, { "loop", "Actions/0/Repeat", 100_001, false },
        { "loop", "Actions/0/RepeatPauseMs", -1, false }, { "loop", "Actions/0/RepeatPauseMs", 86_400_001, false },
        { "loop", "Actions/1/ClickCount", 3, false }, { "loop", "Actions/1/X", 100_001, false },
        { "loop", "Actions/0/IntervalValue", 10, true }, { "loop", "Actions/0/IntervalValue", 86_400_000, true },
    };

    [Theory]
    [MemberData(nameof(MacroCases))]
    public void The_schema_and_the_checks_agree_on_a_fields_limits(string kind, string path, double value, bool valid)
    {
        bool loop = kind == "loop";
        Guid id = Guid.NewGuid();
        JsonNode item = JsonNode.Parse(loop ? LoopJson.Serialize(Loop()) : MacroJson.Serialize(Macro()))!;
        Set(item, path, value);
        string text = item.ToJsonString();
        string schema = loop ? CodeSchema.ForLoop(id) : CodeSchema.ForMacro(id);
        text = text.Replace(item["Id"]!.GetValue<string>(), id.ToString());

        object? read = loop ? LoopJson.Parse(text, id, NoConflict, out var problems) : MacroJson.Parse(text, id, NoConflict, out problems);
        List<string> schemaErrors = MiniSchema.Validate(schema, text);

        Assert.True((read != null) == valid, $"Checks: {string.Join(" / ", problems.Select(p => p.Message))}");
        Assert.True((schemaErrors.Count == 0) == valid, $"Schema: {string.Join(" / ", schemaErrors)}");
    }

    /// <summary>
    /// A loop's interval: the checks take 10 ms to 24 h (<see cref="LoopAction.MinIntervalMs"/>, <see cref="LoopAction.MaxIntervalMs"/>),
    /// in the row's unit. The schema says only "minimum 0": an AI app or the Code view's inline errors take 5 ms or 2 days as
    /// valid, and the save then refuses them.
    /// </summary>
    [Theory]
    [InlineData(5, "Milliseconds")]
    [InlineData(0, "Milliseconds")]
    [InlineData(86_400_001, "Milliseconds")]
    [InlineData(25, "Hours")]
    public void The_loop_schema_refuses_an_interval_the_checks_refuse(double value, string unit)
    {
        LoopDefinition loop = Loop();
        JsonNode item = JsonNode.Parse(LoopJson.Serialize(loop))!;
        item["Actions"]![0]!["IntervalValue"] = value;
        item["Actions"]![0]!["IntervalUnit"] = unit;
        string text = item.ToJsonString();

        Assert.Null(LoopJson.Parse(text, loop.Id, NoConflict, out _));
        Assert.NotEmpty(MiniSchema.Validate(CodeSchema.ForLoop(loop.Id), text));
    }

    /// <summary>
    /// A floating button's label: the checks count UTF-16 chars (string.Length, at most 2), JSON Schema's maxLength counts
    /// characters (code points). Two emoji are 2 for the schema, 4 for the checks: the schema takes a label the save refuses.
    /// </summary>
    [Fact]
    public void The_schema_counts_a_labels_length_as_the_checks_do()
    {
        MacroDefinition macro = Macro();
        macro.FloatingButton.Label = "😀😀";
        string text = MacroJson.Serialize(macro);

        Assert.Null(MacroJson.Parse(text, macro.Id, NoConflict, out _));
        Assert.NotEmpty(MiniSchema.Validate(CodeSchema.ForMacro(macro.Id), text));
    }

    /// <summary>The validator of these tests refuses what a schema refuses (it is not an empty check).</summary>
    [Fact]
    public void The_validator_refuses_unknown_fields_wrong_types_and_enum_values()
    {
        MacroDefinition macro = Macro();
        JsonNode item = JsonNode.Parse(MacroJson.Serialize(macro))!;
        item["Unknown"] = 1;
        item["Speed"] = 3;
        item["Actions"]![0]!["Type"] = "Press";
        item["Actions"]![4]!["If"]!["Then"] = new JsonArray(new JsonObject { ["Vk"] = "A" });

        var errors = MiniSchema.Validate(CodeSchema.ForMacro(macro.Id), item.ToJsonString());

        Assert.Contains(errors, e => e.StartsWith("Unknown"));
        Assert.Contains(errors, e => e.StartsWith("Speed"));
        Assert.Contains(errors, e => e.StartsWith("Actions[0].Type"));
        Assert.Contains(errors, e => e.StartsWith("Actions[4].If.Then[0].Vk"));
    }

    private static void Set(JsonNode root, string path, double value)
    {
        string[] parts = path.Split('/');
        JsonNode node = root;
        foreach (string part in parts[..^1])
            node = int.TryParse(part, out int i) ? node[i]! : node[part]!;
        JsonNode number = value == Math.Floor(value) ? JsonValue.Create((long)value) : JsonValue.Create(value);
        node[parts[^1]] = number;
    }
}
