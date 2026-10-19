using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The macro editor's operations, chained at random on trees of Ifs, groups and clicks that go where their If found
/// something: whatever the editor does, the macro stays one the Code view accepts (groups together in one list, condition
/// names and ids once), and no action is lost, doubled or shared.
/// </summary>
public class MacroEditListInvariantsTests
{
    private static MacroAction Key(int n) => new() { Type = MacroActionType.PressKey, Vk = 0x41, Name = "k" + n };

    private static MacroAction IfWith(Random r, ref int n, int depth)
    {
        var condition = new MacroCondition
        {
            Kind = ConditionKind.Text, Name = "T" + n, Text = "x", Area = SearchArea.Screen, AreaWidth = 100, AreaHeight = 100,
        };
        var a = new MacroAction { Type = MacroActionType.If, Name = "if" + n++, If = new MacroIf { Conditions = { condition } } };
        int count = r.Next(0, 4);
        for (int i = 0; i < count; i++)
            a.If.Then.Add(Make(r, ref n, depth + 1, condition.Id));
        count = r.Next(0, 3);
        for (int i = 0; i < count; i++)
            a.If.Else.Add(Make(r, ref n, depth + 1, null));
        return a;
    }

    private static MacroAction Make(Random r, ref int n, int depth, Guid? found)
    {
        if (depth < 3 && r.Next(4) == 0)
            return IfWith(r, ref n, depth);
        if (found != null && r.Next(3) == 0)
            return new MacroAction { Type = MacroActionType.Click, Vk = 1, Name = "c" + n++, FoundId = found };
        return Key(n++);
    }

    /// <summary>
    /// The Code view's problems, but those a move may rightly leave for the user (a click taken out of its If's Then, which
    /// the editor then names before Save) and an emptied macro.
    /// </summary>
    private static List<string> Problems(MacroEditList list)
    {
        var macro = new MacroDefinition { Name = "M", Actions = list.Actions, Groups = list.Groups };
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out List<CodeProblem> problems);
        return problems.Select(p => p.Message)
            .Where(m => !m.Contains(Strings.Code_FoundOutside) && !m.Contains(Strings.Code_AddAction)).ToList();
    }

    [Fact]
    public void Random_edits_keep_a_macro_the_code_view_accepts()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            int n = 0;
            var actions = new List<MacroAction>();
            for (int i = 0; i < 8; i++)
                actions.Add(Make(r, ref n, 0, null));
            var list = new MacroEditList(actions, Array.Empty<MacroGroup>());
            var log = new List<string>();
            for (int step = 0; step < 30; step++)
            {
                var all = list.All.ToList();
                if (all.Count == 0)
                    break;
                var pick = Enumerable.Range(0, r.Next(1, 3)).Select(_ => all[r.Next(all.Count)]).Distinct().ToList();
                var groups = list.Groups.Count > 0 && r.Next(4) == 0
                    ? new List<MacroGroup> { list.Groups[r.Next(list.Groups.Count)] }
                    : new List<MacroGroup>();
                var lists = MacroDefinition.Lists(list.Actions).ToList();
                var target = lists[r.Next(lists.Count)];
                int op = r.Next(7);
                log.Add($"op{op} {string.Join(",", pick.Select(p => p.Name))} groups={groups.Count}");
                int before = list.All.Count();
                bool keepsCount = true;
                switch (op)
                {
                    case 0:
                        list.Group(pick, groups, "G" + step);
                        break;
                    case 1:
                        list.Move(pick, groups, target, r.Next(target.Count + 1), null);
                        break;
                    case 2:
                        list.MoveUp(pick, groups);
                        break;
                    case 3:
                        list.MoveDown(pick, groups);
                        break;
                    case 4:
                    {
                        var block = list.Copy(pick, groups);
                        var into = target.Count > 0 && r.Next(3) == 0 ? list.GroupOf(target[r.Next(target.Count)]) : null;
                        if (into != null)
                            list.Insert(block, list.ListOf(into), list.LastIndex(into) + 1, into);
                        else
                            list.Insert(block, target, r.Next(target.Count + 1), null);
                        keepsCount = false;
                        break;
                    }
                    case 5:
                        if (list.Groups.Count > 0)
                            list.Ungroup(list.Groups[r.Next(list.Groups.Count)]);
                        break;
                    default:
                        if (r.Next(3) == 0)
                        {
                            list.Remove(pick, groups);
                            keepsCount = false;
                        }
                        break;
                }
                string where = $"seed {seed}, after {string.Join("; ", log)}";
                var objects = list.All.ToList();
                if (keepsCount)
                    Assert.True(before == objects.Count, $"{before} actions became {objects.Count}: {where}");
                Assert.True(objects.Count == objects.Distinct().Count(), $"an action is in two places: {where}");
                var problems = Problems(list);
                Assert.True(problems.Count == 0, $"{string.Join(" | ", problems)}: {where}");
            }
        }
    }
}
