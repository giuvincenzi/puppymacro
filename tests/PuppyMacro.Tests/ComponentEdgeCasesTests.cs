using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Components as functions, a second look from other sides: a component used only as a condition and deleted, Save as
/// component in a component that has a parameter named like the place it makes, a parameter renamed and a new one with its
/// old name, the Library and macros saved together when a file can't take its place, files with null where a list goes
/// (library.json, a .puppyitems file, a Code view), two collections of a file with one component, and an infinite number.
/// </summary>
public class ComponentEdgeCasesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LibraryComponent Finder(string name)
    {
        var bar = ImageFinder("Health bar");
        var component = Comp(name, If(bar, Press()));
        component.Returns = new ComponentReturns { Found = bar.Id };
        return component;
    }

    // ================= Deleting =================

    [Fact]
    public void A_component_a_macro_uses_as_a_condition_can_t_be_deleted_nor_its_collection()
    {
        var low = Finder("Health low");
        var collection = new LibraryCollection { Name = "D4", Components = { low } };
        // The macro (an open editor's, not saved yet, counts too) uses it only as a Component condition, no linked row.
        var heal = Macro("Heal", If(CallCondition(low, "Low"), Press()));

        Assert.NotNull(ComponentLibrary.CanDeleteComponent(low, new[] { heal }));
        Assert.NotNull(ComponentLibrary.CanDeleteCollection(collection, new[] { heal }));
    }

    // ================= Save as component =================

    [Fact]
    public void Save_as_component_inside_a_component_keeps_a_found_place_apart_from_a_parameter_of_the_same_name()
    {
        // A component (made by an earlier Save as component) has a Point parameter "Remove_Affix"; inside it an If finds
        // "Remove Affix" and a click goes where it was found. Saving that click as a component makes a place parameter
        // named from the condition: "Remove_Affix" too.
        var finder = ImageFinder("Remove Affix");
        var click = Click(10, 10, finder.Id);
        var outer = new[] { SlotParam("Remove_Affix") };

        var (actions, parameters, arguments) = ComponentEdits.Lift(new[] { click }, outer,
            new Dictionary<Guid, string> { [finder.Id] = "Remove Affix" });

        // The click must go where the If around it found the image, not to the outer component's parameter.
        string bound = actions[0].Bind!["Point"];
        Assert.True(JsonNode.DeepEquals(Found(finder.Id), arguments![bound]),
            $"the click is bound to {bound}, which the row gives {arguments[bound]?.ToJsonString()}; parameters: " +
            string.Join(", ", parameters.Select(p => p.Name)));
    }

    // ================= Guided update =================

    [Fact]
    public void A_parameter_renamed_and_a_new_required_one_with_its_old_name_asks_a_value_for_the_new_one()
    {
        var before = Comp("Strip", new[] { SlotParam("Slot") }, Bound(Click(), ("Point", "Slot")));
        var after = before.Clone();
        after.Parameters[0].Name = "Row";
        after.Actions[0].Bind = new() { ["Point"] = "Row" };
        after.Parameters.Add(SlotParam("Slot")); // new, required
        after.Actions.Add(Bound(Click(30, 40), ("Point", "Slot")));
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>(),
            new Dictionary<string, string> { ["Slot"] = "Row" });

        // The call's "Slot" value goes to Row: the new Slot has none, so the update must ask one.
        Assert.Contains(update.NeedValue, p => p.Name == "Slot");
        var (macros, _) = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(),
            new Dictionary<string, JsonNode> { ["Slot"] = Point(5, 6) }, after);
        Assert.Empty(Problems(new[] { after }, macros.ToArray()));
    }

    // ================= Saving together =================

    [Fact]
    public void When_a_file_can_t_take_its_place_saving_together_fails_and_changes_nothing()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        macros.Library.Collections.Add(new LibraryCollection { Name = "D4", Components = { strip } });
        Assert.True(macros.Library.TrySave(out _));
        string libraryBefore = File.ReadAllText(_folder.File("library.json"));
        var macro = Macro("D4", Call(strip, ("Slot", Point(1, 2))));
        // The macro's file is written next to its place, but its place is taken (a folder, a file kept open): the move fails.
        Directory.CreateDirectory(Path.Combine(_folder.File("macros"), $"{macro.Id}.json"));

        macros.Library.Collections[0].Name = "Changed";
        bool saved = true;
        var thrown = Record.Exception(() => saved = macros.TrySaveTogether(new[] { macro }, out _));

        Assert.Null(thrown);
        Assert.False(saved);
        Assert.Equal(libraryBefore, File.ReadAllText(_folder.File("library.json")));
    }

    // ================= null where a list goes =================

    private const string CollectionId = "8f1c3a52-6a4c-4d8e-9f00-000000000001";

    [Theory]
    [InlineData("{\"Format\":1,\"Collections\":null}")]
    [InlineData("{\"Format\":1,\"Collections\":[{\"Id\":\"" + CollectionId + "\",\"Name\":\"D4\",\"Components\":null}]}")]
    [InlineData("{\"Format\":1,\"Collections\":[{\"Id\":\"" + CollectionId + "\",\"Name\":\"D4\",\"Components\":[{\"Name\":\"Strip\",\"Concept\":\"strip\",\"Parameters\":null,\"Actions\":[]}]}]}")]
    [InlineData("{\"Format\":1,\"Collections\":[{\"Id\":\"" + CollectionId + "\",\"Name\":\"D4\",\"Components\":[{\"Name\":\"Strip\",\"Concept\":\"strip\",\"Parameters\":[],\"Actions\":null}]}]}")]
    public void A_library_file_with_null_for_a_list_is_read_or_kept_as_unreadable_never_a_crash(string text)
    {
        File.WriteAllText(_folder.File("library.json"), text);
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));

        // What the start does with it: read it, then the engine's snapshot (signatures, prepared components, images kept).
        var thrown = Record.Exception(() =>
        {
            macros.Library.Load(out _);
            _ = EngineSnapshot.From(new AppSettings(), macros);
            macros.ReleaseUnusedImages();
        });

        Assert.Null(thrown);
    }

    [Theory]
    [InlineData("{\"Format\":1,\"Items\":[],\"Collections\":[null]}")]
    [InlineData("{\"Format\":1,\"Items\":[],\"Collections\":[{\"Id\":\"" + CollectionId + "\",\"Name\":\"D4\",\"Components\":null}]}")]
    public void A_shared_file_with_null_for_a_collection_or_its_components_is_refused_never_a_crash(string text)
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        ItemFileContent? content = null;

        var thrown = Record.Exception(() => content = ItemFile.Read(text, new AppSettings(), macros, new Version(1, 17, 0)));

        Assert.Null(thrown);
        Assert.NotNull(content!.Error);
    }

    [Theory]
    [InlineData("Parameters")]
    [InlineData("Actions")]
    public void A_null_in_a_component_s_list_is_a_problem_of_its_Code_view_never_a_crash(string list)
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        var json = JsonNode.Parse(CodeJson.Serialize(strip))!.AsObject();
        json[list]!.AsArray().Add(null);
        List<CodeProblem>? problems = null;

        var thrown = Record.Exception(() => MacroJson.ParseComponent(json.ToJsonString(), strip.Id, out problems, null, Array.Empty<LibraryComponent>()));

        Assert.Null(thrown);
        Assert.NotEmpty(problems!);
    }

    [Fact]
    public void A_null_in_a_macro_s_actions_is_a_problem_of_its_Code_view_never_a_crash()
    {
        var macro = Macro("D4", Press());
        var json = JsonNode.Parse(MacroJson.Serialize(macro))!.AsObject();
        json["Actions"]!.AsArray().Add(null);
        List<CodeProblem>? problems = null;

        var thrown = Record.Exception(() => MacroJson.Parse(json.ToJsonString(), macro.Id, (_, _) => null, out problems));

        Assert.Null(thrown);
        Assert.NotEmpty(problems!);
    }

    // ================= Files =================

    [Fact]
    public void A_file_whose_two_collections_hold_one_component_is_refused()
    {
        var press = Comp("Press", Press());
        var first = new LibraryCollection { Name = "D4", Components = { press } };
        var second = new LibraryCollection { Name = "D4 copy", Components = { press.Clone() } }; // the same component Id
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        string text = ItemFile.Write(Array.Empty<object>(), new Version(1, 17, 0), collections: new[] { first, second });

        var content = ItemFile.Read(text, new AppSettings(), macros, new Version(1, 17, 0));

        // A component lives in one collection: importing both would put one Id in two places of the Library.
        Assert.NotNull(content.Error);
        Assert.Empty(content.NewCollections);
    }

    // ================= Values =================

    [Fact]
    public void An_infinite_number_is_not_a_value_and_never_a_crash()
    {
        string? problem = null;
        object? value = null;
        var thrown = Record.Exception(() => value = ComponentValues.Read(JsonValue.Create(double.PositiveInfinity), ParameterType.Number,
            ValueRule.Any, out problem));
        Assert.Null(thrown);
        Assert.Null(value);
        Assert.NotNull(problem);

        // The value editor's number (an Example, a Default, an argument), as its NumberBox sets it.
        var editor = new ValueEditorViewModel(ParameterType.Number, NumberUnit.Ms, Number(30), null) { Number = double.PositiveInfinity };
        string? shown = null;
        thrown = Record.Exception(() => shown = editor.Problem);
        Assert.Null(thrown);
        Assert.NotNull(shown);
    }
}
