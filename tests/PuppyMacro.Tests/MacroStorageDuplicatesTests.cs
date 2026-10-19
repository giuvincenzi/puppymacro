using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// A JSON object with a name twice ({"X": 1, "X": 2}) inside a call's argument: System.Text.Json reads it into a JsonNode
/// that throws ArgumentException where its properties are first read (ItemFile.Read refuses such a file for that reason).
/// </summary>
public class MacroStorageDuplicatesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static readonly LibraryComponent ClickSlot =
        Comp("Click slot", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));

    private static string MacroText(Guid id) => $$"""
        {
          "Id": "{{id}}",
          "Name": "Twice",
          "Actions": [
            { "Type": "Component", "ComponentId": "{{ClickSlot.Id}}", "Arguments": { "Slot": { "X": 10, "X": 20, "Y": 30 } } }
          ]
        }
        """;

    /// <summary>
    /// The Code view (and an AI app's save, which goes through the same checks) gets a macro whose argument has "X" twice.
    /// Expected: a problem at its line (as for any JSON the model can't take), never an exception.
    /// </summary>
    [Fact]
    public void The_code_view_shows_a_name_twice_in_an_argument_as_a_problem()
    {
        Guid id = Guid.NewGuid();
        System.Collections.Generic.List<CodeProblem> problems = new();
        MacroDefinition? read = null;
        var thrown = Record.Exception(() => read = MacroJson.Parse(MacroText(id), id, (_, _) => null, out problems,
            components: new ComponentSignatures(new[] { ClickSlot })));

        Assert.Null(thrown);
        Assert.Null(read);
        Assert.NotEmpty(problems);
    }

    /// <summary>
    /// The same macro in its file (edited by hand, or written by another tool), its component in the Library. At start
    /// MacroLibrary.Load reads it with no exception (the JsonNode is read lazily), then MainWindow's constructor builds the
    /// engine's snapshot (EngineSnapshot.From, MainWindow.xaml.cs:75), whose ComponentChecks.Unplayable reads the argument
    /// and throws ArgumentException: PuppyMacro can't open its window, at every start, until the file is fixed by hand.
    /// Expected: the file is skipped with a warning at load (as any file it can't read), or its macro can't play; never an
    /// exception.
    /// </summary>
    [Fact]
    public void A_macro_file_with_a_name_twice_in_an_argument_never_stops_the_app_from_starting()
    {
        string folder = _folder.File("macros");
        Directory.CreateDirectory(folder);
        Guid id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(folder, $"{id}.json"), MacroText(id));
        var macros = new MacroLibrary(folder);
        macros.Library.Collections.Add(new LibraryCollection { Name = "Slots", Components = { ClickSlot } });
        var settings = AppSettings.CreateDefault();

        var thrown = Record.Exception(() =>
        {
            macros.Load(Array.Empty<Guid>(), out _);
            EngineSnapshot.From(settings, macros);
        });

        Assert.Null(thrown);
    }
}
