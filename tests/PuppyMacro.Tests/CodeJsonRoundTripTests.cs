using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Generated loops, macros and remaps (<see cref="CodeJsonGenerator"/>), every action type and field inside the editors'
/// limits: each one's Code view text reads back without problems as the same item, the data files read it back the same,
/// and the JSON Schema the Code view and AI apps get (<see cref="CodeSchema"/>) accepts it.
/// </summary>
public class CodeJsonRoundTripTests
{
    private const int Count = 300;

    private static string? NoConflict(HotkeyBinding binding, string? appExe) => null;

    private static string Show(List<CodeProblem> problems) => string.Join("\n", problems.Select(p => $"{p.Line}:{p.Column} {p.Message}"));

    [Fact]
    public void Every_generated_macro_reads_back_as_the_same_macro_and_fits_its_schema()
    {
        var generator = new CodeJsonGenerator(1);
        for (int i = 0; i < Count; i++)
        {
            MacroDefinition macro = generator.Macro();
            string text = MacroJson.Serialize(macro);

            MacroDefinition? read = MacroJson.Parse(text, macro.Id, NoConflict, out var problems);

            Assert.True(read != null, $"Macro {i}:\n{Show(problems)}\n{text}");
            Assert.Equal(text, MacroJson.Serialize(read!));
            Assert.Equal(text, MacroJson.Serialize(CodeJson.ReadFile<MacroDefinition>(text)!));
            Assert.Empty(MiniSchema.Validate(CodeSchema.ForMacro(macro.Id), text));
            Assert.Empty(MiniSchema.Validate(CodeSchema.ForMacro(macro.Id, withDefaults: true), text));
        }
    }

    [Fact]
    public void Every_generated_action_reads_back_in_the_action_window_and_fits_its_schema()
    {
        var generator = new CodeJsonGenerator(2);
        string schema = CodeSchema.ForAction(new Guid?[] { null }, new Dictionary<Guid, string>(), null);
        for (int i = 0; i < Count; i++)
        {
            MacroAction action = generator.Action(depth: 0, places: new List<Guid>());
            string text = MacroJson.Serialize(action);

            MacroAction? read = MacroJson.ParseAction(text, new Guid?[] { null }, new Dictionary<Guid, string>(), out var problems);

            Assert.True(read != null, $"Action {i}:\n{Show(problems)}\n{text}");
            Assert.Equal(text, MacroJson.Serialize(read!));
            Assert.Empty(MiniSchema.Validate(schema, text));
        }
    }

    [Fact]
    public void Every_generated_loop_reads_back_as_the_same_loop_and_fits_its_schema()
    {
        var generator = new CodeJsonGenerator(3);
        for (int i = 0; i < Count; i++)
        {
            LoopDefinition loop = generator.Loop();
            string text = LoopJson.Serialize(loop);

            LoopDefinition? read = LoopJson.Parse(text, loop.Id, NoConflict, out var problems);

            Assert.True(read != null, $"Loop {i}:\n{Show(problems)}\n{text}");
            Assert.Equal(text, LoopJson.Serialize(read!));
            Assert.Empty(MiniSchema.Validate(CodeSchema.ForLoop(loop.Id), text));
        }
    }

    [Fact]
    public void Every_generated_remap_reads_back_as_the_same_remap_and_fits_its_schema()
    {
        var generator = new CodeJsonGenerator(4);
        for (int i = 0; i < Count; i++)
        {
            RemapDefinition remap = generator.Remap();
            string text = RemapJson.Serialize(remap);

            RemapDefinition? read = RemapJson.Parse(text, remap.Id, NoConflict, out var problems);

            Assert.True(read != null, $"Remap {i}:\n{Show(problems)}\n{text}");
            Assert.Equal(text, RemapJson.Serialize(read!));
            Assert.Empty(MiniSchema.Validate(CodeSchema.ForRemap(remap.Id), text));
        }
    }
}
