using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Who uses a component (<see cref="ComponentLibrary.CallersOf"/>): the macros that link it and the components that call
/// it, by a row or a Component condition, only directly, with an open editor's version instead of the saved one; why a
/// component or a collection can't be deleted; and the Library's use line ("Used in 2 macros and 1 component").
/// </summary>
public class ComponentUsesTests
{
    private readonly LibraryComponent _slot = Comp("Strip one slot", new[] { SlotParam() }, Bound(Click(3912, 1056), ("Point", "Slot")));
    private readonly LibraryComponent _rare;
    private readonly LibraryComponent _strip;
    private readonly LibraryComponent _free = Comp("Free", Press());
    private readonly LibraryCollection _d4;
    private readonly ComponentLibrary _library = new("library.json");

    public ComponentUsesTests()
    {
        var pixel = Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"));
        _rare = Comp("Rare item in slot", new[] { SlotParam() }, If(pixel));
        _rare.Returns = new ComponentReturns { Found = pixel.Id };
        // Calls the slot by a row and the rare one as a condition.
        _strip = Comp("Strip if rare", If(CallCondition(_rare, "Rare", ("Slot", Point(1, 2))), Call(_slot, ("Slot", Point(1, 2)))));
        _d4 = new LibraryCollection { Name = "Diablo IV", Components = { _slot, _rare, _strip, _free } };
        _library.Collections.Add(_d4);
    }

    private (List<string> Macros, List<string> Components) Of(LibraryComponent component, IEnumerable<MacroDefinition>? macros = null,
        IEnumerable<MacroDefinition>? open = null) => _library.CallersOf(component.Id, macros ?? Enumerable.Empty<MacroDefinition>(), open);

    [Fact]
    public void A_component_s_callers_are_the_macros_and_components_that_call_it_by_a_row_or_a_condition()
    {
        var byRow = Macro("D4 Strip", Call(_slot, ("Slot", Point(5, 6))));
        var byCondition = Macro("D4 Check", If(CallCondition(_rare, "Rare", ("Slot", Point(5, 6))), Press()));
        var inThen = Macro("Nested", If(Pixel(), Call(_slot, ("Slot", Point(5, 6)))));
        var macros = new[] { byRow, byCondition, inThen };

        Assert.Equal("D4 Strip, Nested | Strip if rare", Names(Of(_slot, macros)));
        Assert.Equal("D4 Check | Strip if rare", Names(Of(_rare, macros)));
        Assert.Equal(" | ", Names(Of(_free, macros)));
    }

    [Fact]
    public void Only_direct_callers_count()
    {
        // The macro calls Strip if rare, which calls the slot: the slot's callers are Strip if rare only.
        var macro = Macro("D4", Call(_strip));
        Assert.Equal(" | Strip if rare", Names(Of(_slot, new[] { macro })));
        Assert.Equal("D4 | ", Names(Of(_strip, new[] { macro })));
    }

    [Fact]
    public void A_macro_that_calls_a_component_twice_counts_once()
    {
        var macro = Macro("D4", Call(_slot, ("Slot", Point(1, 1))), Call(_slot, ("Slot", Point(2, 2))));
        Assert.Single(Of(_slot, new[] { macro }).Macros);
    }

    [Fact]
    public void An_open_macro_editor_counts_instead_of_its_saved_macro()
    {
        var saved = Macro("D4", Call(_slot, ("Slot", Point(1, 1))));
        var edited = saved.Clone();
        edited.Actions.Clear(); // the row was removed in the editor, not saved yet
        Assert.Empty(Of(_slot, new[] { saved }, new[] { edited }).Macros);

        var adding = Macro("Not saved yet", Call(_free));
        Assert.Equal(new[] { "Not saved yet" }, Of(_free, new[] { saved }, new[] { adding }).Macros);
        // The same open macro given twice (the Library over an editor) counts once, its last version.
        Assert.Equal(new[] { "D4" }, Of(_slot, new[] { saved }, new[] { edited, saved.Clone() }).Macros);
    }

    [Fact]
    public void An_open_component_editor_counts_as_the_component_instead_of_its_Library_version()
    {
        // The component editor holds Strip if rare without its call to the slot: it is a component, not a macro.
        var editing = new MacroDefinition { Id = _strip.Id, Name = _strip.Name, Actions = { Press() } };
        Assert.Equal(" | ", Names(Of(_slot, open: new[] { editing })));

        var calling = new MacroDefinition { Id = _free.Id, Name = _free.Name, Actions = { Call(_slot, ("Slot", Point(1, 1))) } };
        Assert.Equal(" | Strip if rare, Free", Names(Of(_slot, open: new[] { calling })));
    }

