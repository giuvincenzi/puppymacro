using System;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// .puppyitems files that are not what they say, from angles ComponentEdgeCasesTests doesn't take: one collection that
/// holds the same component twice, and an image whose bytes are not its id.
/// </summary>
public class ComponentLibraryFilesTests : IDisposable
{
    private static readonly Version This = new(1, 30, 0);

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static LibraryCollection Collection(string name, params LibraryComponent[] components) =>
        new() { Name = name, Domain = LibraryDomain.Game, Components = components.ToList() };

    private MacroLibrary Data(string name) => new(_folder.File(name + "/macros"), _folder.File(name + "/images"));

    [Fact]
    public void A_collection_that_holds_one_component_twice_is_refused()
    {
        var slot = Comp("Slot", Click());
        string text = ItemFile.Write(Array.Empty<object>(), This, null, null, new[] { Collection("Game", slot, slot.Clone()) });

        var content = ItemFile.Read(text, AppSettings.CreateDefault(), Data("friend"), This);

        var ids = content.NewCollections.SelectMany(c => c.Components).Select(c => c.Id).ToList();
        Assert.True(content.Error != null || ids.Distinct().Count() == ids.Count,
            $"the Library would get {ids.Count} components with {ids.Distinct().Count()} ids");
    }

    [Fact]
    public void An_image_of_the_file_whose_bytes_are_not_its_id_is_missing_not_there()
    {
        var macro = Macro("Look", If(ImageFinder("Affix"), Press())); // its image: ImageA
        var root = JsonNode.Parse(ItemFile.Write(new object[] { macro }, This))!.AsObject();
        root["Images"] = new JsonObject { [ImageA] = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };
        var friend = Data("friend");

        var content = ItemFile.Read(root.ToJsonString(), AppSettings.CreateDefault(), friend, This);
        ItemFile.AddImages(content, friend);

        var entry = content.Entries.Single();
        Assert.True(entry.Problem != null || friend.Images.Has(ImageA),
            "the macro is imported as complete, but its image was not kept: its condition says the image is missing");
    }
}
