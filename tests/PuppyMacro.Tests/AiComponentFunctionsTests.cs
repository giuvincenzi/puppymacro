using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Components as functions for AI apps (FakeAiHost as the app and the user): search_library's signature and Condition,
/// get_component's call to copy and who calls it, save_component with parameters (examples written in bound fields, a Bind
/// refused), a signature change that asks call_values and then saves the calls with it, renamed parameters, values outside
/// their limits refused, get_schema("component"), the outline with the calls' values and the components a macro calls
/// through its conditions, check_condition of a Component condition, and the texts that explain functions.
/// </summary>
[Collection(nameof(DebugStore))]
public class AiComponentFunctionsTests : IDisposable
{
    private const string App = "Claude (claude-code)";

    private readonly TempFolder _folder = new();
    private readonly AppSettings _settings = AppSettings.CreateDefault();
    private readonly FakeAiHost _host;
    private readonly AiTools _tools;
    private readonly LibraryComponent _strip;
    private readonly LibraryComponent _rare;
    private readonly LibraryComponent _all;

    private static readonly object Asking = new
    {
        title = "Show the inventory",
        description = "We check the rare item.",
        steps = new[] { "Open the inventory", "Press Enter" },
    };

    public AiComponentFunctionsTests()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        DebugStore.Root = _folder.File("debug");
        _settings.AllowAiAccess = true;
        _settings.AiAskBeforeChanging = false;
        _host = new FakeAiHost(_settings, new MacroLibrary(_folder.File("data/macros")));
        _tools = new AiTools(_host, new AiActivityLog(_folder.File("ai-activity.json")));
        _strip = Comp("Strip one slot", new[] { SlotParam(), HoldParam(@default: Number(30)) },
            Bound(Click(3912, 1056), ("Point", "Slot"), ("HoldMs", "Hold")));
        var pixel = Bound(Pixel(3912, 1056, "#463405"), ("Point", "Slot"));
        _rare = Comp("Rare item in slot", new[] { SlotParam() }, If(pixel));
        _rare.Returns = new ComponentReturns { Found = pixel.Id };
        _all = Comp("Strip two", Call(_strip, ("Slot", Point(1, 2))), Call(_strip, ("Slot", Point(3, 4))));
        _host.Macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Diablo IV", Domain = LibraryDomain.Game, AppExe = "Diablo IV.exe",
            Components = { _strip, _rare, _all },
        });
    }

    public void Dispose() => _folder.Dispose();

    private Task<AiToolResult> Tool(string tool, object? args = null) =>
        _tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args ?? new { }), App, TestContext.Current.CancellationToken);

    private static object With(object extra) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(Asking))!
            .Concat(JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(extra))!)
            .ToDictionary(p => p.Key, p => p.Value);

    /// <summary>A component as an AI app writes it, from a model one.</summary>
    private static JsonNode Json(LibraryComponent component) => JsonNode.Parse(CodeJson.Serialize(component))!;

    [Fact]
    public async Task Search_library_gives_each_component_s_signature_and_whether_it_is_a_condition()
    {
        var result = await Tool("search_library", new { words = "strip" });
        Assert.Contains("\"Parameters\":\"Slot: Point; Hold: Number (Ms) = 30\"", result.Text);
        var rare = await Tool("search_library", new { words = "rare" });
        Assert.Contains("\"Condition\":true", rare.Text);
        Assert.DoesNotContain("\"Condition\"", result.Text);
    }

    [Fact]
    public async Task Get_component_gives_its_signature_a_call_to_copy_and_who_calls_it()
    {
        _host.Macros.Macros.Add(Macro("D4", Call(_strip, ("Slot", Point(5, 6)))));

        using var strip = JsonDocument.Parse((await Tool("get_component", new { id = _strip.Id.ToString() })).Text);
        var root = strip.RootElement;
        Assert.Equal("Slot", root.GetProperty("Component").GetProperty("Parameters")[0].GetProperty("Name").GetString());
        var call = root.GetProperty("Call");
        Assert.Equal("Component", call.GetProperty("Type").GetString());
        Assert.Equal(3912, call.GetProperty("Arguments").GetProperty("Slot").GetProperty("X").GetInt32());
        Assert.False(call.GetProperty("Arguments").TryGetProperty("Hold", out _)); // it has a default
        Assert.False(root.TryGetProperty("AsCondition", out _));
        Assert.Equal("D4", root.GetProperty("UsedIn")[0].GetProperty("Name").GetString());
        Assert.Equal("Strip two", root.GetProperty("CalledBy")[0].GetProperty("Name").GetString());

        using var rare = JsonDocument.Parse((await Tool("get_component", new { id = _rare.Id.ToString() })).Text);
        Assert.Equal("Component", rare.RootElement.GetProperty("AsCondition").GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task A_saved_component_s_bound_fields_hold_its_examples()
    {
        var component = Comp("Press for", new[] { HoldParam(@default: Number(80)) }, Bound(Press(0x41, 5), ("HoldMs", "Hold")));
        component.Concept = "press_for";
        var saved = await Tool("save_component", new { collection = new { id = _host.Macros.Library.Collections[0].Id.ToString() }, component = Json(component) });

        Assert.False(saved.IsError, saved.Text);
        Assert.Contains("A macro calls it with the row {\"Type\":\"Component\"", saved.Text);
        var stored = _host.Macros.Library.AllComponents.Single(c => c.Concept == "press_for");
        Assert.Equal(30, stored.Actions[0].HoldMs); // the Example (30), not the 5 written
    }

    [Fact]
    public async Task Save_component_refuses_a_field_bound_to_a_parameter_it_doesn_t_have()
    {
        var component = Comp("Bad", new[] { SlotParam() }, Bound(Click(), ("Point", "Place")));
        var refused = await Tool("save_component", new { collection = new { id = _host.Macros.Library.Collections[0].Id.ToString() }, component = Json(component) });
        Assert.True(refused.IsError);
        Assert.Contains("no parameter called Place", refused.Text);
        Assert.DoesNotContain(_host.Macros.Library.AllComponents, c => c.Name == "Bad");
    }

    [Fact]
    public async Task A_signature_change_asks_the_values_the_calls_need_then_saves_them_together()
    {
        var macro = Macro("D4", Call(_strip, ("Slot", Point(5, 6))), Call(_strip, ("Slot", Point(7, 8))));
        _host.Macros.Macros.Add(macro);
        var changed = _strip.Clone();
        changed.Parameters.Add(HoldParam("Settle"));

        var asked = await Tool("save_component", new { id = _strip.Id.ToString(), component = Json(changed) });
        Assert.True(asked.IsError);
        Assert.Contains("call_values", asked.Text);
        Assert.Contains("Settle (Number, Ms)", asked.Text);
        Assert.Contains("\"D4\" (macro, 2)", asked.Text);
        Assert.Contains("\"Strip two\" (component, 2)", asked.Text);
        Assert.Equal(2, _host.Macros.Library.Find(_strip.Id)!.Value.Component.Parameters.Count); // nothing saved

        var saved = await Tool("save_component", new { id = _strip.Id.ToString(), component = Json(changed), call_values = new { Settle = 120 } });
        Assert.False(saved.IsError, saved.Text);
        Assert.Contains("Updated 4 call(s)", saved.Text);
        Assert.All(SignatureUpdate.CallsTo(_host.Macros.Find(macro.Id)!.Actions, _strip.Id), a => Assert.Equal(120, (int)a!["Settle"]!));
        Assert.All(SignatureUpdate.CallsTo(_host.Macros.Library.Find(_all.Id)!.Value.Component.Actions, _strip.Id),
            a => Assert.Equal(120, (int)a!["Settle"]!));
    }

    [Fact]
    public async Task Renamed_parameters_keep_the_calls_values_and_values_outside_their_limits_are_refused()
    {
        var macro = Macro("D4", Call(_strip, ("Slot", Point(5, 6))));
        _host.Macros.Macros.Add(macro);
        var renamed = _strip.Clone();
        renamed.Parameters[0].Name = "Place";
        renamed.Actions[0].Bind!["Point"] = "Place";

        var saved = await Tool("save_component", new { id = _strip.Id.ToString(), component = Json(renamed), renamed = new { Slot = "Place" } });
        Assert.False(saved.IsError, saved.Text);
        Assert.True(JsonNode.DeepEquals(Point(5, 6), _host.Macros.Find(macro.Id)!.Actions[0].Arguments!["Place"]));

        var required = _host.Macros.Library.Find(_strip.Id)!.Value.Component.Clone();
        required.Parameters[1].Default = null; // Hold: every call now gives one
        var outside = await Tool("save_component", new { id = _strip.Id.ToString(), component = Json(required), call_values = new { Hold = 0 } });
        Assert.True(outside.IsError);
        Assert.Contains("call_values.Hold", outside.Text);
    }

    [Fact]
    public async Task Get_schema_gives_a_component_s_format()
    {
        var schema = await Tool("get_schema", new { kind = "component" });
        Assert.False(schema.IsError, schema.Text);
        Assert.Contains("\"Parameters\"", schema.Text);
        Assert.Contains("\"Returns\"", schema.Text);
        Assert.Contains("Bind", schema.Text);
    }

    [Fact]
    public async Task The_outline_shows_each_call_s_values_and_the_list_names_components_called_as_conditions()
    {
        var macro = Macro("D4", Call(_strip, ("Slot", Point(5, 6))), If(CallCondition(_rare, "Rare", ("Slot", Point(5, 6))), Press()));
        _host.Macros.Macros.Add(macro);

        var outline = await Tool("get_item", new { id = macro.Id.ToString(), outline = true });
        Assert.Contains("/Actions/0 Component game-diablo_iv-act-strip_one_slot {\"Slot\":{\"X\":5,\"Y\":6}}", outline.Text);
        Assert.Contains("rare_item_in_slot", outline.Text);
        Assert.Contains("Component \"Rare\"", outline.Text);
    }

    [Fact]
    public async Task Check_condition_of_a_Component_condition_checks_the_condition_it_returns()
    {
        _settings.AiSeeScreen = true;
        var result = await Tool("check_condition", With(new
        {
            condition = new { Kind = "Component", Name = "Rare", ComponentId = _rare.Id.ToString(), Arguments = new { Slot = new { X = 7, Y = 8 } } },
        }));

        Assert.False(result.IsError, result.Text);
        var checkedCondition = Assert.Single(_host.Checked);
        Assert.Equal(ConditionKind.Pixel, checkedCondition.Kind);
        Assert.Equal((7, 8), (checkedCondition.X, checkedCondition.Y));
    }

    [Fact]
    public void The_texts_for_AI_apps_explain_functions_and_reuse()
    {
        Assert.Contains("A component is a function", AiTools.Instructions);
        Assert.Contains("one concept", AiTools.Instructions);
        var tools = _tools.All.ToDictionary(t => t.Name);
        Assert.Contains("call_values", tools[AiTools.Name("save_component")].Description);
        Assert.Contains("A component is a function", tools[AiTools.Name("save_component")].Description);
        Assert.Contains("Call", tools[AiTools.Name("get_component")].Description);
        Assert.Contains("\"component\"", tools[AiTools.Name("get_schema")].InputSchema);
    }
}