    [Fact]
    public void A_component_never_counts_as_its_own_caller()
    {
        // Not valid in a saved Library (a loop), but the count must not name it: only others call it.
        var self = new MacroDefinition { Id = _free.Id, Name = _free.Name, Actions = { Call(_free) } };
        Assert.Empty(Of(_free, open: new[] { self }).Components);
    }

    // ================= Deleting =================

    [Fact]
    public void A_component_nothing_uses_can_be_deleted()
    {
        Assert.Null(_library.CanDeleteComponent(_free, new[] { Macro("D4", Call(_slot, ("Slot", Point(1, 1)))) }));
    }

    [Fact]
    public void Why_a_component_can_t_be_deleted_names_the_macros_the_components_or_both()
    {
        var macro = Macro("D4 Strip", Call(_slot, ("Slot", Point(5, 6))));

        Assert.Equal("Free is linked in these macros: D4. Unlink it there first.",
            _library.CanDeleteComponent(_free, new[] { Macro("D4", Call(_free)) }));
        Assert.Equal("Rare item in slot is used by these components: Strip if rare. Stop using it there first.",
            _library.CanDeleteComponent(_rare, new MacroDefinition[0]));
        Assert.Equal("Strip one slot is linked in these macros: D4 Strip, and used by these components: Strip if rare. Stop using it there first.",
            _library.CanDeleteComponent(_slot, new[] { macro }));
    }

    [Fact]
    public void An_open_editor_s_link_blocks_deleting_and_its_removed_link_does_not()
    {
        var saved = Macro("D4", Call(_free));
        var edited = saved.Clone();
        edited.Actions.Clear();
        Assert.NotNull(_library.CanDeleteComponent(_free, new MacroDefinition[0], new[] { Macro("Open", Call(_free)) }));
        Assert.Null(_library.CanDeleteComponent(_free, new[] { saved }, new[] { edited }));
    }

    [Fact]
    public void A_collection_whose_components_call_each_other_can_be_deleted()
    {
        Assert.Null(_library.CanDeleteCollection(_d4, new MacroDefinition[0]));
    }

    [Fact]
    public void Why_a_collection_can_t_be_deleted_names_the_macros_the_other_collections_components_or_both()
    {
        var other = new LibraryCollection { Name = "General", Components = { Comp("Strip from General", Call(_slot, ("Slot", Point(1, 1)))) } };
        _library.Collections.Add(other);
        var macro = Macro("D4 Strip", Call(_rare, ("Slot", Point(1, 1))));

        Assert.Equal("Some components of Diablo IV are used by components of other collections: Strip from General. Stop using them there first.",
            _library.CanDeleteCollection(_d4, new MacroDefinition[0]));
        Assert.Equal("Some components of Diablo IV are linked in these macros: D4 Strip, and used by components of other collections: Strip from General. Stop using them there first.",
            _library.CanDeleteCollection(_d4, new[] { macro }));
        other.Components.Clear();
        Assert.Equal("Some components of Diablo IV are linked in these macros: D4 Strip. Unlink them there first.",
            _library.CanDeleteCollection(_d4, new[] { macro }));
        // General's own components call nothing in it.
        Assert.Null(_library.CanDeleteCollection(other, new[] { macro }));
    }

    // ================= What the Library shows =================

    [Theory]
    [InlineData(0, 0, "Not used")]
    [InlineData(1, 0, "Used in 1 macro")]
    [InlineData(3, 0, "Used in 3 macros")]
    [InlineData(0, 1, "Used in 1 component")]
    [InlineData(0, 2, "Used in 2 components")]
    [InlineData(1, 1, "Used in 1 macro and 1 component")]
    [InlineData(2, 3, "Used in 2 macros and 3 components")]
    public void The_use_line_counts_macros_and_components(int macros, int components, string expected)
    {
        Assert.Equal(expected, LibraryWindow.UseText(macros, components));
    }

    [Fact]
    public void The_component_window_names_the_macros_then_the_components()
    {
        Assert.Equal("D4, Strip if rare", ComponentWindow.UsedIn(new List<string> { "D4" }, new List<string> { "Strip if rare" }));
        Assert.Equal("Strip if rare", ComponentWindow.UsedIn(new List<string>(), new List<string> { "Strip if rare" }));
        Assert.Equal("Not used", ComponentWindow.UsedIn(new List<string>(), new List<string>()));
    }

    /// <summary>"D4 Strip, Nested | Strip if rare": the macros, then the components.</summary>
    private static string Names((List<string> Macros, List<string> Components) callers) =>
        string.Join(", ", callers.Macros) + " | " + string.Join(", ", callers.Components);
}
