using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// What must hold for every component, checked on hundreds made at random from fixed seeds (<see cref="ComponentGenerator"/>):
/// a component whose Examples keep its rules passes its own check; a call the checks accept plays fields a macro's checks
/// accept, and a call they refuse has a value some field refuses; Unlink gives the rows what the call plays; the JSON (also the
/// compact one AI apps read) and the fingerprint survive a round trip and follow only the signature and the actions; limits rise through any chain of
/// {"From"}; the check and the play agree on what a chain of Component conditions checks; a guided update leaves every call
/// working with the values it had; any text gives a valid parameter name. Each failure names its seed.
/// </summary>
public class ComponentInvariantsTests
{
    private static readonly Dictionary<Guid, Point> Nothing = new();

    private static MacroAction CallWith(LibraryComponent component, Dictionary<string, JsonNode?> arguments) => new()
    {
        Type = MacroActionType.Component,
        ComponentId = component.Id,
        Arguments = arguments.Count == 0 ? null : arguments,
    };

    private static string Describe(Dictionary<string, JsonNode?> arguments) =>
        string.Join(", ", arguments.Select(a => $"{a.Key} = {a.Value?.ToJsonString() ?? "null"}"));

    private static void NoFailures(List<string> failures, int cases) =>
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases fail:\n" + string.Join("\n", failures.Take(12)));

    [Fact]
    public void A_component_whose_examples_keep_its_rules_passes_its_own_check()
    {
        var failures = new List<string>();
        for (int seed = 0; seed < 400; seed++)
        {
            var component = new ComponentGenerator(seed).Component();
            var problems = ComponentProblems(component);
            if (problems.Count > 0)
                failures.Add($"seed {seed}: {string.Join("; ", problems)}\n{CodeJson.Serialize(component)}");
        }
        NoFailures(failures, 400);
    }

    [Fact]
    public void Every_call_the_checks_accept_plays_fields_a_macro_s_checks_accept()
    {
        var failures = new List<string>();
        int accepted = 0;
        for (int seed = 0; seed < 500; seed++)
        {
            var generator = new ComponentGenerator(seed);
            var component = generator.Component();
            var arguments = generator.Arguments(component, new ComponentSignatures(new[] { component }));
            if (Problems(new[] { component }, Macro("Caller", CallWith(component, arguments))).Count > 0)
                continue;
            accepted++;
            var prepared = ComponentPrograms.Prepare(new[] { component }).Find(component.Id)!;
            var frame = ComponentPrograms.Evaluate(prepared, arguments, FrameArguments.None, Nothing, out string? missing);
            if (frame == null)
            {
                failures.Add($"seed {seed}: accepted, but {missing} has no value at play");
                continue;
            }
            var refused = MacroProblems(Macro("Played", ComponentGenerator.Played(prepared.Actions, frame).ToArray()));
            if (refused.Count > 0)
                failures.Add($"seed {seed}: {Describe(arguments)} is accepted by the call, but the fields it fills are refused: {string.Join("; ", refused)}");
        }
        Assert.True(accepted > 150, $"only {accepted} calls accepted: the generator must make more valid calls");
        NoFailures(failures, accepted);
    }

    [Fact]
    public void Every_call_the_checks_refuse_has_a_value_some_field_refuses()
    {
        var failures = new List<string>();
        int refused = 0;
        for (int seed = 1000; seed < 1500; seed++)
        {
            var generator = new ComponentGenerator(seed);
            var component = generator.Component();
            var arguments = generator.Arguments(component, new ComponentSignatures(new[] { component }));
            var problems = Problems(new[] { component }, Macro("Caller", CallWith(component, arguments)));
            if (problems.Count == 0)
                continue;
            refused++;
            // A value that isn't of its type at all is never written into a field, and a whole-number field (Repeat,
            // Tolerance…) can't hold a fraction (it would be cut): nothing to compare.
            if (component.Parameters.Any(p => arguments.TryGetValue(p.Name, out var value)
                    && (ComponentValues.Read(value, p.Type, ValueRule.Any, out _) == null
                        || (p.Unit is NumberUnit.Count or NumberUnit.Percent && ComponentValues.Numeric(value, out double n) && n != Math.Floor(n)))))
                continue;
            var prepared = ComponentPrograms.Prepare(new[] { component }).Find(component.Id)!;
            var frame = ComponentPrograms.Evaluate(prepared, arguments, FrameArguments.None, Nothing, out _)!;
            if (MacroProblems(Macro("Played", ComponentGenerator.Played(prepared.Actions, frame).ToArray())).Count == 0)
                failures.Add($"seed {seed}: {Describe(arguments)} is refused ({string.Join("; ", problems)}), but every field takes it");
        }
        Assert.True(refused > 150, $"only {refused} calls refused: the generator must make more edge values");
        NoFailures(failures, refused);
    }

