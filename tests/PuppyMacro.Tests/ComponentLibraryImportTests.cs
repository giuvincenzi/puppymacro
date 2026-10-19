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
/// The Library's files from angles the other classes don't take: a .puppyitems file whose two collections share one Id,
/// library.json with a value whose field is written twice, and the passing sides next to them.
/// </summary>
public class ComponentLibraryImportTests : IDisposable
{
    private static readonly Version This = new(1, 30, 0);

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private MacroLibrary Data(string name) => new(_folder.File(name + "/macros"), _folder.File(name + "/images"));

    /// <summary>
    /// A file with two collections that share one Id but hold different components (a file edited by hand or written by
    /// an AI app), and a macro that calls the component of the second. The file is read as fine and both are new
    /// collections, so the macro passes its checks (they see both); but the import adds a collection only when the Library
    /// has none with its Id (<c>MainWindow.AddCollections</c>), so the second is dropped and the imported macro calls a
    /// component the Library doesn't have. A collection's Id is one collection: the file must be refused.
    /// </summary>
    [Fact]
    public void A_file_whose_two_collections_share_one_id_is_refused()
    {
        var press = Comp("Press", Press());
        var strip = Comp("Strip", Click());
        var first = new LibraryCollection { Name = "D4", Domain = LibraryDomain.Game, Components = { press } };
        var second = new LibraryCollection { Id = first.Id, Name = "D4 too", Domain = LibraryDomain.Game, Components = { strip } };
        var macro = Macro("Strip all", Call(strip));
        string text = ItemFile.Write(new object[] { macro }, This, collections: new[] { first, second });

        var content = ItemFile.Read(text, AppSettings.CreateDefault(), Data("friend"), This);

        Assert.True(content.Error != null || content.NewCollections.Select(c => c.Id).Distinct().Count() == content.NewCollections.Count,
            $"{content.NewCollections.Count} new collections with {content.NewCollections.Select(c => c.Id).Distinct().Count()} id(s), " +
            $"and the macro is {(content.Entries.Single().Problem == null ? "accepted" : "refused")}");
    }

    /// <summary>The passing side: two collections with their own Ids are both new, and the macro calling the second is accepted.</summary>
    [Fact]
    public void A_file_with_two_collections_adds_both()
    {
        var press = Comp("Press", Press());
        var strip = Comp("Strip", Click());
        var first = new LibraryCollection { Name = "D4", Domain = LibraryDomain.Game, Components = { press } };
        var second = new LibraryCollection { Name = "D4 too", Domain = LibraryDomain.Game, Components = { strip } };
        string text = ItemFile.Write(new object[] { Macro("Strip all", Call(strip)) }, This, collections: new[] { first, second });

        var content = ItemFile.Read(text, AppSettings.CreateDefault(), Data("friend"), This);

        Assert.Null(content.Error);
        Assert.Equal(2, content.NewCollections.Count);
        Assert.Null(content.Entries.Single().Problem);
    }

    /// <summary>
    /// A .puppyitems file (edited by hand, or written by an AI app) whose collection's component has an Example with "X"
    /// written twice: reading it for Import says the file has a problem, never throws.
    /// </summary>
    [Fact]
    public void A_file_with_a_field_written_twice_in_a_component_s_value_is_refused_never_a_crash()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        ComponentEdits.WriteExamples(strip);
        var collection = new LibraryCollection { Name = "D4", Domain = LibraryDomain.Game, Components = { strip } };
        string text = ItemFile.Write(Array.Empty<object>(), This, collections: new[] { collection });
        int at = text.IndexOf("\"Example\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        int x = text.IndexOf("\"X\": 3912", at, StringComparison.Ordinal);
        Assert.True(x > 0);
        text = text.Insert(x, "\"X\": 1, ");

        ItemFileContent? content = null;
        var thrown = Record.Exception(() => content = ItemFile.Read(text, AppSettings.CreateDefault(), Data("friend"), This));

        Assert.Null(thrown);
        Assert.NotNull(content!.Error);
    }

    /// <summary>
    /// library.json (edited by hand) where a component's Example has "X" written twice. Loading it and making the engine's
    /// snapshot (<c>EngineSnapshot.From</c> checks every call: <see cref="ComponentChecks.Unplayable"/>) must not throw: the
    /// file is either unreadable (kept as library.invalid.json) or its component fails the checks.
    /// </summary>
    [Fact]
    public void A_library_file_with_a_field_written_twice_in_a_value_never_throws_at_start()
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(), ("Point", "Slot")));
        ComponentEdits.WriteExamples(strip);
        var collection = new LibraryCollection { Name = "D4", Domain = LibraryDomain.Game, Components = { strip } };
        string file = _folder.File("library.json");
        var saved = new ComponentLibrary(file) { Collections = { collection } };
        string text = saved.Text().Replace("\"X\": 3912,", "\"X\": 3912, \"X\": 1,");
        Assert.Contains("\"X\": 1,", text);
        File.WriteAllText(file, text);
        var macro = Macro("Strip all", Call(strip, ("Slot", Point(1, 2))));

        var library = new ComponentLibrary(file);
        var thrown = Record.Exception(() =>
        {
            library.Load(out _);
            ComponentChecks.Unplayable(library.Signatures(), new[] { macro });
            ComponentPrograms.Prepare(library.AllComponents);
        });

        Assert.Null(thrown);
    }
}
