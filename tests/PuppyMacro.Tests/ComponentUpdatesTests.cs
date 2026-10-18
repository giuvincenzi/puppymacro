using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// What changes components and their calls: the guided update when a signature changes (removed, renamed, new required,
/// default taken away, type changed; references kept; nothing for no calls; what the checks still refuse after it), Unlink
/// (values written, {"From"} kept bound, {"Found"} made "Where … was found", one with no equivalent refused, calls inside
/// given the row's values, new condition ids), Save as component (places found outside and parameters of the component
/// around made parameters), new condition ids following into {"Found"} arguments, the fingerprint with the signature, the
/// images of arguments and parameters, a file carrying the collections its components call, an import checked with the
/// file's collections, and the Library and macros saved together or not at all.
/// </summary>
public class ComponentUpdatesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LibraryComponent Strip(params ComponentParameter[] parameters) => Comp("Strip one slot",
        parameters.Length > 0 ? parameters : new[] { SlotParam() },
        Bound(Click(3912, 1056), ("Point", "Slot")));

    private static LibraryComponent Changed(LibraryComponent component, Action<LibraryComponent> change)
    {
        var copy = component.Clone();
        change(copy);
        return copy;
    }

    // ================= Guided update =================

    [Fact]
    public void A_signature_with_new_optional_parameters_changes_no_call()
    {
        var before = Strip();
        var after = Changed(before, c => c.Parameters.Add(HoldParam(@default: Number(30))));
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());

        Assert.False(update.Needed);
        Assert.Empty(update.NeedValue);
        Assert.Equal(1, update.Calls); // the calls are known, nothing to change in them
    }

    [Fact]
    public void A_new_required_parameter_asks_one_value_for_every_call_that_has_none()
    {
        var before = Strip();
        var after = Changed(before, c => c.Parameters.Add(HoldParam("Settle")));
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2))), Call(before, ("Slot", Point(3, 4))));
        var other = Comp("Strip twice", Call(before, ("Slot", Point(5, 6))), Call(before, ("Slot", Point(7, 8))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { other, after });

        Assert.True(update.Needed);
        Assert.Equal("Settle", Assert.Single(update.NeedValue).Name);
        Assert.Equal(4, update.Calls);
        Assert.Equal(new[] { ("D4", true, 2), ("Strip twice", false, 2) }, update.Sites.Select(s => (s.OwnerName, s.IsMacro, s.Calls)));

        var (macros, components) = update.Apply(new[] { macro }, new[] { other }, new Dictionary<string, JsonNode> { ["Settle"] = Number(100) }, after);
        Assert.All(SignatureUpdate.CallsTo(macros.Single().Actions, before.Id).Concat(SignatureUpdate.CallsTo(components.Single().Actions, before.Id)),
            arguments => Assert.True(JsonNode.DeepEquals(Number(100), arguments!["Settle"])));
        // The values each call had stay.
        Assert.True(JsonNode.DeepEquals(Point(3, 4), macros.Single().Actions[1].Arguments!["Slot"]));
        // Copies: what is saved changes only when the caller saves them.
        Assert.Null(macro.Actions[0].Arguments!.GetValueOrDefault("Settle"));
        Assert.Empty(Problems(new[] { after, components.Single() }, macros.Single()));
    }

    [Fact]
    public void A_removed_parameter_goes_from_every_call_and_a_renamed_one_follows()
    {
        var before = Strip(SlotParam(), HoldParam("Speed", Number(30)));
        var after = Changed(before, c =>
        {
            c.Parameters.RemoveAll(p => p.Name == "Speed");
            c.Parameters[0].Name = "Place";
            c.Actions[0].Bind!["Point"] = "Place";
        });
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2)), ("Speed", Number(50))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>(),
            new Dictionary<string, string> { ["Slot"] = "Place" });

        Assert.Equal(new[] { "Speed" }, update.Removed);
        Assert.Equal("Place", update.Renamed["Slot"]);
        Assert.Empty(update.NeedValue);
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode>(), after).Macros.Single();
        Assert.Equal(new[] { "Place" }, changed.Actions[0].Arguments!.Keys);
        Assert.True(JsonNode.DeepEquals(Point(1, 2), changed.Actions[0].Arguments!["Place"]));
        Assert.Empty(Problems(new[] { after }, changed));

        // Without knowing the rename, it is a parameter removed and a new one every call must give a value for.
        var unknown = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());
        Assert.Equal(new[] { "Slot", "Speed" }, unknown.Removed);
        Assert.Equal("Place", Assert.Single(unknown.NeedValue).Name);
    }

    [Fact]
    public void A_default_taken_away_gives_the_value_only_to_the_calls_that_left_it_out()
    {
        var before = Strip(SlotParam(), HoldParam(@default: Number(30)));
        var after = Changed(before, c => c.Parameters[1].Default = null);
        var leftOut = Call(before, ("Slot", Point(1, 2)));
        var given = Call(before, ("Slot", Point(3, 4)), ("Hold", Number(80)));
        var macro = Macro("D4", leftOut, given);

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());
        Assert.Equal("Hold", Assert.Single(update.NeedValue).Name);

        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode> { ["Hold"] = Number(45) }, after).Macros.Single();
        Assert.True(JsonNode.DeepEquals(Number(45), changed.Actions[0].Arguments!["Hold"]));
        Assert.True(JsonNode.DeepEquals(Number(80), changed.Actions[1].Arguments!["Hold"]));
    }

    [Fact]
    public void A_type_changed_replaces_every_value_and_keeps_references_for_the_checks_to_judge()
    {
        var before = Strip(SlotParam(), HoldParam());
        var after = Changed(before, c =>
        {
            c.Parameters[1].Unit = NumberUnit.Seconds;
            c.Parameters[1].Example = Number(1);
        });
        var macro = Macro("D4", Call(before, ("Slot", Point(1, 2)), ("Hold", Number(80))));
        var forwarding = Comp("Forward", new[] { SlotParam(), HoldParam("Time") }, Call(before, ("Slot", From("Slot")), ("Hold", From("Time"))));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { forwarding });
        Assert.Equal("Hold", Assert.Single(update.NeedValue).Name);
        Assert.Contains("Hold", update.TypeChangedNames);

        var (macros, components) = update.Apply(new[] { macro }, new[] { forwarding }, new Dictionary<string, JsonNode> { ["Hold"] = Number(2) }, after);
        Assert.True(JsonNode.DeepEquals(Number(2), macros.Single().Actions[0].Arguments!["Hold"]));
        Assert.True(JsonNode.DeepEquals(From("Time"), components.Single().Actions[0].Arguments!["Hold"]));
        // The forwarded parameter is in ms, the new one in seconds: refused, with where.
        Assert.Contains(Problems(new[] { after, components.Single() }), p => p.StartsWith("Forward: Actions[0].Arguments.Hold"));
    }

    [Fact]
    public void A_signature_nobody_calls_needs_no_update_and_the_component_itself_is_not_a_call_site()
    {
        var before = Strip();
        var after = Changed(before, c => c.Parameters.Add(HoldParam()));
        var update = SignatureUpdate.Compare(before, after, new[] { Macro("Other", Press()) }, new[] { after });
        Assert.False(update.Needed);
        Assert.Empty(update.Sites);
    }

    [Fact]
    public void Component_conditions_are_calls_too()
    {
        var pixel = Bound(Pixel(), ("Point", "Slot"));
        var before = Comp("Rare", new[] { SlotParam() }, If(pixel));
        before.Returns = new ComponentReturns { Found = pixel.Id };
        var after = Changed(before, c => c.Parameters.Add(HoldParam("Extra")));
        var macro = Macro("D4", If(CallCondition(before, "Rare", ("Slot", Point(1, 2))), Press()));

        var update = SignatureUpdate.Compare(before, after, new[] { macro }, Array.Empty<LibraryComponent>());
        Assert.Equal(1, update.Calls);
        var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), new Dictionary<string, JsonNode> { ["Extra"] = Number(5) }, after).Macros.Single();
        Assert.True(JsonNode.DeepEquals(Number(5), changed.Actions[0].If!.Conditions[0].Arguments!["Extra"]));
    }

    // ================= Unlink =================

    [Fact]
    public void Unlink_writes_the_row_s_values_in_the_fields_with_new_condition_ids()
    {
        var finder = ImageFinder("Remove Affix");
        var component = Comp("Strip", new[] { SlotParam(), HoldParam(@default: Number(30)) },
            If(Bound(Pixel(1, 1), ("Point", "Slot")), Bound(Click(1, 1), ("Point", "Slot"), ("HoldMs", "Hold"))),
            If(finder, Click(found: finder.Id)));
        var row = Call(component, ("Slot", Point(3986, 1056)));

        var actions = ComponentEdits.Unlink(row, component, out var problem)!;

        Assert.Null(problem);
        var pixel = actions[0].If!.Conditions[0];
        Assert.Equal((3986, 1056), (pixel.X, pixel.Y));
        Assert.Null(pixel.Bind);
        var click = actions[0].If!.Then[0];
        Assert.Equal((3986, 1056, 30d), (click.X, click.Y, click.HoldMs)); // Hold left out: its default
        Assert.Null(click.Bind);
        // New ids, and "Where … was found" follows.
        Assert.NotEqual(finder.Id, actions[1].If!.Conditions[0].Id);
        Assert.Equal(actions[1].If!.Conditions[0].Id, actions[1].If!.Then[0].FoundId);
        // The component stays as it was.
        Assert.Equal((1, 1), (component.Actions[0].If!.Conditions[0].X, component.Actions[0].If!.Conditions[0].Y));
        Assert.Empty(MacroProblems(Macro("D4", actions.ToArray())));
    }

    [Fact]
    public void Unlink_keeps_From_bound_and_makes_Found_a_Where_was_found()
    {
        var component = Comp("Strip", new[] { SlotParam() }, Bound(Click(1, 1), ("Point", "Slot")));
        var fromRow = Call(component, ("Slot", From("Where")));
        var bound = ComponentEdits.Unlink(fromRow, component, out _)!.Single();
        Assert.Equal("Where", bound.Bind!["Point"]);

        var finder = ImageFinder("Slot");
        var foundRow = Call(component, ("Slot", Found(finder.Id, 4, -2)));
        var click = ComponentEdits.Unlink(foundRow, component, out _)!.Single();
        Assert.Equal((finder.Id, 4, -2), (click.FoundId!.Value, click.FoundOffsetX, click.FoundOffsetY));
        Assert.Null(click.Bind);
        Assert.Empty(MacroProblems(Macro("D4", If(finder, click))));
    }

    [Fact]
    public void Unlink_refuses_a_found_place_for_a_pixel_s_point()
    {
        var component = Comp("Is rare", new[] { SlotParam() }, If(Bound(Pixel(), ("Point", "Slot")), Press()));
        var finder = ImageFinder("Slot");
        Assert.Null(ComponentEdits.Unlink(Call(component, ("Slot", Found(finder.Id))), component, out var problem));
        Assert.Equal(Strings.Library_UnlinkFoundPixel, problem);
    }

    [Fact]
    public void Unlink_is_one_level_the_calls_inside_get_the_row_s_values()
    {
        var press = Comp("Press", new[] { HoldParam() }, Bound(Press(), ("HoldMs", "Hold")));
        var outer = Comp("Outer", new[] { HoldParam("Time") }, Call(press, ("Hold", From("Time"))));

        var actions = ComponentEdits.Unlink(Call(outer, ("Time", Number(45))), outer, out _)!;

        var inner = Assert.Single(actions);
        Assert.Equal(MacroActionType.Component, inner.Type);
        Assert.Equal(press.Id, inner.ComponentId);
        Assert.True(JsonNode.DeepEquals(Number(45), inner.Arguments!["Hold"]));
        Assert.Empty(MacroProblems(Macro("D4", actions.ToArray()), press));
    }

    // ================= Save as component =================

    [Fact]
    public void Save_as_component_makes_a_place_found_outside_a_point_parameter()
    {
        var finder = ImageFinder("Remove Affix");
        var first = Click(10, 10, finder.Id);
        first.FoundOffsetX = 5;
        var second = Click(20, 20, finder.Id);
        second.FoundOffsetX = 5; // the same place: one parameter
        var third = Click(30, 30, finder.Id); // another offset: another parameter

        var (actions, parameters, arguments) = ComponentEdits.Lift(new[] { first, second, third }, null,
            new Dictionary<Guid, string> { [finder.Id] = "Remove Affix" });

        Assert.Equal(new[] { "Remove_Affix", "Remove_Affix2" }, parameters.Select(p => p.Name));
        Assert.All(parameters, p => Assert.Equal(ParameterType.Point, p.Type));
        Assert.True(JsonNode.DeepEquals(Point(10, 10), parameters[0].Example));
        Assert.Equal("Remove_Affix", actions[0].Bind!["Point"]);
        Assert.Equal("Remove_Affix", actions[1].Bind!["Point"]);
        Assert.Equal("Remove_Affix2", actions[2].Bind!["Point"]);
        Assert.All(actions, a => Assert.Null(a.FoundId));
        Assert.True(JsonNode.DeepEquals(Found(finder.Id, 5, 0), arguments!["Remove_Affix"]));
        Assert.True(JsonNode.DeepEquals(Found(finder.Id), arguments["Remove_Affix2"]));

        // The new component and the row that calls it pass the checks where the actions were.
        var component = Comp("Clicks", parameters, actions.ToArray());
        var row = new MacroAction { Type = MacroActionType.Component, ComponentId = component.Id, Arguments = arguments };
        Assert.Empty(ComponentProblems(component));
        Assert.Empty(MacroProblems(Macro("D4", If(finder, row)), component));
    }

    [Fact]
    public void Save_as_component_keeps_what_is_found_inside_and_lifts_calls_and_parameters_from_outside()
    {
        var inside = ImageFinder("Inside");
        var outside = ImageFinder("Outside");
        var strip = Strip();
        var selected = new[]
        {
            If(inside, Click(found: inside.Id)),
            Call(strip, ("Slot", Found(outside.Id, 1, 2))),
            Bound(Press(), ("HoldMs", "Hold")),
        };
        var outer = new[] { HoldParam("Hold", Number(30)) };

        var (actions, parameters, arguments) = ComponentEdits.Lift(selected, outer, new Dictionary<Guid, string> { [outside.Id] = "Outside" });

        // Found inside: its new id, still its own.
        Assert.Equal(actions[0].If!.Conditions[0].Id, actions[0].If!.Then[0].FoundId);
        Assert.NotEqual(inside.Id, actions[0].If!.Conditions[0].Id);
        // A call's place found outside: a parameter passed on.
        Assert.True(JsonNode.DeepEquals(From("Outside"), actions[1].Arguments!["Slot"]));
        Assert.True(JsonNode.DeepEquals(Found(outside.Id, 1, 2), arguments!["Outside"]));
        // A parameter of the component around: one like it, passed on.
        var hold = parameters.Single(p => p.Name == "Hold");
        Assert.Equal((ParameterType.Number, NumberUnit.Ms), (hold.Type, hold.Unit));
        Assert.True(JsonNode.DeepEquals(Number(30), hold.Default));
        Assert.True(JsonNode.DeepEquals(From("Hold"), arguments["Hold"]));
        Assert.Equal("Hold", actions[2].Bind!["HoldMs"]);
    }

    [Theory]
    [InlineData("Remove Affix", "Remove_Affix")]
    [InlineData("Élite ✦ item", "Elite_item")]
    [InlineData("2nd slot", "Place_2nd_slot")]
    [InlineData("", "Place")]
    [InlineData("技能", "Place")]
    public void A_parameter_s_name_is_made_from_the_condition_s_name(string text, string name) =>
        Assert.Equal(name, ComponentEdits.NameFrom(text, "Place"));

    // ================= Ids, fingerprint, images =================

    [Fact]
    public void New_condition_ids_follow_into_Found_arguments()
    {
        var finder = ImageFinder();
        Guid before = finder.Id;
        var strip = Strip();
        var actions = new List<MacroAction> { If(finder, Call(strip, ("Slot", Found(finder.Id)))) };

        var renewed = MacroEditList.RenewConditions(actions);

        Guid now = actions[0].If!.Conditions[0].Id;
        Assert.NotEqual(before, now);
        Assert.Equal(now, renewed[before]);
        Assert.Equal(now, ComponentValues.Reference(actions[0].If!.Then[0].Arguments!["Slot"])!.Found);
    }

    [Fact]
    public void The_fingerprint_covers_the_signature()
    {
        var component = Strip();
        var collection = new LibraryCollection { Name = "D4", Components = { component } };
        string before = ComponentLibrary.Fingerprint(collection);

        component.Parameters[0].Default = Point(1, 1);
        string withDefault = ComponentLibrary.Fingerprint(collection);
        Assert.NotEqual(before, withDefault);

        component.Returns = new ComponentReturns { Found = Guid.NewGuid() };
        Assert.NotEqual(withDefault, ComponentLibrary.Fingerprint(collection));

        // Names and descriptions still don't count.
        string now = ComponentLibrary.Fingerprint(collection);
        component.Parameters[0].Description = "The inventory slot";
        component.Name = "Renamed";
        Assert.Equal(now, ComponentLibrary.Fingerprint(collection));
    }

    [Fact]
    public void Images_given_as_values_are_kept_like_the_conditions_ones()
    {
        var imageParameter = Param("Look", ParameterType.Image, Images(ImageA), Images(ImageB));
        var component = Comp("Find", new[] { imageParameter }, Press());
        Assert.Equal(new[] { ImageA, ImageB }.ToHashSet(), ImageStore.IdsIn(component));

        string imageC = new string('c', 64);
        var macro = Macro("D4", Call(component, ("Look", Images(imageC))),
            If(CallCondition(component, "Look", ("Look", Images(ImageA))), Press()));
        Assert.Equal(new[] { imageC, ImageA }.ToHashSet(), ImageStore.IdsIn(macro));
    }

    // ================= Sharing =================

    private static LibraryCollection Collection(string name, params LibraryComponent[] components) =>
        new() { Name = name, Domain = LibraryDomain.Game, Components = components.ToList() };

    [Fact]
    public void A_file_carries_the_collections_its_components_call_down_every_call()
    {
        var press = Comp("Press", Press());
        var keys = Collection("Keys", press);
        var twice = Comp("Twice", Call(press), Call(press));
        var helpers = Collection("Helpers", twice);
        var rare = Comp("Rare", If(Pixel()));
        rare.Returns = new ComponentReturns { Found = rare.Actions[0].If!.Conditions[0].Id };
        var conditions = Collection("Conditions", rare);
        var unrelated = Collection("Unrelated", Comp("Alone", Press()));
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        macros.Library.Collections.AddRange(new[] { keys, helpers, conditions, unrelated });
        var macro = Macro("Farm", Call(twice), If(CallCondition(rare, "Rare"), Press()));

        string text = ItemFile.Write(new object[] { macro }, new Version(1, 30, 0), macros.Images, macros.Library);

        var names = JsonNode.Parse(text)!["Collections"]!.AsArray().Select(c => (string)c!["Name"]!).ToList();
        Assert.Equal(new[] { "Helpers", "Conditions", "Keys" }.ToHashSet(), names.ToHashSet());
        Assert.Equal(3, names.Count);
        // Export collection alone carries what its components call too.
        Assert.Equal(new[] { "Helpers", "Keys" }, macros.Library.WithDependencies(new[] { helpers }).Select(c => c.Name));
    }

    [Fact]
    public void An_import_checks_the_calls_against_the_Library_with_the_file_s_collections()
    {
        var press = Comp("Press", new[] { HoldParam() }, Bound(Press(), ("HoldMs", "Hold")));
        var twice = Comp("Twice", Call(press, ("Hold", Number(20))), Call(press, ("Hold", Number(30))));
        var mine = new MacroLibrary(_folder.File("mine/macros"), _folder.File("mine/images"));
        mine.Library.Collections.Add(Collection("Keys", press));
        mine.Library.Collections.Add(Collection("Helpers", twice));
        var macro = Macro("Farm", Call(twice), Call(press, ("Hold", Number(40))));
        string text = ItemFile.Write(new object[] { macro }, new Version(1, 30, 0), mine.Images, mine.Library);

        var friend = new MacroLibrary(_folder.File("friend/macros"), _folder.File("friend/images"));
        var content = ItemFile.Read(text, AppSettings.CreateDefault(), friend, new Version(1, 30, 0));
        Assert.Null(content.Error);
        Assert.Equal(2, content.NewCollections.Count);
        Assert.Null(Assert.Single(content.Entries).Problem);

        // A file whose component calls one it doesn't carry is refused, saying which collection.
        var lonely = new MacroLibrary(_folder.File("lonely/macros"), _folder.File("lonely/images"));
        lonely.Library.Collections.Add(Collection("Helpers", twice));
        string partial = ItemFile.Write(Array.Empty<object>(), new Version(1, 30, 0), lonely.Images, null, new[] { lonely.Library.Collections[0] });
        var refused = ItemFile.Read(partial, AppSettings.CreateDefault(), new MacroLibrary(_folder.File("other/macros"), _folder.File("other/images")), new Version(1, 30, 0));
        Assert.NotNull(refused.Error);
        Assert.Contains("Helpers", refused.Error);
    }

    // ================= Saving together =================

    [Fact]
    public void The_Library_and_the_macros_are_saved_together()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var strip = Strip();
        macros.Library.Collections.Add(Collection("D4", strip));
        var macro = Macro("D4", Call(strip, ("Slot", Point(1, 2))));
        macros.Macros.Add(macro);

        Assert.True(macros.TrySaveTogether(new[] { macro }, out var error), error);
        Assert.True(File.Exists(_folder.File("library.json")));
        Assert.True(File.Exists(Path.Combine(_folder.File("macros"), $"{macro.Id}.json")));
        Assert.Contains("\"Slot\"", File.ReadAllText(_folder.File("library.json")));
    }

    [Fact]
    public void When_one_file_can_t_be_written_none_changes()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var strip = Strip();
        macros.Library.Collections.Add(Collection("D4", strip));
        Assert.True(macros.Library.TrySave(out _));
        string libraryBefore = File.ReadAllText(_folder.File("library.json"));
        var macro = Macro("D4", Call(strip, ("Slot", Point(1, 2))));
        // Its file's place for writing is taken by a folder: that write fails.
        Directory.CreateDirectory(Path.Combine(_folder.File("macros"), $"{macro.Id}.json.tmp"));

        macros.Library.Collections[0].Name = "Changed";
        Assert.False(macros.TrySaveTogether(new[] { macro }, out var error));
        Assert.NotNull(error);
        Assert.Equal(libraryBefore, File.ReadAllText(_folder.File("library.json")));
        Assert.False(File.Exists(Path.Combine(_folder.File("macros"), $"{macro.Id}.json")));
        Assert.False(File.Exists(_folder.File("library.json.tmp")));
    }

    [Fact]
    public void A_Library_it_can_t_read_is_never_saved_together_either()
    {
        File.WriteAllText(_folder.File("library.json"), "{ broken");
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        macros.Library.Load(out _);
        Assert.False(macros.TrySaveTogether(Array.Empty<MacroDefinition>(), out var error));
        Assert.Equal(Strings.Library_FileUnreadable, error);
    }
}