    [Fact]
    public void Unlink_gives_the_rows_exactly_what_the_call_plays()
    {
        var failures = new List<string>();
        int cases = 0;
        for (int seed = 2000; seed < 2400; seed++)
        {
            var generator = new ComponentGenerator(seed);
            var component = generator.Component();
            var arguments = generator.Arguments(component, new ComponentSignatures(new[] { component }));
            var row = CallWith(component, arguments);
            if (Problems(new[] { component }, Macro("Caller", row)).Count > 0)
                continue;
            cases++;
            var prepared = ComponentPrograms.Prepare(new[] { component }).Find(component.Id)!;
            var frame = ComponentPrograms.Evaluate(prepared, arguments, FrameArguments.None, Nothing, out _)!;
            var unlinked = ComponentEdits.Unlink(row, component, out string? problem);
            if (unlinked == null)
            {
                failures.Add($"seed {seed}: Unlink refused ({problem})");
                continue;
            }
            var played = ComponentGenerator.Comparable(ComponentGenerator.Played(prepared.Actions, frame));
            var rows = ComponentGenerator.Comparable(unlinked);
            for (int i = 0; i < played.Count; i++)
                if (played[i] != rows[i])
                    failures.Add($"seed {seed}, action {i}: the call plays\n{played[i]}\nbut Unlink writes\n{rows[i]}");
            if (unlinked.Any(a => MacroDefinition.Walk(new List<MacroAction> { a }).Any(x => x.Bind != null)))
                failures.Add($"seed {seed}: Unlink in a macro left a Bind");
        }
        Assert.True(cases > 150, $"only {cases} cases");
        NoFailures(failures, cases);
    }

    [Fact]
    public void A_component_read_back_from_its_JSON_passes_and_keeps_its_fingerprint()
    {
        var failures = new List<string>();
        for (int seed = 3000; seed < 3300; seed++)
        {
            var component = new ComponentGenerator(seed).Component();
            var read = MacroJson.ParseComponent(CodeJson.Serialize(component), component.Id, out var problems, null, Array.Empty<LibraryComponent>());
            if (read == null)
            {
                failures.Add($"seed {seed}: not read back: {string.Join("; ", problems.Select(p => p.Message))}");
                continue;
            }
            string before = ComponentLibrary.Fingerprint(new LibraryCollection { Components = { component } });
            string after = ComponentLibrary.Fingerprint(new LibraryCollection { Components = { read } });
            if (before != after)
                failures.Add($"seed {seed}: the fingerprint changed after a round trip");
            if (CodeJson.Serialize(read) != CodeJson.Serialize(component))
                failures.Add($"seed {seed}: the JSON changed after a round trip");
            // The compact form AI apps read (get_component), written back as it is (save_component).
            var compact = MacroJson.ParseComponent(AiJson.Compact(component), component.Id, out problems, null, Array.Empty<LibraryComponent>());
            if (compact == null || CodeJson.Serialize(compact) != CodeJson.Serialize(component))
                failures.Add($"seed {seed}: the compact JSON read back is another component: {string.Join("; ", problems.Select(p => p.Message))}");
        }
        NoFailures(failures, 300);
    }

