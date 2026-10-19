using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The Library (ComponentLibrary): library.json read and written, a file it can't read kept and never replaced, structured
/// names, the fingerprint (what it covers and what it leaves out), a component's rules, deleting what macros link, and the
/// engine's snapshot (the components prepared once, a macro whose calls fail can't play). The functions themselves (parameters,
/// calls, frames) are in ComponentFunctionsTests.
/// </summary>
public class ComponentLibraryTests : IDisposable
{
    private const string ImageA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string LibraryFile => _folder.File(ComponentLibrary.FileName);

    private static MacroAction Click(int x = 10, Guid? found = null) =>
        new() { Type = MacroActionType.Click, Vk = 0x01, X = x, Y = 20, FoundId = found };

    private static MacroCondition Image(string name) => new()
    {
        Kind = ConditionKind.Image,
        Name = name,
        Images = new() { new ConditionImage { Id = ImageA, X = 100, Y = 200 } },
        Similarity = 90,
    };

    /// <summary>An If that finds an image and clicks where it was found.</summary>
    private static MacroAction FindAndClick(MacroCondition image) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf
        {
            Conditions = { image },
            Wait = ConditionWait.UpTo,
            WaitSeconds = 5,
            Then = { Click(found: image.Id) },
        },
    };

    private static LibraryComponent Component(string name, params MacroAction[] actions) =>
        new() { Name = name, Purpose = ComponentPurpose.Act, Concept = "remove_one_affix", Actions = actions.ToList() };

    private static LibraryCollection Collection(params LibraryComponent[] components) => new()
    {
        Name = "Diablo IV 5120×1440",
        Domain = LibraryDomain.Game,
        AppExe = "Diablo IV.exe",
        Screen = new LibraryScreen { Width = 5120, Height = 1440, ScalePercent = 100 },
        Components = components.ToList(),
    };

    private static MacroAction Link(LibraryComponent component) =>
        new() { Type = MacroActionType.Component, ComponentId = component.Id };

    [Fact]
    public void The_library_is_saved_and_read_back_whole()
    {
        var component = Component("Remove one affix", FindAndClick(Image("Remove Affix")));
        var library = new ComponentLibrary(LibraryFile);
        library.Load(out _);
        library.Collections.Add(Collection(component));
        Assert.True(library.TrySave(out string? error), error);

        var read = new ComponentLibrary(LibraryFile);
        read.Load(out string? warning);

        Assert.Null(warning);
        var (collection, found) = read.Find(component.Id)!.Value;
        Assert.Equal("Diablo IV 5120×1440", collection.Name);
        Assert.Equal(5120, collection.Screen!.Width);
        Assert.Equal("Remove one affix", found.Name);
        Assert.Equal(ComponentLibrary.Fingerprint(library.Collections[0]), ComponentLibrary.Fingerprint(collection));
    }

    [Fact]
    public void No_file_is_an_empty_library()
    {
        var library = new ComponentLibrary(LibraryFile);
        library.Load(out string? warning);

        Assert.Null(warning);
        Assert.Empty(library.Collections);
        Assert.False(library.Broken);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Format\": 99, \"Collections\": []}")]
    [InlineData("{\"Format\": 1, \"Collections\": [{\"Name\": \"X\", \"Domain\": \"Spaceship\"}]}")]
    public void A_file_it_can_t_read_is_kept_and_never_replaced(string text)
    {
        File.WriteAllText(LibraryFile, text);
        var library = new ComponentLibrary(LibraryFile);

        library.Load(out string? warning);

        Assert.True(library.Broken);
        Assert.Equal(Strings.Library_FileUnreadable, warning);
        Assert.Equal(text, File.ReadAllText(_folder.File("library.invalid.json")));
        Assert.False(library.TrySave(out _));
        Assert.Equal(text, File.ReadAllText(LibraryFile));
    }

    [Theory]
    [InlineData("Diablo IV.exe", "diablo_iv")]
    [InlineData("EXCEL.EXE", "excel")]
    [InlineData("my-app_2.exe", "my_app_2")]
    [InlineData(null, "any")]
    [InlineData("   ", "any")]
    public void The_app_s_part_of_a_structured_name_is_lowercase_with_underscores(string? exe, string slug) =>
        Assert.Equal(slug, ComponentLibrary.AppSlug(exe));

    [Fact]
    public void A_structured_name_is_domain_app_purpose_concept()
    {
        var component = Component("Remove one affix", Click());
        Assert.Equal("game-diablo_iv-act-remove_one_affix", ComponentLibrary.StructuredName(Collection(component), component));
    }

    [Fact]
    public void The_fingerprint_follows_the_actions_and_ids_not_the_names()
    {
        var component = Component("Remove one affix", Click(10));
        var collection = Collection(component);
        string first = ComponentLibrary.Fingerprint(collection);

        // Names, descriptions, the app and the screen: a rename on one PC never blocks an import.
        collection.Name = "Diablo";
        collection.AppExe = null;
        collection.Screen = null;
        component.Name = "Other";
        component.Description = "Clicks it";
        component.Concept = "other";
        Assert.Equal(first, ComponentLibrary.Fingerprint(collection));

        // The order of the components does not matter.
        var second = Component("Slot 1", Click(30));
        collection.Components.Add(second);
        string both = ComponentLibrary.Fingerprint(collection);
        collection.Components.Reverse();
        Assert.Equal(both, ComponentLibrary.Fingerprint(collection));

        // What plays does.
        second.Actions[0].X = 31;
        Assert.NotEqual(both, ComponentLibrary.Fingerprint(collection));
        string changed = ComponentLibrary.Fingerprint(collection);
        second.Id = Guid.NewGuid();
        Assert.NotEqual(changed, ComponentLibrary.Fingerprint(collection));
    }

    [Fact]
    public void The_fingerprint_is_the_same_in_every_language()
    {
        var collection = Collection(Component("Wait", new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41, DelayMs = 12.5 }));
        string english = ComponentLibrary.Fingerprint(collection);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
            Assert.Equal(english, ComponentLibrary.Fingerprint(collection));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_component_needs_a_name_a_concept_and_actions_and_calls_only_components_the_Library_has()
    {
        var good = Component("Remove one affix", FindAndClick(Image("Remove Affix")));
        Assert.Null(ComponentLibrary.CheckComponent(good));

        Assert.EndsWith(Strings.Library_NameMissing, ComponentLibrary.CheckComponent(Component(" ", Click())));
        var badConcept = Component("A", Click());
        badConcept.Concept = "Remove Affix";
        Assert.EndsWith(Strings.Library_ConceptInvalid, ComponentLibrary.CheckComponent(badConcept));
        Assert.EndsWith(Strings.Library_NoActions, ComponentLibrary.CheckComponent(Component("A")));
        // A component can call another one, when the Library has it.
        Assert.EndsWith(Strings.Code_ComponentMissing, ComponentLibrary.CheckComponent(Component("A", Link(good))));
        Assert.Null(ComponentLibrary.CheckComponent(Component("A", Link(good)), library: new[] { good }));
    }

    [Fact]
    public void A_component_s_found_points_use_its_own_conditions()
    {
        var image = Image("Remove Affix");
        // A click "where Remove Affix was found" without the If that finds it: the If is in the macro, not in the component.
        var outside = Component("Click it", Click(found: image.Id));

        Assert.NotNull(ComponentLibrary.CheckComponent(outside));
    }

    [Fact]
    public void Linked_components_and_their_collections_can_t_be_deleted()
    {
        var linked = Component("Remove one affix", Click());
        var free = Component("Slot 2", Click(30));
        var collection = Collection(linked, free);
        var macro = new MacroDefinition { Name = "D4 Strip Affixes", Actions = { Link(linked) } };
        var macros = new[] { macro };
        var library = new ComponentLibrary("library.json") { Collections = { collection } };

        string? refused = library.CanDeleteComponent(linked, macros);
        Assert.NotNull(refused);
        Assert.Contains("D4 Strip Affixes", refused);
        Assert.Null(library.CanDeleteComponent(free, macros));
        Assert.Contains("D4 Strip Affixes", library.CanDeleteCollection(collection, macros));

        // Inside a Then too.
        var nested = new MacroDefinition { Name = "Nested", Actions = { new MacroAction { Type = MacroActionType.If, If = new MacroIf { Then = { Link(free) } } } } };
        Assert.Contains("Nested", library.CanDeleteComponent(free, new[] { nested }));
        Assert.Equal(2, ComponentLibrary.Links(new[] { macro, nested }).Values.Sum());
    }

    [Fact]
    public void The_snapshot_prepares_each_component_once_and_leaves_the_Library_as_it_is()
    {
        var image = Image("Remove Affix");
        var component = Component("Remove one affix", FindAndClick(image));
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        macros.Library.Collections.Add(Collection(component));
        var macro = new MacroDefinition { Name = "Twice", Actions = { Link(component), Link(component) } };
        macros.Macros.Add(macro);

        var snapshot = EngineSnapshot.From(AppSettings.CreateDefault(), macros);

        var prepared = snapshot.Components.Find(component.Id)!;
        Assert.Equal("Remove one affix", prepared.Name);
        // A copy, the same ids: each call gets a frame of its own, so the ids never need to change.
        Assert.NotSame(component.Actions[0], prepared.Actions[0]);
        Assert.Equal(image.Id, prepared.Actions[0].If!.Conditions[0].Id);
        Assert.Equal(1, snapshot.Components.Count);
        // The snapshot's macro is the saved one, untouched: its two rows play the one prepared component.
        Assert.Equal(MacroJson.Serialize(macro), MacroJson.Serialize(snapshot.Macros.Single()));
        Assert.Empty(snapshot.Unplayable);
    }

    [Fact]
    public void A_macro_linking_a_missing_component_can_t_play()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var gone = Guid.NewGuid();
        var macro = new MacroDefinition { Name = "Old", Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = gone } } };
        var asCondition = new MacroDefinition { Name = "Old condition", Actions = { new MacroAction { Type = MacroActionType.If, If = new MacroIf
        {
            Conditions = { new MacroCondition { Kind = ConditionKind.Component, Name = "Gone", ComponentId = gone } },
        } } } };
        macros.Macros.Add(macro);
        macros.Macros.Add(asCondition);

        Assert.Equal(new[] { gone }, macros.Library.Missing(macro));
        Assert.Equal(new[] { gone }, macros.Library.Missing(asCondition));
        Assert.Equal(new[] { macro.Id, asCondition.Id }.ToHashSet(), EngineSnapshot.From(AppSettings.CreateDefault(), macros).Unplayable.ToHashSet());
    }

    [Fact]
    public void A_macro_without_links_plays_as_it_is()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var macro = new MacroDefinition { Name = "Plain", Actions = { Click() } };
        macros.Macros.Add(macro);

        Assert.Empty(macros.Library.Missing(macro));
        var snapshot = EngineSnapshot.From(AppSettings.CreateDefault(), macros);
        Assert.Empty(snapshot.Unplayable);
        Assert.Equal(10, snapshot.Macros.Single().Actions[0].X);
    }

    private static List<CodeProblem> Problems(MacroDefinition macro, params LibraryComponent[] library)
    {
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out var problems, components: new ComponentSignatures(library));
        return problems;
    }

    [Fact]
    public void A_linked_row_has_only_its_component_and_the_component_must_be_there()
    {
        var component = Component("Remove one affix", Click());
        var good = new MacroDefinition { Name = "D4", Actions = { Link(component) } };
        Assert.Empty(Problems(good, component));

        Assert.Contains(Problems(good), p => p.Message.EndsWith(Strings.Code_ComponentMissing));
        var noId = new MacroDefinition { Name = "D4", Actions = { new MacroAction { Type = MacroActionType.Component } } };
        Assert.Contains(Problems(noId), p => p.Message.EndsWith(Strings.Code_ChooseComponent));
        var withDelay = new MacroDefinition { Name = "D4", Actions = { Link(component) } };
        withDelay.Actions[0].DelayMs = 100;
        Assert.Contains(Problems(withDelay), p => p.Message.EndsWith(Strings.Code_ComponentOnlyLink));
        var onClick = new MacroDefinition { Name = "D4", Actions = { Click() } };
        onClick.Actions[0].ComponentId = component.Id;
        Assert.Contains(Problems(onClick), p => p.Message.EndsWith(Strings.Code_ComponentIdOnlyLinks));
    }

    [Fact]
    public void The_engine_s_snapshot_has_the_components_and_a_macro_missing_one_can_t_play()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        var component = Component("Remove one affix", Click(42));
        macros.Library.Collections.Add(Collection(component));
        var linked = new MacroDefinition { Name = "Linked", Actions = { Link(component) } };
        var broken = new MacroDefinition { Name = "Broken", Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = Guid.NewGuid() } } };
        macros.Macros.Add(linked);
        macros.Macros.Add(broken);

        var snapshot = EngineSnapshot.From(AppSettings.CreateDefault(), macros);

        Assert.Equal(42, snapshot.Components.Find(component.Id)!.Actions.Single().X);
        Assert.Equal(new[] { broken.Id }, snapshot.Unplayable);
    }

    [Fact]
    public void The_images_of_the_components_stay_and_a_library_it_can_t_read_keeps_them_all()
    {
        var macros = new MacroLibrary(_folder.File("macros"), _folder.File("images"));
        string used = macros.Images.Add(new byte[] { 1, 2, 3 });
        string unused = macros.Images.Add(new byte[] { 4, 5, 6 });
        var image = Image("Remove Affix");
        image.Images![0].Id = used;
        macros.Library.Collections.Add(Collection(Component("Remove one affix", FindAndClick(image))));

        macros.ReleaseUnusedImages();
        Assert.True(macros.Images.Has(used));
        Assert.False(macros.Images.Has(unused));

        File.WriteAllText(LibraryFile, "{ broken");
        macros.Library.Load(out _);
        string again = macros.Images.Add(new byte[] { 7, 8, 9 });
        macros.ReleaseUnusedImages();
        Assert.True(macros.Images.Has(again));
    }

    [Fact]
    public void The_library_names_the_images_its_components_use()
    {
        var library = new ComponentLibrary(LibraryFile);
        library.Collections.Add(Collection(Component("Remove one affix", FindAndClick(Image("Remove Affix")))));

        Assert.Equal(new[] { ImageA }, library.ImageIds().Distinct());
    }
}
