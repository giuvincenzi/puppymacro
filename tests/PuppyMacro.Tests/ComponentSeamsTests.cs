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
/// Where two parts of components as functions meet: Save as component and then Unlink give back the actions selected, for
/// every mix of what they take from outside, in a macro and in a component; the call get_component gives to copy is
/// accepted as it is, and save_component keeps a component as the AI app wrote it, for components made at random.
/// </summary>
[Collection(nameof(DebugStore))]
public class ComponentSeamsTests : IDisposable
{
    private const string App = "Claude (claude-code)";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>The actions as JSON with their conditions' ids numbered by where they are, so two copies made with new ids compare.</summary>
    private static List<string> Normalized(IEnumerable<MacroAction> actions)
    {
        var list = actions.ToList();
        var ids = MacroDefinition.Walk(list).SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).Select(c => c.Id).ToList();
        return list.Select(a =>
        {
            string json = MacroJson.Serialize(a);
            for (int i = 0; i < ids.Count; i++)
                json = json.Replace(ids[i].ToString(), $"condition-{i}");
            return json;
        }).ToList();
    }

    private static readonly Guid Seen = Guid.Parse("5ee05ee0-0000-4000-8000-000000000001");

    /// <summary>The kinds of actions a selection mixes: what each takes from outside it.</summary>
    private static readonly Func<LibraryComponent, MacroAction>[] Kinds =
    {
        _ => new MacroAction { Type = MacroActionType.Click, Vk = 0x01, X = 40, Y = 50, FoundId = Seen, FoundOffsetX = 4, FoundOffsetY = 5 },
        _ => Press(0x87, 15),
        _ =>
        {
            var inner = ImageFinder("Inner");
            return If(inner, Click(found: inner.Id));
        },
        strip => Call(strip, ("Slot", Found(Seen))),
        _ => Bound(Press(0x87, 30), ("HoldMs", "Hold")), // in a component only
        strip => Call(strip, ("Slot", From("Where"))), // in a component only
    };

    public static TheoryData<int, bool> Selections()
    {
        var data = new TheoryData<int, bool>();
        for (int mask = 1; mask < 1 << 4; mask++)
            data.Add(mask, false);
        for (int mask = 1; mask < 1 << Kinds.Length; mask++)
            data.Add(mask, true);
        return data;
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public void Save_as_component_then_Unlink_gives_back_the_actions_selected(int mask, bool inComponent)
    {
        var strip = Comp("Strip", new[] { SlotParam() }, Bound(Click(3912, 1056), ("Point", "Slot")));
        var selected = Kinds.Where((_, i) => (mask & (1 << i)) != 0).Select(make => make(strip)).ToList();
        var outer = inComponent ? new List<ComponentParameter> { SlotParam("Where"), HoldParam("Hold") } : null;

        var (actions, parameters, arguments) = ComponentEdits.Lift(selected, outer, new Dictionary<Guid, string> { [Seen] = "Seen" });
        var lifted = Comp("Lifted", parameters, actions.ToArray());
        ComponentEdits.WriteExamples(lifted);
        var row = new MacroAction { Type = MacroActionType.Component, ComponentId = lifted.Id, Arguments = arguments };
        var seen = ImageFinder("Seen");
        seen.Id = Seen;

        Assert.Empty(ComponentProblems(lifted, strip));
        if (inComponent)
            Assert.Empty(Problems(new[] { strip, lifted, Comp("Around", outer!, If(seen, row)) }));
        else
            Assert.Empty(Problems(new[] { strip, lifted }, Macro("D4", If(seen, row))));

        var unlinked = ComponentEdits.Unlink(row, lifted, out string? problem);
        Assert.Null(problem);
        Assert.Equal(Normalized(selected), Normalized(unlinked!));
    }

    private (AiTools Tools, FakeAiHost Host, LibraryCollection Collection) Ai()
    {
        AiTools.WaitStep = TimeSpan.FromSeconds(5);
        DebugStore.Root = _folder.File("debug");
        var settings = AppSettings.CreateDefault();
        settings.AllowAiAccess = true;
        settings.AiAskBeforeChanging = false;
        var host = new FakeAiHost(settings, new MacroLibrary(_folder.File("data/macros")));
        var collection = new LibraryCollection { Name = "Generated", Domain = LibraryDomain.Game, AppExe = "Game.exe" };
        host.Macros.Library.Collections.Add(collection);
        return (new AiTools(host, new AiActivityLog(_folder.File("ai-activity.json"))), host, collection);
    }

    private static Task<AiToolResult> Tool(AiTools tools, string tool, object args) =>
        tools.Call(AiTools.Name(tool), JsonSerializer.SerializeToElement(args), App, TestContext.Current.CancellationToken);

    private static LibraryComponent Generated(int seed)
    {
        var component = new ComponentGenerator(seed).Component();
        component.Name = $"Generated {seed}";
        component.Concept = $"generated_{seed}";
        return component;
    }

    [Fact]
    public async Task The_call_get_component_gives_to_copy_is_accepted_as_it_is()
    {
        var (tools, host, collection) = Ai();
        var components = Enumerable.Range(9000, 40).Select(Generated).ToList();
        collection.Components.AddRange(components);
        var failures = new List<string>();
        foreach (var component in components)
        {
            var result = await Tool(tools, "get_component", new { id = component.Id.ToString() });
            using var answer = JsonDocument.Parse(result.Text);
            var call = JsonSerializer.Deserialize<MacroAction>(answer.RootElement.GetProperty("Call").GetRawText(), CodeJson.Options)!;
            if (Problems(host.Macros.Library.AllComponents, Macro("Copied", call)) is { Count: > 0 } problems)
                failures.Add($"{component.Name}: {answer.RootElement.GetProperty("Call").GetRawText()} → {string.Join("; ", problems)}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Save_component_keeps_a_component_as_the_AI_app_wrote_it()
    {
        var (tools, host, collection) = Ai();
        var failures = new List<string>();
        // Without images: save_component checks that PuppyMacro has them, and the generator's are not in the store.
        var components = Enumerable.Range(9100, 200).Select(Generated)
            .Where(c => c.Parameters.All(p => p.Type != ParameterType.Image)
                        && MacroDefinition.Walk(c.Actions).All(a => a.If?.Conditions.All(x => x.Kind != ConditionKind.Image) ?? true))
            .Take(40)
            .ToList();
        Assert.Equal(40, components.Count);
        foreach (var component in components)
        {
            var saved = await Tool(tools, "save_component", new
            {
                collection = new { id = collection.Id.ToString() },
                component = JsonNode.Parse(CodeJson.Serialize(component)),
            });
            if (saved.IsError)
            {
                failures.Add($"{component.Name}: refused: {saved.Text}");
                continue;
            }
            var stored = host.Macros.Library.AllComponents.Single(c => c.Concept == component.Concept);
            if (CodeJson.Serialize(stored.Parameters) != CodeJson.Serialize(component.Parameters)
                || !Normalized(stored.Actions).SequenceEqual(Normalized(component.Actions)))
                failures.Add($"{component.Name}: stored as\n{CodeJson.Serialize(stored)}\nwritten as\n{CodeJson.Serialize(component)}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
