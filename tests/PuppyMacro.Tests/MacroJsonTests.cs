using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class MacroJsonTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly MacroDefinition _macro;

    public MacroJsonTests()
    {
        var group = new MacroGroup { Name = "Type hi" };
        _macro = new MacroDefinition
        {
            Name = "Sample: type and confirm",
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x48, GroupId = group.Id },
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x49, DelayMs = 50, GroupId = group.Id },
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x0D, DelayMs = 100, Name = "Send" },
            },
            Groups = { group },
        };
    }

    public void Dispose() => _folder.Dispose();

    private static string? NoConflict(HotkeyBinding binding) => null;

    private MacroDefinition? Parse(string text, out List<CodeProblem> problems, Func<HotkeyBinding, string?>? conflict = null) =>
        MacroJson.Parse(text, _macro.Id, conflict ?? NoConflict, out problems);

    /// <summary>1-based line of the first line containing <paramref name="fragment"/>.</summary>
    private static int LineOf(string text, string fragment, int skip = 0) =>
        text.Split('\n').Select((line, i) => (line, i)).Where(x => x.line.Contains(fragment)).Skip(skip).First().i + 1;

    [Fact]
    public void The_code_is_exactly_the_saved_file()
    {
        var library = new MacroLibrary(_folder.Path);
        Assert.True(library.TrySave(_macro, out _));

        string file = File.ReadAllText(_folder.File($"{_macro.Id}.json"));

        Assert.Equal(file, MacroJson.Serialize(_macro));
    }

    [Fact]
    public void Unchanged_code_reads_back_without_problems()
    {
        string text = MacroJson.Serialize(_macro);

        MacroDefinition? read = Parse(text, out var problems);

        Assert.Empty(problems);
        Assert.NotNull(read);
        Assert.Equal(text, MacroJson.Serialize(read!));
    }

    [Fact]
    public void Changing_the_Id_is_a_problem_on_its_line()
    {
        string text = MacroJson.Serialize(_macro).Replace(_macro.Id.ToString(), Guid.NewGuid().ToString());

        Assert.Null(Parse(text, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.Equal(LineOf(text, "\"Id\""), problem.Line);
        Assert.Contains("cannot be changed", problem.Message);
    }

    [Fact]
    public void Broken_JSON_is_a_problem_on_its_line()
    {
        string text = MacroJson.Serialize(_macro).Replace("\"DelayMs\": 50,", "\"DelayMs\": 50");

        Assert.Null(Parse(text, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.InRange(problem.Line, LineOf(text, "\"DelayMs\": 50"), LineOf(text, "\"DelayMs\": 50") + 1);
    }

    [Fact]
    public void Unknown_properties_and_wrong_values_are_problems_on_their_line()
    {
        string unknown = MacroJson.Serialize(_macro).Replace("\"Name\": \"Send\",", "\"Name\": \"Send\",\n      \"Colour\": \"red\",");
        Assert.Null(Parse(unknown, out var problems));
        CodeProblem problem = Assert.Single(problems);
        Assert.Equal(LineOf(unknown, "\"Colour\""), problem.Line);
        Assert.Contains("Unknown property \"Colour\"", problem.Message);

        string wrongEnum = MacroJson.Serialize(_macro).Replace("\"Type\": \"PressKey\"", "\"Type\": \"Presskey\"");
        Assert.Null(Parse(wrongEnum, out problems));
        Assert.Equal(LineOf(wrongEnum, "\"Presskey\""), Assert.Single(problems).Line);

        string number = MacroJson.Serialize(_macro).Replace("\"Repeat\": \"Once\"", "\"Repeat\": 0");
        Assert.Null(Parse(number, out problems));
        Assert.Single(problems);
    }

    [Fact]
    public void Values_out_of_the_editor_limits_are_problems()
    {
        _macro.Actions[1].Repeat = 0;
        _macro.Speed = 3;
        _macro.SoundName = "Missing";
        string text = MacroJson.Serialize(_macro);

        Assert.Null(Parse(text, out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith("Actions[1].Repeat:") && p.Line == LineOf(text, "\"Repeat\": 0"));
        Assert.Contains(problems, p => p.Message.StartsWith("Speed:"));
        Assert.Contains(problems, p => p.Message.StartsWith("SoundName:"));
    }

    [Fact]
    public void Groups_must_exist_and_keep_their_actions_together()
    {
        Guid groupId = _macro.Groups[0].Id;
        _macro.Actions[1].GroupId = null;
        _macro.Actions[2].GroupId = groupId;             // H, (none), Send: the group is split
        _macro.Actions[0].Name = "first";
        string split = MacroJson.Serialize(_macro);
        Assert.Null(Parse(split, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[2].GroupId:") && p.Message.Contains("next to each other"));

        _macro.Actions[2].GroupId = Guid.NewGuid();
        Assert.Null(Parse(MacroJson.Serialize(_macro), out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[2].GroupId:") && p.Message.Contains("No group"));
    }

    [Fact]
    public void Hotkeys_follow_the_same_rules_as_the_list_view()
    {
        _macro.Mode = ActivationMode.Hold;
        Assert.Null(Parse(MacroJson.Serialize(_macro), out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Mode:") && p.Message.Contains("Hold needs a hotkey"));

        _macro.Mode = ActivationMode.Toggle;
        _macro.Hotkey = HotkeyBinding.FromKey(KeyNames.VK_LBUTTON);
        Assert.Null(Parse(MacroJson.Serialize(_macro), out problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Hotkey:") && p.Message.Contains("Left and right click"));

        _macro.Hotkey = new HotkeyBinding { Vk = KeyNames.VK_LBUTTON, Ctrl = true };
        Assert.NotNull(Parse(MacroJson.Serialize(_macro), out problems));

        Assert.Null(Parse(MacroJson.Serialize(_macro), out problems, _ => "Ctrl + LButton is already used by Stop all."));
        Assert.Contains(problems, p => p.Message == "Hotkey: Ctrl + LButton is already used by Stop all.");
    }

    [Fact]
    public void A_floating_button_is_not_available_with_Hold()
    {
        _macro.Mode = ActivationMode.Hold;
        _macro.Hotkey = HotkeyBinding.FromKey(0x75);
        _macro.FloatingButton.Enabled = true;

        Assert.Null(Parse(MacroJson.Serialize(_macro), out var problems));

        Assert.Contains(problems, p => p.Message.StartsWith("FloatingButton.Enabled:"));
    }

    [Fact]
    public void The_schema_lists_the_values_and_forbids_unknown_properties()
    {
        JsonObject schema = JsonNode.Parse(MacroSchema.Build(_macro.Id))!.AsObject();
        string text = schema.ToJsonString();

        Assert.Contains("\"enumDescriptions\"", text);
        Assert.Contains("\"PasteText\"", text);
        Assert.Contains("\"additionalProperties\":false", text);
        Assert.StartsWith("The macro's id", schema["properties"]!["Id"]!["description"]!.GetValue<string>());
        // A deleted Id comes back with Ctrl+Space: its value is the default and the only one allowed.
        Assert.Equal(_macro.Id.ToString(), schema["properties"]!["Id"]!["default"]!.GetValue<string>());
        Assert.Equal(_macro.Id.ToString(), Assert.Single(schema["properties"]!["Id"]!["enum"]!.AsArray())!.GetValue<string>());
        JsonObject speed = schema["properties"]!["Speed"]!.AsObject();
        Assert.Equal(5, speed["enum"]!.AsArray().Count);
    }
}