    [Fact]
    public void The_fingerprint_follows_the_signature_and_never_the_names_or_descriptions()
    {
        var failures = new List<string>();
        for (int seed = 4000; seed < 4300; seed++)
        {
            var generator = new ComponentGenerator(seed);
            var random = generator.Random;
            var component = generator.Component();
            string Print(LibraryComponent c) => ComponentLibrary.Fingerprint(new LibraryCollection { Components = { c } });
            string original = Print(component);

            var described = component.Clone();
            described.Name += " renamed";
            described.Description = "Another description";
            described.Parameters[0].Description = "What it is for";
            if (Print(described) != original)
                failures.Add($"seed {seed}: a name or a description changed the fingerprint");

            var changed = component.Clone();
            var parameter = changed.Parameters[random.Next(changed.Parameters.Count)];
            string what;
            switch (random.Next(5))
            {
                case 0:
                    what = "a parameter renamed";
                    ComponentEdits.Rename(changed.Actions, parameter.Name, parameter.Name + "x");
                    parameter.Name += "x";
                    break;
                case 1:
                    what = "a default added or taken away";
                    parameter.Default = parameter.Default == null ? parameter.Example!.DeepClone() : null;
                    break;
                case 2:
                    what = "another example";
                    parameter.Example = parameter.Type switch
                    {
                        ParameterType.Point => Point(54321, 1),
                        ParameterType.Number => Number(ComponentValues.Numeric(parameter.Example, out double n) && n == 77 ? 78 : 77),
                        ParameterType.Text => Text("another text"),
                        ParameterType.Key => Key(0x86),
                        ParameterType.Color => Text("#fedcba"),
                        ParameterType.Image => Images(ImageB, ImageA, ImageB),
                        _ => Area(1, 2, 333, 444),
                    };
                    break;
                case 3:
                    what = "another unit or type";
                    if (parameter.Type == ParameterType.Number)
                        parameter.Unit = parameter.Unit == NumberUnit.Ms ? NumberUnit.Seconds : NumberUnit.Ms;
                    else
                        parameter.Type = parameter.Type == ParameterType.Text ? ParameterType.Color : ParameterType.Text;
                    break;
                default:
                    what = "a parameter added";
                    changed.Parameters.Add(Param("Added", ParameterType.Text, Text("x")));
                    break;
            }
            if (Print(changed) == original)
                failures.Add($"seed {seed}: {what} kept the fingerprint");
        }
        NoFailures(failures, 300);
    }

    private static readonly (string Slot, (double Min, double Max) Range)[] MsFields =
    {
        ("DelayMs", (0, MacroJson.MaxDelayMs)),
        ("HoldMs", MacroJson.HoldMs),
        ("RepeatPauseMs", MacroJson.RepeatPauseMs),
    };

    [Fact]
    public void Limits_rise_through_any_chain_of_From_to_the_tightest_field()
    {
        var failures = new List<string>();
        for (int seed = 5000; seed < 5300; seed++)
        {
            var random = new Random(seed);
            int depth = random.Next(1, ComponentSignatures.MaxDepth + 1);
            var range = (Min: 0.0, Max: MacroJson.MaxDelayMs);
            var chain = new List<LibraryComponent>();
            LibraryComponent? below = null;
            for (int level = depth; level >= 1; level--)
            {
                var actions = new List<MacroAction>();
                for (int k = random.Next(0, 3); k > 0; k--)
                {
                    var (slot, limits) = MsFields[random.Next(MsFields.Length)];
                    actions.Add(Bound(Press(), (slot, "V")));
                    range = (Math.Max(range.Min, limits.Min), Math.Min(range.Max, limits.Max));
                }
                if (below != null)
                    actions.Add(Call(below, ("V", From("V"))));
                if (actions.Count == 0)
                    actions.Add(Press());
                var component = Comp($"Level {level}", new[] { Param("V", ParameterType.Number, Number(range.Min), unit: NumberUnit.Ms) }, actions.ToArray());
                ComponentEdits.WriteExamples(component);
                chain.Add(component);
                below = component;
            }
            var top = below!;
            var signatures = new ComponentSignatures(chain);
            var rule = signatures.RuleOf(top.Id, "V");
            if (rule.Range != range)
                failures.Add($"seed {seed}, {depth} levels: the top's rule is {rule.Range}, the tightest field {range}");
            if (signatures.Height(top.Id) != depth)
                failures.Add($"seed {seed}: height {signatures.Height(top.Id)} for {depth} levels");
            foreach (var component in chain)
                if (ComponentProblems(component, chain.ToArray()) is { Count: > 0 } own)
                    failures.Add($"seed {seed}: {component.Name} fails its own check: {string.Join("; ", own)}");
            foreach (double value in new[] { range.Min, range.Max })
                if (Problems(chain, Macro("Caller", Call(top, ("V", Number(value))))) is { Count: > 0 } refused)
                    failures.Add($"seed {seed}: {value} at a limit is refused: {string.Join("; ", refused)}");
            foreach (double value in new[] { range.Min - 0.5, range.Max + 1 })
                if (Problems(chain, Macro("Caller", Call(top, ("V", Number(value))))).Count == 0)
                    failures.Add($"seed {seed}: {value} past a limit is accepted");
        }
        NoFailures(failures, 300);
    }

