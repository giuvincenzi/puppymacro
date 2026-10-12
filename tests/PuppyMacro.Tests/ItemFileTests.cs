using System;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>.puppyitems files: what Export writes and what Import adds (<see cref="ItemFile"/>).</summary>
public class ItemFileTests : IDisposable
{
    private const int F6 = 0x75, F7 = 0x76, F8 = 0x77, KeyJ = 0x4A, KeyC = 0x43;
    private static readonly Version This = new(1, 17, 0);

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = new();
    private readonly MacroLibrary _macros;

    public ItemFileTests() => _macros = new MacroLibrary(_folder.Path);

    public void Dispose() => _folder.Dispose();

    private static LoopDefinition Loop(string name, int? hotkey = null, ActivationMode mode = ActivationMode.Toggle, string? app = null) => new()
    {
        Name = name,
        Mode = mode,
        Hotkey = hotkey is int vk ? HotkeyBinding.FromKey(vk) : null,
        AppExe = app,
        Actions = { new LoopAction { Type = ActionType.Key, KeyVk = 0x01, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds } },
    };

    private static MacroDefinition Macro(string name, int? hotkey = null) => new()
    {
        Name = name,
        Hotkey = hotkey is int vk ? HotkeyBinding.FromKey(vk) : null,
        Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41 } },
    };

    private static RemapDefinition Remap(int source, int target, string note = "") => new()
    {
        Source = HotkeyBinding.FromKey(source),
        Target = HotkeyBinding.FromKey(target),
        Note = note,
    };

    private ItemFileContent Read(string text, params int[] only) =>
        ItemFile.Read(text, _settings, _macros, This, only.Length == 0 ? null : only);

    [Fact]
    public void Loops_macros_and_remaps_come_back_as_new_disabled_items_with_their_fields()
    {
        LoopDefinition loop = Loop("Farm", F6, app: "game.exe");
        MacroDefinition macro = Macro("Craft", F7);
        macro.Speed = 2;
        RemapDefinition remap = Remap(KeyJ, KeyC, "J to C");
        string text = ItemFile.Write(new object[] { loop, macro, remap }, This);

        ItemFileContent content = Read(text);

        Assert.Null(content.Error);
        Assert.Equal("1.17.0", content.App);
        Assert.Equal(new[] { "loop", "macro", "remap" }, content.Entries.Select(e => e.Kind));
        Assert.All(content.Entries, e => Assert.True(e.Selected));
        Assert.All(content.Entries, e => Assert.Empty(e.Notes));

        var importedLoop = Assert.IsType<LoopDefinition>(content.Entries[0].Item);
        Assert.NotEqual(loop.Id, importedLoop.Id);
        Assert.False(importedLoop.Enabled);
        Assert.Equal("Farm", importedLoop.Name);
        Assert.Equal("game.exe", importedLoop.AppExe);
        Assert.Equal(F6, importedLoop.Hotkey!.Vk);
        Assert.Single(importedLoop.Actions);

        var importedMacro = Assert.IsType<MacroDefinition>(content.Entries[1].Item);
        Assert.NotEqual(macro.Id, importedMacro.Id);
        Assert.False(importedMacro.Enabled);
        Assert.Equal(2, importedMacro.Speed);

        var importedRemap = Assert.IsType<RemapDefinition>(content.Entries[2].Item);
        Assert.NotEqual(remap.Id, importedRemap.Id);
        Assert.False(importedRemap.Enabled);
        Assert.Equal("J to C", importedRemap.Note);
        Assert.Equal("J → C", content.Entries[2].Title);
    }

    [Fact]
    public void The_file_is_json_with_its_format_version_and_each_item_as_the_code_view_shows_it()
    {
        MacroDefinition macro = Macro("Craft");
        JsonObject file = JsonNode.Parse(ItemFile.Write(new object[] { macro }, This))!.AsObject();

        Assert.Equal(ItemFile.CurrentFormat, file["Format"]!.GetValue<int>());
        Assert.Equal("1.17.0", file["App"]!.GetValue<string>());
        JsonObject item = file["Items"]![0]!.AsObject();
        Assert.Equal("Macro", item["Kind"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(MacroJson.Serialize(macro)), item["Item"]));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "Items": [] }""")]
    [InlineData("""{ "Format": 1 }""")]
    public void A_file_that_is_not_an_items_file_is_refused(string text)
    {
        Assert.Equal("This is not a PuppyMacro items file.", Read(text).Error);
    }

    [Fact]
    public void A_file_of_a_newer_format_asks_to_update_PuppyMacro()
    {
        string text = """{ "Format": 2, "App": "2.0.0", "Items": [] }""";

        Assert.Equal("This file was made with PuppyMacro 2.0.0, newer than this one (1.17.0). Update PuppyMacro to import it.",
            Read(text).Error);
    }

    [Fact]
    public void An_item_with_fields_of_a_newer_version_asks_to_update_PuppyMacro()
    {
        JsonObject file = JsonNode.Parse(ItemFile.Write(new object[] { Loop("Farm") }, new Version(1, 20, 0)))!.AsObject();
        file["Items"]![0]!["Item"]!["NewField"] = true;

        ItemFileEntry entry = Assert.Single(Read(file.ToJsonString()).Entries);

        Assert.False(entry.Selected);
        Assert.Null(entry.Item);
        Assert.Equal("This file was made with PuppyMacro 1.20.0, newer than this one (1.17.0). Update PuppyMacro to import it.", entry.Problem);
    }

    [Fact]
    public void An_item_with_problems_or_of_an_unknown_kind_is_shown_with_why_and_the_others_still_import()
    {
        LoopDefinition broken = Loop("Broken");
        broken.SoundName = "No such sound";
        JsonObject file = JsonNode.Parse(ItemFile.Write(new object[] { broken, Macro("Good") }, This))!.AsObject();
        file["Items"]!.AsArray().Add(new JsonObject { ["Kind"] = "Clicker", ["Item"] = new JsonObject() });

        ItemFileContent content = Read(file.ToJsonString());

        Assert.Equal(3, content.Entries.Count);
        Assert.Equal("Broken", content.Entries[0].Title);
        Assert.StartsWith("SoundName: Unknown sound.", content.Entries[0].Problem);
        Assert.False(content.Entries[0].Selected);
        Assert.True(content.Entries[1].Selected);
        Assert.Equal("Unknown kind \"Clicker\": PuppyMacro imports loops, macros and remaps.", content.Entries[2].Problem);
        Assert.Equal(new[] { "Good" }, content.SelectedItems.Cast<MacroDefinition>().Select(m => m.Name));
    }

    [Fact]
    public void A_name_already_used_gets_a_number_also_between_items_of_the_file()
    {
        _settings.Loops.Add(Loop("Farm"));
        string text = ItemFile.Write(new object[] { Loop("Farm"), Loop("farm"), Macro("Farm") }, This);

        ItemFileContent content = Read(text);

        Assert.Equal("Farm (2)", content.Entries[0].Title);
        Assert.Equal("A loop has this name: imported as \"Farm (2)\".", Assert.Single(content.Entries[0].Notes));
        Assert.Equal("farm (3)", ((LoopDefinition)content.Entries[1].Item!).Name);
        Assert.Equal("Farm", content.Entries[2].Title); // macros and loops have their own names
        Assert.Empty(content.Entries[2].Notes);
    }

    [Fact]
    public void A_hotkey_already_used_is_left_out_and_a_Hold_item_becomes_Toggle()
    {
        _settings.Loops.Add(Loop("Mine", F6));
        string text = ItemFile.Write(new object[] { Loop("Farm", F6), Loop("Run", F6, ActivationMode.Hold) }, This);

        ItemFileContent content = Read(text);

        var farm = (LoopDefinition)content.Entries[0].Item!;
        Assert.Null(farm.Hotkey);
        Assert.Equal("F6 is already used by the loop \"Mine\": imported without a hotkey.", Assert.Single(content.Entries[0].Notes));
        var run = (LoopDefinition)content.Entries[1].Item!;
        Assert.Null(run.Hotkey);
        Assert.Equal(ActivationMode.Toggle, run.Mode);
        Assert.EndsWith("imported without a hotkey, as Toggle (Hold needs a hotkey).", Assert.Single(content.Entries[1].Notes));
    }

    [Fact]
    public void Two_items_of_the_file_with_the_same_hotkey_keep_it_on_the_first()
    {
        string text = ItemFile.Write(new object[] { Loop("Farm", F8), Macro("Craft", F8) }, This);

        ItemFileContent content = Read(text);

        Assert.Equal(F8, ((LoopDefinition)content.Entries[0].Item!).Hotkey!.Vk);
        Assert.Null(((MacroDefinition)content.Entries[1].Item!).Hotkey);
        Assert.Equal("F8 is already used by the loop \"Farm\" in this file: imported without a hotkey.", Assert.Single(content.Entries[1].Notes));
    }

    [Fact]
    public void A_hotkey_used_in_another_app_is_kept()
    {
        _settings.Loops.Add(Loop("Mine", F6, app: "other.exe"));
        string text = ItemFile.Write(new object[] { Loop("Farm", F6, app: "game.exe") }, This);

        Assert.Equal(F6, ((LoopDefinition)Read(text).Entries[0].Item!).Hotkey!.Vk);
    }

    [Fact]
    public void A_remap_of_a_key_already_remapped_or_used_by_a_hotkey_cannot_be_imported()
    {
        _settings.Remaps.Add(Remap(KeyJ, KeyC));
        string text = ItemFile.Write(new object[] { Remap(KeyJ, 0x44), Loop("Farm", F7), Remap(F7, 0x44), Remap(0x45, 0x44), Remap(0x45, 0x46) }, This);

        ItemFileContent content = Read(text);

        Assert.Equal("J is already remapped for all apps.", content.Entries[0].Problem);
        Assert.Equal("F7 is already used by the loop \"Farm\" in this file. Hotkeys take priority over remaps.", content.Entries[2].Problem);
        Assert.True(content.Entries[3].Selected);
        Assert.Equal("E is already remapped for all apps in this file.", content.Entries[4].Problem);
        Assert.Equal(2, content.SelectedItems.Count());
    }

    [Fact]
    public void Items_not_chosen_are_not_imported_and_leave_their_name_and_hotkey_free()
    {
        string text = ItemFile.Write(new object[] { Loop("Farm", F6), Loop("Farm", F6), Macro("Craft") }, This);

        ItemFileContent content = Read(text, 1, 2);

        Assert.False(content.Entries[0].Selected);
        Assert.True(content.Entries[1].Selected);
        var second = (LoopDefinition)content.Entries[1].Item!;
        Assert.Equal("Farm", second.Name);
        Assert.Equal(F6, second.Hotkey!.Vk);
        Assert.Equal(new[] { "Farm", "Craft" }, content.SelectedItems.Select(i => i is LoopDefinition l ? l.Name : ((MacroDefinition)i).Name));
    }

    [Fact]
    public void The_file_name_is_the_item_name_for_one_item()
    {
        Assert.Equal("Farm  now.puppyitems", ItemFile.FileName(new object[] { Loop("Farm: now") }));
        Assert.Equal("Remap J - C.puppyitems", ItemFile.FileName(new object[] { Remap(KeyJ, KeyC) }));
        Assert.Equal("J to C.puppyitems", ItemFile.FileName(new object[] { Remap(KeyJ, KeyC, "J to C") }));
        Assert.Equal("PuppyMacro items.puppyitems", ItemFile.FileName(new object[] { Loop("A"), Macro("B") }));
    }
}
