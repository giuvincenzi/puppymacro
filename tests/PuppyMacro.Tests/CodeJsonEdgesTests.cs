using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The Code views' strict reading at its edges: nesting, repeated properties, where a problem is shown.</summary>
public class CodeJsonEdgesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string? NoConflict(HotkeyBinding binding, string? appExe) => null;

    /// <summary>A macro of <paramref name="levels"/> Ifs, each inside the Then of the one before, a Press key in the last.</summary>
    private static MacroDefinition Nested(int levels)
    {
        var inner = new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 };
        for (int i = 0; i < levels; i++)
        {
            inner = new MacroAction
            {
                Type = MacroActionType.If,
                If = new MacroIf
                {
                    Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 1, Color = "#000000" } },
                    Then = { inner },
                },
            };
        }
        return new MacroDefinition { Name = "Deep", Actions = { inner } };
    }

    /// <summary>
    /// Nothing limits how deep Ifs go inside each other's Then (the editor adds an If inside a Then at any depth), but the
    /// macro files and the Code view are written with System.Text.Json's default MaxDepth of 64: at 21 Ifs (fewer with Image
    /// conditions or call arguments) MacroJson.Serialize throws "A possible object cycle was detected". MacroLibrary.TrySave
    /// catches only IOException and UnauthorizedAccessException, so the editor's Save (and the Code view, the History, an
    /// export) ends in an unhandled JsonException instead of saving the macro or saying why it can't.
    /// </summary>
    [Fact]
    public void A_macro_with_ifs_nested_21_deep_is_saved_or_refused_without_an_exception()
    {
        MacroDefinition macro = Nested(21);
        var library = new MacroLibrary(_folder.Path);

        Exception? thrown = Record.Exception(() => library.TrySave(macro, out _));

        Assert.Null(thrown);
    }

    /// <summary>The same macro, in its Code view: the text is made, or the problem is said.</summary>
    [Fact]
    public void A_macro_with_ifs_nested_21_deep_shows_in_the_code_view()
    {
        MacroDefinition macro = Nested(21);

        Exception? thrown = Record.Exception(() => MacroJson.Serialize(macro));

        Assert.Null(thrown);
    }

    /// <summary>20 Ifs inside each other still save, read back and pass the checks (the limit above is the JSON depth only).</summary>
    [Fact]
    public void A_macro_with_ifs_nested_20_deep_reads_back_unchanged()
    {
        MacroDefinition macro = Nested(20);
        string text = MacroJson.Serialize(macro);

        MacroDefinition? read = MacroJson.Parse(text, macro.Id, NoConflict, out var problems);

        Assert.Empty(problems);
        Assert.Equal(text, MacroJson.Serialize(read!));
    }

    /// <summary>
    /// The Code views read "strictly": every value the Form view could not produce is a problem at its line. A property
    /// written twice ("Vk": 65, then "Vk": 66, as an AI app or a paste can write it) is not one: System.Text.Json keeps the
    /// last value and the first one is dropped without a word (.NET 10's JsonSerializerOptions.AllowDuplicateProperties
    /// is left at true in CodeJson.StrictOptions).
    /// </summary>
    [Fact]
    public void A_property_written_twice_is_a_problem()
    {
        var macro = new MacroDefinition { Name = "Twice", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        string text = MacroJson.Serialize(macro).Replace("\"Vk\": 65,", "\"Vk\": 65,\n      \"Vk\": 66,");
        Assert.Contains("\"Vk\": 66", text);

        MacroDefinition? read = MacroJson.Parse(text, macro.Id, NoConflict, out var problems);

        Assert.Null(read);
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[0].Vk"));
    }

    /// <summary>
    /// An unknown property whose name has an apostrophe ("Don't") is named and placed wrong: the name is cut at the first
    /// quote of .NET's message ("Don"), and that path is not in the text, so the problem goes to line 1 instead of the
    /// property's line.
    /// </summary>
    [Fact]
    public void An_unknown_property_with_an_apostrophe_is_named_and_placed_right()
    {
        var macro = new MacroDefinition { Name = "Quote", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        string text = MacroJson.Serialize(macro).Replace("\"Name\": \"Quote\",", "\"Name\": \"Quote\",\n  \"Don't\": 1,");
        int line = text.Split('\n').ToList().FindIndex(l => l.Contains("Don't")) + 1;

        Assert.Null(MacroJson.Parse(text, macro.Id, NoConflict, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.Contains("Don't", problem.Message);
        Assert.Equal(line, problem.Line);
    }

    // ---- Passing: what the strict reading already gets right ----

    /// <summary>Enums only by their exact name: not in another case, not as a number.</summary>
    [Theory]
    [InlineData("\"presskey\"")]
    [InlineData("0")]
    [InlineData("\"PRESSKEY\"")]
    public void An_enum_is_read_only_by_its_exact_name(string value)
    {
        var macro = new MacroDefinition { Name = "Enum", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } } };
        string text = MacroJson.Serialize(macro).Replace("\"PressKey\"", value);

        Assert.Null(MacroJson.Parse(text, macro.Id, NoConflict, out var problems));
        Assert.Contains(problems, p => p.Message.StartsWith("Actions[0].Type"));
    }

    /// <summary>A null where the model has none, at any depth (a path point, an If's conditions, a Bind's value), is a problem at its path.</summary>
    [Theory]
    [InlineData("{\"Type\": \"MovePath\", \"Path\": [null]}", "Actions[0].Path[0]")]
    [InlineData("{\"Type\": \"If\", \"If\": {\"Conditions\": null}}", "Actions[0].If.Conditions")]
    [InlineData("{\"Type\": \"PressKey\", \"Vk\": 65, \"Bind\": {\"Key\": null}}", "Actions[0].Bind.Key")]
    [InlineData("{\"Type\": \"Scroll\", \"ScrollDirection\": null}", "Actions[0].ScrollDirection")]
    [InlineData("{\"Type\": \"If\", \"If\": {\"Conditions\": [{\"Kind\": \"Pixel\", \"Color\": \"#000000\"}], \"Then\": [null]}}", "Actions[0].If.Then[0]")]
    public void A_misplaced_null_is_a_problem_at_its_path(string action, string path)
    {
        Guid id = Guid.NewGuid();
        string text = $"{{\n  \"Id\": \"{id}\",\n  \"Name\": \"N\",\n  \"Actions\": [\n    {action}\n  ]\n}}";

        Assert.Null(MacroJson.Parse(text, id, NoConflict, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.StartsWith(path + ":", problem.Message);
        Assert.Equal(5, problem.Line);
    }

    /// <summary>A number too big for its field, a string for a number, an array for an object: wrong type, at the value's line.</summary>
    [Theory]
    [InlineData("\"Vk\": 99999999999")]
    [InlineData("\"Vk\": \"65\"")]
    [InlineData("\"Vk\": 65.5")]
    [InlineData("\"Vk\": true")]
    public void A_value_of_the_wrong_type_is_a_problem_at_its_line(string vk)
    {
        Guid id = Guid.NewGuid();
        string text = $"{{\n  \"Id\": \"{id}\",\n  \"Name\": \"N\",\n  \"Actions\": [\n    {{\"Type\": \"PressKey\",\n {vk}}}\n  ]\n}}";

        Assert.Null(MacroJson.Parse(text, id, NoConflict, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.StartsWith("Actions[0].Vk:", problem.Message);
        Assert.Equal(6, problem.Line);
    }

    /// <summary>The column of a problem counts characters, not UTF-8 bytes: an emoji before it is one or two columns, not four.</summary>
    [Fact]
    public void A_problems_column_counts_characters_after_an_emoji()
    {
        Guid id = Guid.NewGuid();
        string line = "    {\"Type\": \"PasteText\", \"Text\": \"😀\", \"Vk\": \"x\"}";
        string text = $"{{\n  \"Id\": \"{id}\",\n  \"Name\": \"N\",\n  \"Actions\": [\n{line}\n  ]\n}}";

        Assert.Null(MacroJson.Parse(text, id, NoConflict, out var problems));

        CodeProblem problem = Assert.Single(problems);
        Assert.Equal(5, problem.Line);
        Assert.Equal(line.IndexOf("\"Vk\"", StringComparison.Ordinal) + 1, problem.Column);
    }
}
