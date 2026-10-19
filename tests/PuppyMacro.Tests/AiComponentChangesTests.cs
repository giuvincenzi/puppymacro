using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Changing and deleting Library components from an AI app (FakeAiHost as the app and the user): patch_component (only what
/// changes, then save_component's checks, guided update and permission), delete_component (refused while something uses
/// it, confirmed as a deletion, kept for Restore), the answers that name the components a change left called by nothing,
/// and the tools for loops, macros and remaps naming the component tool when given a component's Id.
/// </summary>
[Collection(nameof(DebugStore))]
public class AiComponentChangesTests : IDisposable
{
    private const string App = "Claude (claude-code)";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiActivityLog _log;
    private readonly AiTools _tools;
    private readonly LibraryComponent _strip;
    private readonly LibraryComponent _all;
    private readonly LibraryComponent _free;
    private readonly LibraryCollection _d4;

    public AiComponentChangesTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.AiAskBeforeChanging = false;
        _settings.AiAskBeforeDeleting = false;
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("data/macros")));
        _log = new AiActivityLog(_folder.File("ai-activity.json"));
        _tools = new AiTools(_host, _log);
        _strip = Comp("Strip one slot", new[] { SlotParam(), HoldParam(@default: Number(30)) },
            Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold")));
        _all = Comp("Strip two", Call(_strip, ("Slot", Point(1, 2))), Call(_strip, ("Slot", Point(3, 4))));
        _free = Comp("Select remove affix", Press(0x41));
        _d4 = new LibraryCollection
        {
            Name = "Diablo IV", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe",
            Components = { _strip, _all, _free },
        };
        _host.Macros.Library.Collections.Add(_d4);
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Tool(string tool, object? args = null) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private LibraryComponent Stored(LibraryComponent component) => _host.Macros.Library.Find(component.Id)!.Value.Component;

    private static object Replace(string path, object value) => new { op = "replace", path, value };

    // ================= patch_component =================

    [Fact]
    public async Task Patch_component_changes_only_what_it_names()
    {
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/DelayMs", 250) } });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Saved the component \"game-diablo_iv-act-strip_one_slot\"", result.Text);
        var stored = Stored(_strip);
        Assert.Equal(250, stored.Actions[0].DelayMs);
        Assert.Equal(3912, stored.Actions[0].X);
        Assert.Equal(2, stored.Parameters.Count);
        Assert.Equal(_strip.Id, stored.Id);
        // Kept for Restore, as save_component's changes are.
        var entry = _log.Entries().Single(e => e.ItemId == _strip.Id);
        Assert.Equal("component", entry.ItemKind);
        Assert.Contains("3912", entry.Before);
    }

    [Fact]
    public async Task Patch_component_reaches_a_field_get_component_leaves_out_at_its_default()
    {
        // The click's Name is empty: get_component's compact JSON leaves it out, the patch still replaces it.
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/Name", "Click the slot") } });
        Assert.False(result.IsError, result.Text);
        Assert.Equal("Click the slot", Stored(_strip).Actions[0].Name);
    }

    [Fact]
    public async Task Patch_component_that_changes_nothing_says_so()
    {
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/X", 3912) } });
        Assert.False(result.IsError, result.Text);
        Assert.Equal("Nothing to change: the component is already like this.", result.Text);
        Assert.DoesNotContain(_log.Entries(), e => e.ItemId == _strip.Id);
    }

    [Fact]
    public async Task Patch_component_with_a_wrong_operation_names_it_and_saves_nothing()
    {
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new object[] { Replace("/Actions/0/DelayMs", 250), Replace("/Actions/9/X", 1) } });
        Assert.True(result.IsError);
        Assert.Contains("/Actions/9", result.Text);
        Assert.NotEqual(250, Stored(_strip).Actions[0].DelayMs); // all or nothing
    }

    [Fact]
    public async Task Patch_component_that_breaks_the_component_is_refused_with_the_checks_problem()
    {
        // The click bound to a parameter the component doesn't have.
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/Bind/Point", "Place") } });
        Assert.True(result.IsError);
        Assert.Contains("nothing was saved", result.Text);
        Assert.Contains("Place", result.Text);
        Assert.Equal("Slot", Stored(_strip).Actions[0].Bind!["Point"]);
    }

    [Fact]
    public async Task Patch_component_needs_an_id_and_a_patch()
    {
        Assert.True((await Tool("patch_component", new { patch = new[] { Replace("/Name", "X") } })).IsError);
        var noPatch = await Tool("patch_component", new { id = _strip.Id.ToString() });
        Assert.True(noPatch.IsError);
        Assert.Contains("\"patch\"", noPatch.Text);
        var notList = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = "replace" });
        Assert.True(notList.IsError);
    }

    [Fact]
    public async Task Patch_component_on_an_Id_that_is_no_component_says_which_tool_changes_it()
    {
        var macro = Macro("D4");
        _host.Macros.Macros.Add(macro);

        var onMacro = await Tool("patch_component", new { id = macro.Id.ToString(), patch = new[] { Replace("/Name", "X") } });
        Assert.True(onMacro.IsError);
        Assert.Equal($"The Id {macro.Id} is a macro, not a component. patch_item changes it.".Replace("patch_item", AiTools.Name("patch_item")), onMacro.Text);

        var unknown = Guid.NewGuid();
        var none = await Tool("patch_component", new { id = unknown.ToString(), patch = new[] { Replace("/Name", "X") } });
        Assert.True(none.IsError);
        Assert.Contains($"No component has the Id {unknown}", none.Text);
    }

    [Fact]
    public async Task A_patch_that_changes_the_calls_asks_their_values_then_saves_them_together()
    {
        var macro = Macro("D4", Call(_strip, ("Slot", Point(5, 6))));
        _host.Macros.Macros.Add(macro);
        var settle = new { Name = "Settle", Type = "Number", Unit = "Ms", Example = 100 };
        var add = new { op = "add", path = "/Parameters/-", value = settle };

        var asked = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { add } });
        Assert.True(asked.IsError);
        Assert.Contains("call_values", asked.Text);
        Assert.Contains("\"D4\" (macro, 1)", asked.Text);
        Assert.Contains("\"Strip two\" (component, 2)", asked.Text);
        Assert.Equal(2, Stored(_strip).Parameters.Count);

        var saved = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { add }, call_values = new { Settle = 120 } });
        Assert.False(saved.IsError, saved.Text);
        Assert.Contains("Updated 3 call(s)", saved.Text);
        Assert.Equal(120, (int)SignatureUpdate.CallsTo(_host.Macros.Find(macro.Id)!.Actions, _strip.Id).Single()!["Settle"]!);
    }

    [Fact]
    public async Task A_patch_that_renames_a_parameter_keeps_the_calls_values()
    {
        var macro = Macro("D4", Call(_strip, ("Slot", Point(5, 6))));
        _host.Macros.Macros.Add(macro);

        var saved = await Tool("patch_component", new
        {
            id = _strip.Id.ToString(),
            patch = new[] { Replace("/Parameters/0/Name", "Place"), Replace("/Actions/0/Bind/Point", "Place") },
            renamed = new { Slot = "Place" },
        });
        Assert.False(saved.IsError, saved.Text);
        Assert.True(JsonNode.DeepEquals(Point(5, 6), _host.Macros.Find(macro.Id)!.Actions[0].Arguments!["Place"]));
    }

    [Fact]
    public async Task Patch_component_asks_the_user_when_they_chose_it()
    {
        _settings.AiAskBeforeChanging = true;
        _host.ConfirmAnswer = AiAnswer.Cancelled;

        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/DelayMs", 250) } });
        Assert.Equal(Strings.Ai_ConfirmChangedComponent, _host.Confirmed.Single().Title);
        Assert.Contains("cancel", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(250, Stored(_strip).Actions[0].DelayMs);
    }

    [Fact]
    public async Task A_patch_of_a_bound_field_says_the_field_holds_its_parameter_s_Example()
    {
        // The click's point is bound to Slot: PuppyMacro writes Slot's Example there, so the patch changes nothing.
        var result = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/X", 4000) } });
        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("Nothing to change: the component is already like this. A field bound to a parameter holds the parameter's Example", result.Text);
        Assert.Equal(3912, Stored(_strip).Actions[0].X);

        // With another change the save goes on, and says it too.
        var both = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/X", 4000), Replace("/Actions/0/DelayMs", 250) } });
        Assert.Contains("Saved the component", both.Text);
        Assert.Contains("the parameter's Example or Default changes it", both.Text);
        Assert.Equal(3912, Stored(_strip).Actions[0].X);

        // The Example changes it.
        await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Parameters/0/Example", new { X = 4000, Y = 1056 }) } });
        Assert.Equal(4000, Stored(_strip).Actions[0].X);
    }

    // ================= delete_component =================

    [Fact]
    public async Task Delete_component_deletes_one_nothing_uses_and_keeps_it_for_Restore()
    {
        var result = await Tool("delete_component", new { id = _free.Id.ToString() });

        Assert.False(result.IsError, result.Text);
        Assert.Equal("Deleted the component \"game-diablo_iv-act-select_remove_affix\". The user can restore it in PuppyMacro.", result.Text);
        Assert.Null(_host.Macros.Library.Find(_free.Id));
        var entry = _log.Entries().Single(e => e.ItemId == _free.Id);
        Assert.Equal("collection", entry.ItemKind);
        Assert.Equal(AiActivityResult.Applied, entry.Result);

        // Restore puts it back in its collection: what MainWindow does with the entry, through the same host call.
        var kept = JsonSerializer.Deserialize<LibraryCollection>(entry.Before!, CodeJson.Options)!;
        Assert.Equal(_d4.Id, kept.Id);
        var back = Assert.Single(kept.Components);
        Assert.Null(_host.SaveComponent(back, kept, "You"));
        Assert.Equal(_d4.Id, _host.Macros.Library.Find(_free.Id)!.Value.Collection.Id);
        Assert.Equal(3, _d4.Components.Count);
    }

    [Fact]
    public async Task A_deleted_component_comes_back_with_its_collection_when_that_is_gone_too()
    {
        var alone = Comp("Alone", Press());
        var general = new LibraryCollection { Name = "General", Components = { alone } };
        _host.Macros.Library.Collections.Add(general);
        await Tool("delete_component", new { id = alone.Id.ToString() });
        _host.Macros.Library.Collections.Remove(general);

        var entry = _log.Entries().Single(e => e.ItemId == alone.Id);
        var kept = JsonSerializer.Deserialize<LibraryCollection>(entry.Before!, CodeJson.Options)!;
        Assert.Equal("General", kept.Name);
        Assert.Null(_host.SaveComponent(kept.Components[0], kept, "You"));
        Assert.Equal("General", _host.Macros.Library.Find(alone.Id)!.Value.Collection.Name);
    }

    [Fact]
    public async Task Delete_component_refuses_one_a_macro_or_a_component_uses_and_names_them()
    {
        _host.Macros.Macros.Add(Macro("D4 Strip", Call(_strip, ("Slot", Point(5, 6)))));

        var used = await Tool("delete_component", new { id = _strip.Id.ToString() });
        Assert.True(used.IsError);
        Assert.Equal("Strip one slot is linked in these macros: D4 Strip, and used by these components: Strip two. Stop using it there first.", used.Text);
        Assert.NotNull(_host.Macros.Library.Find(_strip.Id));
        Assert.Empty(_log.Entries());
    }

    [Fact]
    public async Task Delete_component_s_refusal_is_in_English_whatever_the_user_s_language()
    {
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        try
        {
            var used = await Tool("delete_component", new { id = _strip.Id.ToString() });
            Assert.Contains("is used by these components: Strip two", used.Text);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public async Task Delete_component_asks_the_user_when_they_chose_it()
    {
        _settings.AiAskBeforeDeleting = true;
        _host.ConfirmAnswer = AiAnswer.Cancelled;

        var cancelled = await Tool("delete_component", new { id = _free.Id.ToString() });
        var (title, lines) = _host.Confirmed.Single();
        Assert.Equal(Strings.Ai_ConfirmDeleteComponent, title);
        Assert.Equal(new[] { "Select remove affix" }, lines);
        Assert.Contains("cancel", cancelled.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(_host.Macros.Library.Find(_free.Id));

        _host.ConfirmAnswer = AiAnswer.Done;
        var deleted = await Tool("delete_component", new { id = _free.Id.ToString() });
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Null(_host.Macros.Library.Find(_free.Id));
    }

    [Fact]
    public async Task Ask_before_changing_does_not_ask_for_a_deletion()
    {
        _settings.AiAskBeforeChanging = true;
        var deleted = await Tool("delete_component", new { id = _free.Id.ToString() });
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Empty(_host.Confirmed);
    }

    [Fact]
    public async Task Delete_component_on_an_Id_that_is_no_component_says_which_tool_deletes_it()
    {
        var macro = Macro("D4");
        _host.Macros.Macros.Add(macro);
        var onMacro = await Tool("delete_component", new { id = macro.Id.ToString() });
        Assert.True(onMacro.IsError);
        Assert.Contains($"is a macro, not a component. {AiTools.Name("delete_item")} deletes it.", onMacro.Text);
        Assert.NotNull(_host.Macros.Find(macro.Id));

        var none = await Tool("delete_component", new { id = Guid.NewGuid().ToString() });
        Assert.True(none.IsError);
        Assert.Contains("No component has the Id", none.Text);
    }

    [Fact]
    public async Task Delete_component_is_refused_while_the_user_has_an_editor_open()
    {
        _host.UserHasOpen = "the macro editor of \"D4\"";
        var refused = await Tool("delete_component", new { id = _free.Id.ToString() });
        Assert.True(refused.IsError);
        Assert.NotNull(_host.Macros.Library.Find(_free.Id));
    }

    // ================= Components left unused =================

    [Fact]
    public async Task Deleting_the_last_caller_names_the_component_it_left_unused()
    {
        var result = await Tool("delete_component", new { id = _all.Id.ToString() });
        Assert.False(result.IsError, result.Text);
        Assert.EndsWith($"\nNo macro or component calls these components any more: \"game-diablo_iv-act-strip_one_slot\" (Id {_strip.Id}).", result.Text);
    }

    [Fact]
    public async Task A_component_change_that_stops_calling_another_names_it()
    {
        // The test's case: a component that stopped calling the one it had made just before.
        var caller = Comp("Craft unique slot", Call(_free));
        _d4.Components.Add(caller);

        var result = await Tool("patch_component", new { id = caller.Id.ToString(), patch = new[] { Replace("/Actions/0", JsonNode.Parse(CodeJson.Serialize(Press(0x42)))!) } });
        Assert.False(result.IsError, result.Text);
        Assert.Contains($"No macro or component calls these components any more: \"game-diablo_iv-act-select_remove_affix\" (Id {_free.Id}).", result.Text);
    }

    [Fact]
    public async Task A_macro_change_that_removes_the_last_link_names_the_component()
    {
        var macro = Macro("D4", Call(_free), Press());
        _host.Macros.Macros.Add(macro);

        var result = await Tool("patch_item", new { id = macro.Id.ToString(), patch = new[] { new { op = "remove", path = "/Actions/0" } } });
        Assert.False(result.IsError, result.Text);
        Assert.Contains("No macro or component calls these components any more: \"game-diablo_iv-act-select_remove_affix\"", result.Text);
    }

    [Fact]
    public async Task Deleting_a_macro_names_the_components_only_it_used()
    {
        var only = Macro("D4", Call(_free));
        var another = Macro("D4 again", Call(_all));
        _host.Macros.Macros.Add(only);
        _host.Macros.Macros.Add(another);

        var result = await Tool("delete_item", new { id = only.Id.ToString() });
        Assert.Contains("\"game-diablo_iv-act-select_remove_affix\"", result.Text);
        var second = await Tool("delete_item", new { id = another.Id.ToString() });
        // Strip two is unused now; Strip one slot is still called by Strip two.
        Assert.Contains("\"game-diablo_iv-act-strip_two\"", second.Text);
        Assert.DoesNotContain("strip_one_slot", second.Text);
    }

    [Fact]
    public async Task A_change_that_leaves_every_component_used_says_nothing_about_it()
    {
        var macro = Macro("D4", Call(_free), Press());
        _host.Macros.Macros.Add(macro);
        _host.Macros.Macros.Add(Macro("Other", Call(_free)));

        var stillUsed = await Tool("patch_item", new { id = macro.Id.ToString(), patch = new[] { new { op = "remove", path = "/Actions/0" } } });
        Assert.DoesNotContain("No macro or component calls", stillUsed.Text);
        // A component nothing called before (just made, or never linked) is not named by an unrelated change.
        var unrelated = await Tool("patch_component", new { id = _strip.Id.ToString(), patch = new[] { Replace("/Actions/0/DelayMs", 250) } });
        Assert.DoesNotContain("No macro or component calls", unrelated.Text);
    }

    // ================= Tools for items given a component's Id =================

    [Theory]
    [InlineData("get_item", "get_component reads it.")]
    [InlineData("patch_item", "patch_component changes it.")]
    [InlineData("delete_item", "delete_component deletes it.")]
    [InlineData("set_enabled", "A component has no on or off: it plays where a macro links it.")]
    public async Task An_item_tool_given_a_component_s_Id_names_it_and_the_tool_for_components(string tool, string hint)
    {
        object args = tool switch
        {
            "patch_item" => new { id = _free.Id.ToString(), patch = new[] { Replace("/Name", "X") } },
            "set_enabled" => new { id = _free.Id.ToString(), enabled = false },
            _ => new { id = _free.Id.ToString() },
        };
        var result = await Tool(tool, args);
        Assert.True(result.IsError);
        Assert.Equal($"The Id {_free.Id} is the component \"game-diablo_iv-act-select_remove_affix\", not a loop, macro or remap. " + AiTools.Prefixed(hint),
            result.Text);
        Assert.NotNull(_host.Macros.Library.Find(_free.Id));
    }

    [Fact]
    public async Task Update_actions_given_a_component_s_Id_names_patch_component()
    {
        var result = await Tool("update_actions", new { id = _free.Id.ToString(), set = new { DelayMs = 10 } });
        Assert.True(result.IsError);
        Assert.Contains($"is the component \"game-diablo_iv-act-select_remove_affix\", not a loop or macro. {AiTools.Name("patch_component")} changes it.", result.Text);
    }

    // ================= The tools and the rules =================

    [Fact]
    public void The_component_tools_are_listed_with_honest_annotations()
    {
        var patch = _tools.All.Single(t => t.Name == AiTools.Name("patch_component"));
        var delete = _tools.All.Single(t => t.Name == AiTools.Name("delete_component"));
        Assert.Equal("Change part of a component", patch.Title);
        Assert.Equal("Delete a component", delete.Title);
        Assert.All(new[] { patch, delete }, t =>
        {
            Assert.False(t.ReadOnly);
            Assert.True(t.Destructive);
            Assert.False(t.Idempotent);
        });
        Assert.Contains("search_library", JsonDocument.Parse(delete.InputSchema).RootElement.GetProperty("properties").GetProperty("id").GetProperty("description").GetString());
        Assert.Contains("search_library", JsonDocument.Parse(_tools.All.Single(t => t.Name == AiTools.Name("get_component")).InputSchema)
            .RootElement.GetProperty("properties").GetProperty("id").GetProperty("description").GetString());
    }

    [Fact]
    public void The_rules_say_to_leave_nothing_behind()
    {
        Assert.Contains("Leave nothing behind: when a change of yours stops using a component you made, use it again or delete it.", AiTools.GlobalRules);
        Assert.Contains("- Leave nothing behind", _tools.FirstAnswer());
        Assert.Contains("patch_component and puppymacro_delete_component", AiTools.InstructionsText);
    }
}
