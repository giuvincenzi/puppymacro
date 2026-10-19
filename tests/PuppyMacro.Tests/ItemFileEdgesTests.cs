using System;
using System.Globalization;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// .puppyitems files from angles ItemFileTests doesn't take: a damaged file's JSON, and a whole round trip of a macro with
/// the collections it needs and their images, from one PuppyMacro to another and back.
/// </summary>
public class ItemFileEdgesTests : IDisposable
{
    private static readonly Version This = new(1, 30, 0);

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>A PuppyMacro of its own: its macros, images and library.json in its own folder.</summary>
    private MacroLibrary Data(string name) => new(_folder.File(name + "/macros"), _folder.File(name + "/images"));

    /// <summary>
    /// A file whose JSON has a name twice in one object (edited by hand, merged by a tool, sent by an AI app's
    /// import_items): JsonNode.Parse accepts it, and the first read of that object throws ArgumentException ("An item with
    /// the same key has already been added"), which ItemFile.Read does not catch. Import shows PuppyMacro's unexpected-error
    /// box instead of "not a PuppyMacro items file" (the UI thread's handler), and an AI app's import gets an exception. The
    /// same text is refused cleanly by the Code views, which read with JsonDocument.
    /// </summary>
    [Theory]
    [InlineData("""{ "Format": 1, "Format": 1, "Items": [] }""")]
    [InlineData("""{ "Format": 1, "Items": [ { "Kind": "Macro", "Kind": "Loop", "Item": { "Name": "x" } } ] }""")]
    [InlineData("""{ "Format": 1, "Items": [ { "Kind": "Macro", "Item": { "Name": "x", "Name": "y", "Actions": [] } } ] }""")]
    public void A_file_with_a_name_twice_in_an_object_is_refused_not_an_exception(string text)
    {
        ItemFileContent? content = null;
        var exception = Record.Exception(() => content = ItemFile.Read(text, new AppSettings(), Data("friend"), This));

        Assert.Null(exception);
        Assert.True(content!.Error != null || content.Entries.All(e => e.Problem != null));
    }

    /// <summary>
    /// A macro linking a component that calls a component of another collection, with an image in a condition: exported with
    /// both collections and the image, imported on another PC (in another language), saved there and read back, it has the
    /// same collections (same fingerprint) and image; exported from there and imported back here, nothing is new and
    /// nothing conflicts.
    /// </summary>
    [Fact]
    public void A_macro_with_its_collections_and_images_goes_to_another_PC_and_back_unchanged()
    {
        var mine = Data("mine");
        byte[] png = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        string image = mine.Images.Add(png);

        var wait = Comp("Wait", new[] { Param("Hold", ParameterType.Number, Number(30), Number(40.5)) }, Bound(Press(), ("HoldMs", "Hold")));
        var loot = Comp("Loot", new[] { SlotParam() },
            If(new MacroCondition { Kind = ConditionKind.Image, Name = "Chest", Images = new() { new ConditionImage { Id = image, X = 100, Y = 200 } }, Similarity = 90 },
                Bound(Click(), ("Point", "Slot"))),
            Call(wait, ("Hold", Number(55.25))));
        var waits = new LibraryCollection { Name = "Waits – è 循环", Domain = LibraryDomain.Game, Components = { wait } };
        var game = new LibraryCollection { Name = "Diablo IV", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe", Components = { loot } };
        mine.Library.Collections.AddRange(new[] { game, waits });
        Assert.True(mine.Library.TrySave(out _));
        var farm = Macro("Farm", Call(loot, ("Slot", Point(3912, 1056))));

        string text = InCulture("de-DE", () => ItemFile.Write(new object[] { farm }, This, mine.Images, mine.Library));

        var friend = Data("friend");
        ItemFileContent content = InCulture("ar-SA", () => ItemFile.Read(text, new AppSettings(), friend, This));
        Assert.Null(content.Error);
        Assert.Null(content.Entries.Single().Problem);
        Assert.Equal(new[] { game.Id, waits.Id }.OrderBy(i => i), content.NewCollections.Select(c => c.Id).OrderBy(i => i));
        ItemFile.AddImages(content, friend);
        friend.Library.Collections.AddRange(content.NewCollections);
        Assert.True(friend.Library.TrySave(out _));
        var imported = (MacroDefinition)content.Entries.Single().Item!;
        Assert.True(friend.TrySave(imported, out _));

        var friendRestarted = Data("friend");
        friendRestarted.Load(Array.Empty<Guid>(), out string? warning);
        friendRestarted.Library.Load(out string? libraryWarning);
        Assert.Null(warning);
        Assert.Null(libraryWarning);
        Assert.Equal(png, friendRestarted.Images.Read(image));
        foreach (var collection in mine.Library.Collections)
            Assert.Equal(ComponentLibrary.Fingerprint(collection),
                ComponentLibrary.Fingerprint(friendRestarted.Library.FindCollection(collection.Id)!));
        Assert.Equal(MacroJson.Serialize(imported), MacroJson.Serialize(friendRestarted.Macros.Single()));

        string back = ItemFile.Write(friendRestarted.Macros.Cast<object>().ToList(), This, friendRestarted.Images, friendRestarted.Library);
        var mineRestarted = Data("mine");
        mineRestarted.Library.Load(out _);
        ItemFileContent home = ItemFile.Read(back, new AppSettings(), mineRestarted, This);
        Assert.Null(home.Error);
        Assert.Empty(home.NewCollections);
        Assert.Null(home.Entries.Single().Problem);
    }

    private static T InCulture<T>(string name, Func<T> action)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
