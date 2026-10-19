using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The Library for AI apps (FakeAiHost as the app and the user): search_library's filters and short answers,
/// get_component, save_component (new, in a new or an existing collection, changed, refused, confirmed), a macro linking
/// a component (saved, refused when it is missing), get_item's outline and path, update_actions leaving linked rows
/// alone, and collections coming in with import_items.
/// </summary>
public class AiLibraryToolsTests : IDisposable
{
    private const string App = "Claude (claude-code)";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiTools _tools;
    private readonly LibraryComponent _rare;
    private readonly LibraryComponent _slot;

    public AiLibraryToolsTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        _settings.AllowAiAccess = true;
        _settings.AiAskBeforeChanging = false;
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("data/macros")));
        _tools = new AiTools(_host, new AiActivityLog(_folder.File("ai-activity.json")));
        _rare = new LibraryComponent
        {
            Name = "Rare item in slot", Purpose = ComponentPurpose.Find, Concept = "rare_item_in_slot",
            Description = "Orange border at the slot",
            Actions = { new MacroAction { Type = MacroActionType.If, If = new MacroIf
            {
                Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 5, Y = 6, Color = "#463405", Tolerance = 5 } },
                Then = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 5, Y = 6 } },
            } } },
        };
        _slot = new LibraryComponent
        {
            Name = "Slot 1", Purpose = ComponentPurpose.Point, Concept = "inventory_slot_1",
            Actions = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 3912, Y = 1056 } },
        };
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Diablo IV 5120x1440", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe",
            Screen = new LibraryScreen { Width = 5120, Height = 1440, ScalePercent = 100 },
            Components = { _rare, _slot },
        });
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Excel", Domain = LibraryDomain.Office, AppExe = "EXCEL.EXE",
            Components = { new LibraryComponent { Name = "Cell A1", Purpose = ComponentPurpose.Point, Concept = "cell_a1",
                Actions = { new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 10, Y = 10 } } } },
        });
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Call(string tool, object? args = null) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static int Found(AiToolResult result) => int.Parse(Regex.Match(result.Text, "\"Found\":(\\d+)").Groups[1].Value);

    [Fact]
    public async Task Search_library_narrows_by_the_name_s_parts_and_words()
    {
        Assert.Equal(3, Found(await Call("search_library")));
        Assert.Equal(2, Found(await Call("search_library", new { domain = "game" })));
        Assert.Equal(2, Found(await Call("search_library", new { app = "diablo_iv" })));
        Assert.Equal(2, Found(await Call("search_library", new { purpose = "point" })));
        var rare = await Call("search_library", new { words = "rare slot" });
        Assert.Equal(1, Found(rare));
        Assert.Contains("game-diablo_iv-find-rare_item_in_slot", rare.Text);
        Assert.Contains("5120x1440 at 0,0, 100%", rare.Text);
        Assert.Equal(1, Found(await Call("search_library", new { words = "orange" }))); // the description too

        var limited = await Call("search_library", new { limit = 1 });
        Assert.Contains("Only the first 1", limited.Text);
    }

    [Fact]
    public async Task Get_component_gives_its_actions_collection_and_the_macros_that_link_it()
    {
        var macro = new MacroDefinition { Name = "Farm", Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = _rare.Id } } };
        _host.Macros.Macros.Add(macro);

        var read = await Call("get_component", new { id = _rare.Id.ToString() });

        Assert.False(read.IsError, read.Text);
        Assert.Contains("\"Color\":\"#463405\"", read.Text);
        Assert.Contains("Diablo IV 5120x1440", read.Text);
        Assert.Contains("\"Farm\"", read.Text);
        Assert.True((await Call("get_component", new { id = Guid.NewGuid().ToString() })).IsError);
    }

    private static object NewComponent(string concept = "remove_one_affix", object? actions = null) => new
    {
        Name = "Remove one affix",
        Purpose = "Act",
        Concept = concept,
        Actions = actions ?? new object[] { new { Type = "PressKey", Vk = 65 } },
    };

    [Fact]
    public async Task Save_component_makes_a_new_one_in_a_new_or_an_existing_collection()
    {
        var created = await Call("save_component", new { collection = new { name = "Diablo IV 2560x1440", domain = "game", app = "Diablo IV.exe" }, component = NewComponent() });
        Assert.False(created.IsError, created.Text);
        Assert.Contains("game-diablo_iv-act-remove_one_affix", created.Text);
        Assert.Equal(3, _host.Macros.Library.Collections.Count);

        Guid excel = _host.Macros.Library.Collections.Single(c => c.Name == "Excel").Id;
        var added = await Call("save_component", new { collection = new { id = excel.ToString() }, component = NewComponent("save_as_pdf") });
        Assert.False(added.IsError, added.Text);
        Assert.Equal(2, _host.Macros.Library.FindCollection(excel)!.Components.Count);
    }

    [Fact]
    public async Task Save_component_with_an_id_replaces_its_content()
    {
        var changed = await Call("save_component", new
        {
            id = _slot.Id.ToString(),
            component = new { Name = "Slot 1", Purpose = "Point", Concept = "inventory_slot_1", Actions = new object[] { new { Type = "Click", Vk = 1, X = 4000, Y = 1056 } } },
        });

        Assert.False(changed.IsError, changed.Text);
        Assert.Equal(4000, _host.Macros.Library.Find(_slot.Id)!.Value.Component.Actions[0].X);
    }

    [Fact]
    public async Task Save_component_refuses_what_the_Library_refuses()
    {
        Assert.Contains("lowercase", (await Call("save_component", new { collection = new { name = "X" }, component = NewComponent("Remove Affix") })).Text);
        var missing = new object[] { new { Type = "Component", ComponentId = Guid.NewGuid() } };
        Assert.True((await Call("save_component", new { collection = new { name = "X" }, component = NewComponent(actions: missing) })).IsError);
        Assert.Contains("\"collection\"", (await Call("save_component", new { component = NewComponent() })).Text);
        Assert.Contains("No collection has the Id", (await Call("save_component", new { collection = new { id = Guid.NewGuid().ToString() }, component = NewComponent() })).Text);
        Assert.Equal(2, _host.Macros.Library.Collections.Count);
    }

    [Fact]
    public async Task Save_component_waits_for_the_user_s_OK_when_they_chose_it()
    {
        _settings.AiAskBeforeChanging = true;
        _host.ConfirmAnswer = AiAnswer.Cancelled;

        var refused = await Call("save_component", new { collection = new { name = "New" }, component = NewComponent() });

        Assert.Equal(2, _host.Macros.Library.Collections.Count);
        Assert.DoesNotContain("Saved", refused.Text);
    }

    [Fact]
    public async Task A_macro_links_a_component_by_its_id_and_a_missing_one_is_refused()
    {
        var saved = await Call("save_macro", new { item = new { Name = "Farm", Actions = new object[] { new { Type = "Component", ComponentId = _rare.Id } } } });
        Assert.False(saved.IsError, saved.Text);

        var missing = await Call("save_macro", new { item = new { Name = "Gone", Actions = new object[] { new { Type = "Component", ComponentId = Guid.NewGuid() } } } });
        Assert.True(missing.IsError);
        Assert.Contains("Library", missing.Text);
    }

    [Fact]
    public async Task Get_item_reads_a_big_macro_in_pieces_and_lists_its_links()
    {
        var macro = new MacroDefinition
        {
            Name = "D4",
            Actions =
            {
                new MacroAction { Type = MacroActionType.If, Name = "Slot 1 Magic or Rare?", If = new MacroIf
                {
                    Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 1, Color = "#000000" } },
                    Then = { new MacroAction { Type = MacroActionType.Component, ComponentId = _slot.Id }, new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } },
                } },
            },
        };
        _host.Macros.Macros.Add(macro);

        var outline = await Call("get_item", new { id = macro.Id.ToString(), outline = true });
        Assert.Contains("/Actions/0 If \"Slot 1 Magic or Rare?\" [Pixel] Then 2, Else 0", outline.Text);
        Assert.DoesNotContain("/Actions/0/If/Then/0", outline.Text); // depth 1
        var deeper = await Call("get_item", new { id = macro.Id.ToString(), outline = true, outline_depth = 2 });
        Assert.Contains("/Actions/0/If/Then/0 Component game-diablo_iv-point-inventory_slot_1", deeper.Text);

        var part = await Call("get_item", new { id = macro.Id.ToString(), path = "/Actions/0/If/Then/1" });
        Assert.Equal("{\"Vk\":65}", part.Text); // compact: PressKey is the default Type
        Assert.True((await Call("get_item", new { id = macro.Id.ToString(), path = "/Actions/9" })).IsError);

        var whole = await Call("get_item", new { id = macro.Id.ToString() });
        Assert.Contains($"- {_slot.Id}: game-diablo_iv-point-inventory_slot_1", whole.Text);
    }

    [Fact]
    public async Task Update_actions_leaves_linked_rows_as_they_are()
    {
        var macro = new MacroDefinition
        {
            Name = "Mixed",
            Actions =
            {
                new MacroAction { Type = MacroActionType.Component, ComponentId = _slot.Id },
                new MacroAction { Type = MacroActionType.PressKey, Vk = 65 },
            },
        };
        _host.Macros.Macros.Add(macro);

        var updated = await Call("update_actions", new { id = macro.Id.ToString(), set = new { DelayMs = 100 } });

        Assert.False(updated.IsError, updated.Text);
        var saved = (MacroDefinition)_host.Saved.Last().Item;
        Assert.Equal(0, saved.Actions[0].DelayMs);
        Assert.Equal(100, saved.Actions[1].DelayMs);
    }

    [Fact]
    public async Task Import_items_brings_the_collections_its_macros_link()
    {
        var macro = new MacroDefinition { Name = "Farm", Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = _rare.Id } } };
        string file = ItemFile.Write(new object[] { macro }, ItemFile.AppVersion, _host.Macros.Images, _host.Macros.Library);
        _host.Macros.Library.Collections.Clear();

        var imported = await Call("import_items", new { content = file });

        Assert.False(imported.IsError, imported.Text);
        Assert.Contains("Library collection \"Diablo IV 5120x1440\"", imported.Text);
        Assert.NotNull(_host.Macros.Library.Find(_rare.Id));
    }
    [Fact]
    public async Task Get_overview_filters_by_words_and_pages_each_list()
    {
        for (int n = 1; n <= 5; n++)
            _host.Macros.Macros.Add(new MacroDefinition { Name = $"Diablo salvage {n}", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } } });
        _host.Macros.Macros.Add(new MacroDefinition { Name = "Excel report", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } } });

        var page = await Call("get_overview", new { words = "diablo", offset = 1, limit = 2 });

        Assert.Contains("\"Macros\": 5", page.Text);
        Assert.Contains("Diablo salvage 2", page.Text);
        Assert.Contains("Diablo salvage 3", page.Text);
        Assert.DoesNotContain("Diablo salvage 1", page.Text);
        Assert.DoesNotContain("Excel report", page.Text);
    }

    [Fact]
    public async Task Get_overview_gives_each_item_s_characters_as_get_item_gives_them()
    {
        var macro = new MacroDefinition { Name = "Farm", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 65 } } };
        _host.Macros.Macros.Add(macro);
        var loop = _settings.Loops.First();
        var remap = new RemapDefinition { Source = HotkeyBinding.FromKey(0x85), Target = HotkeyBinding.FromKey(0x86), Note = "F22 to F23" };
        _settings.Remaps.Add(remap);

        using var overview = JsonDocument.Parse((await Call("get_overview")).Text);

        int Of(string list, Guid id) => overview.RootElement.GetProperty(list).EnumerateArray()
            .Single(e => e.GetProperty("Id").GetGuid() == id).GetProperty("Characters").GetInt32();
        Assert.Equal((await Call("get_item", new { id = macro.Id })).Text.Length, Of("Macros", macro.Id));
        Assert.Equal((await Call("get_item", new { id = loop.Id })).Text.Length, Of("Loops", loop.Id));
        Assert.Equal((await Call("get_item", new { id = remap.Id })).Text.Length, Of("Remaps", remap.Id));
    }

    [Fact]
    public async Task Get_overview_lists_the_library_s_collections_with_their_components()
    {
        using var all = JsonDocument.Parse((await Call("get_overview")).Text);
        var library = all.RootElement.GetProperty("Library");
        Assert.Equal(2, library.GetProperty("Collections").GetInt32());
        var diablo = library.GetProperty("List").EnumerateArray().Single(c => c.GetProperty("Name").GetString() == "Diablo IV 5120x1440");
        Assert.Equal(2, diablo.GetProperty("Components").GetInt32());
        Assert.Equal(_host.Macros.Library.Collections[0].Id, diablo.GetProperty("Id").GetGuid());

        _host.Macros.Library.Collections.Clear();
        using var empty = JsonDocument.Parse((await Call("get_overview")).Text);
        Assert.Equal(0, empty.RootElement.GetProperty("Library").GetProperty("Collections").GetInt32());
        Assert.Empty(empty.RootElement.GetProperty("Library").GetProperty("List").EnumerateArray());
    }

    [Fact]
    public async Task Get_overview_lists_the_whole_library_whatever_words_offset_and_limit_say()
    {
        // An AI app looking for its macro "Sell all inventory" asked get_overview with words "sell": the collections, none
        // of them named "sell", were left out, and it believed the Library had no components (19 Oct 2026).
        foreach (object args in new object[] { new { words = "sell" }, new { offset = 100 }, new { offset = 1, limit = 1 } })
        {
            using var answer = JsonDocument.Parse((await Call("get_overview", args)).Text);
            var library = answer.RootElement.GetProperty("Library");
            Assert.Equal(2, library.GetProperty("Collections").GetInt32());
            Assert.Equal(["Diablo IV 5120x1440", "Excel"],
                library.GetProperty("List").EnumerateArray().Select(c => c.GetProperty("Name").GetString()));
        }
    }

    [Fact]
    public async Task Search_library_gives_one_collection_s_components()
    {
        var diablo = _host.Macros.Library.Collections[0];
        var excel = _host.Macros.Library.Collections[1];

        Assert.Equal(2, Found(await Call("search_library", new { collection = diablo.Id.ToString() })));
        Assert.Equal(1, Found(await Call("search_library", new { collection = excel.Id.ToString() })));
        Assert.Equal(1, Found(await Call("search_library", new { collection = diablo.Id.ToString(), purpose = "find" }))); // with the others
        Assert.Equal(0, Found(await Call("search_library", new { collection = Guid.NewGuid().ToString() })));

        var bad = await Call("search_library", new { collection = "slot" });
        Assert.True(bad.IsError);
        Assert.Contains("\"collection\" must be a collection's Id", bad.Text);
    }
}