    [Fact]
    public void The_check_and_the_play_agree_on_what_a_chain_of_Component_conditions_checks()
    {
        var failures = new List<string>();
        for (int seed = 6000; seed < 6300; seed++)
        {
            var random = new Random(seed);
            int depth = random.Next(1, ComponentSignatures.MaxDepth + 1);
            bool not = false;
            var pixel = Bound(Pixel(1, 1, "#000000", not: random.Next(2) == 0), ("Point", "Slot"));
            not ^= pixel.Not;
            var bottom = Comp("Level 1", new[] { SlotParam() }, If(pixel, Press()));
            bottom.Returns = new ComponentReturns { Found = pixel.Id };
            var chain = new List<LibraryComponent> { bottom };
            for (int level = 2; level <= depth; level++)
            {
                var condition = CallCondition(chain[^1], $"Level {level - 1}", ("Slot", From("Slot")));
                condition.Not = random.Next(2) == 0;
                not ^= condition.Not;
                var component = Comp($"Level {level}", new[] { SlotParam() }, If(condition, Press()));
                component.Returns = new ComponentReturns { Found = condition.Id };
                chain.Add(component);
            }
            var outer = CallCondition(chain[^1], "Outer", ("Slot", Point(7, 8)));
            outer.Not = random.Next(2) == 0;
            not ^= outer.Not;

            if (Problems(chain, Macro("Caller", If(outer, Press()))) is { Count: > 0 } refused)
                failures.Add($"seed {seed}, {depth} levels: refused: {string.Join("; ", refused)}");
            var checkedAs = ComponentChecks.ReturnedCondition(new ComponentSignatures(chain), chain[^1].Id, outer.Not);
            var played = ComponentPrograms.Prepare(chain).Effective(outer, FrameArguments.None, Nothing);
            if (checkedAs == null || played == null)
            {
                failures.Add($"seed {seed}, {depth} levels: the check sees {checkedAs?.Kind.ToString() ?? "nothing"}, the play {played?.Kind.ToString() ?? "nothing"}");
                continue;
            }
            if (checkedAs.Kind != ConditionKind.Pixel || played.Kind != ConditionKind.Pixel || checkedAs.Not != not || played.Not != not)
                failures.Add($"seed {seed}, {depth} levels: Not should be {not}; the check says {checkedAs.Not}, the play {played.Not}");
            if ((played.X, played.Y) != (7, 8) || played.Id != outer.Id || played.Name != "Outer" || played.Bind != null)
                failures.Add($"seed {seed}: the play checks {played.X},{played.Y} as {played.Name}");
        }
        NoFailures(failures, 300);
    }

    [Fact]
    public void A_guided_update_leaves_every_call_working_with_the_values_it_had()
    {
        var failures = new List<string>();
        int cases = 0;
        for (int seed = 7000; seed < 7400; seed++)
        {
            var generator = new ComponentGenerator(seed);
            var random = generator.Random;
            var before = generator.Component();
            var signatures = new ComponentSignatures(new[] { before });
            var arguments = before.Parameters.ToDictionary(p => p.Name, p => (JsonNode?)generator.Valid(p.Type, signatures.RuleOf(before.Id, p.Name)));
            var macro = Macro("Caller", CallWith(before, arguments));
            if (Problems(new[] { before }, macro).Count > 0)
                continue;

            var after = before.Clone();
            var renamed = new Dictionary<string, string>();
            var parameter = after.Parameters[random.Next(after.Parameters.Count)];
            string what;
            int kind = random.Next(after.Parameters.Count >= 2 ? 5 : 4);
            switch (kind)
            {
                case 0:
                    what = $"{parameter.Name} removed";
                    ComponentEdits.Forget(after.Actions, parameter);
                    after.Parameters.Remove(parameter);
                    break;
                case 1:
                    what = $"{parameter.Name} renamed";
                    renamed[parameter.Name] = "Renamed" + seed;
                    ComponentEdits.Rename(after.Actions, parameter.Name, "Renamed" + seed);
                    parameter.Name = "Renamed" + seed;
                    break;
                case 2:
                {
                    what = "a required parameter added";
                    var field = ComponentGenerator.Fields[random.Next(ComponentGenerator.Fields.Count)];
                    after.Parameters.Add(new ComponentParameter
                    {
                        Name = "Added", Type = field.Type, Unit = field.Unit, Example = generator.Valid(field.Type, ComponentValues.BaseRule(field.Type, field.Unit)),
                    });
                    break;
                }
                case 3:
                {
                    var field = ComponentGenerator.Fields.First(f => f.Type != parameter.Type || f.Unit != parameter.Unit);
                    what = $"{parameter.Name} of another type";
                    ComponentEdits.Forget(after.Actions, parameter);
                    (parameter.Type, parameter.Unit, parameter.Default) = (field.Type, field.Unit, null);
                    parameter.Example = generator.Valid(field.Type, ComponentValues.BaseRule(field.Type, field.Unit));
                    break;
                }
                default:
                {
                    // The second parameter taken out, the first renamed to its name: what the editor allows in one save.
                    var (first, second) = (after.Parameters[0], after.Parameters[1]);
                    what = $"{second.Name} removed and {first.Name} renamed {second.Name}";
                    ComponentEdits.Forget(after.Actions, second);
                    after.Parameters.Remove(second);
                    renamed[first.Name] = second.Name;
                    ComponentEdits.Rename(after.Actions, first.Name, second.Name);
                    first.Name = second.Name;
                    break;
                }
            }
            if (ComponentProblems(after) is { Count: > 0 } own)
            {
                failures.Add($"seed {seed} ({what}): the new version fails its own check: {string.Join("; ", own)}");
                continue;
            }
            cases++;
            var update = SignatureUpdate.Compare(before, after, new[] { macro }, new[] { after }, renamed);
            var values = update.NeedValue.ToDictionary(p => p.Name, p => p.Example!);
            var changed = update.Apply(new[] { macro }, Array.Empty<LibraryComponent>(), values, after).Macros.SingleOrDefault() ?? macro;
            if (Problems(new[] { after }, changed) is { Count: > 0 } broken)
                failures.Add($"seed {seed} ({what}): the call is broken after the update: {string.Join("; ", broken)}");
            var now = changed.Actions[0].Arguments ?? new Dictionary<string, JsonNode?>();
            foreach (var (old, value) in arguments)
            {
                string name = renamed.GetValueOrDefault(old) ?? old;
                var kept = after.Parameters.FirstOrDefault(p => p.Name == name);
                var was = before.Parameters.First(p => p.Name == old);
                if (kept == null || kept.Type != was.Type || kept.Unit != was.Unit || (!renamed.ContainsKey(old) && renamed.ContainsValue(old)))
                    continue;
                if (!JsonNode.DeepEquals(value, now.GetValueOrDefault(name)))
                    failures.Add($"seed {seed} ({what}): {name} should keep {value?.ToJsonString()} (the call's {old}), it has {now.GetValueOrDefault(name)?.ToJsonString() ?? "nothing"}");
            }
        }
        Assert.True(cases > 200, $"only {cases} cases");
        var kinds = failures.Select(f => f[(f.IndexOf('(') + 1)..f.IndexOf(')')]).Select(w => w.Contains(" and ") ? "removed and renamed into its name"
            : w.Split(' ', 2)[1]).GroupBy(w => w).Select(g => $"{g.Key}: {g.Count()}");
        Assert.True(failures.Count == 0, $"{failures.Count} failures ({string.Join(", ", kinds)}) in {cases} cases:\n" + string.Join("\n", failures.Take(12)));
    }

    private static readonly Regex ParameterName = new("^[A-Za-z][A-Za-z0-9_]{0,39}$");

    [Fact]
    public void A_name_made_from_any_text_is_a_valid_parameter_name()
    {
        const string letters = "aZ09 _-.,!?éÈüßøñçŁ€日本\t\n'\"#";
        var random = new Random(8000);
        var failures = new List<string>();
        for (int i = 0; i < 500; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 80)).Select(_ => letters[random.Next(letters.Length)]).ToArray());
            string name = ComponentEdits.NameFrom(text, random.Next(2) == 0 ? "Place" : "Value");
            if (!ParameterName.IsMatch(name))
                failures.Add($"\"{text}\" gives \"{name}\"");
        }
        NoFailures(failures, 500);
    }
}
