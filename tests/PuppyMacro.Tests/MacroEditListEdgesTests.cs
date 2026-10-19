using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The macro editor's operations on a tree of Ifs and groups, at their edges.</summary>
public class MacroEditListEdgesTests
{
    private static readonly MacroGroup[] NoGroups = Array.Empty<MacroGroup>();

    private static MacroAction Key(string name, Guid? group = null) => new() { Type = MacroActionType.PressKey, Vk = 0x41, Name = name, GroupId = group };

    private static MacroAction IfWith(string name, IEnumerable<MacroAction> then, IEnumerable<MacroAction>? otherwise = null)
    {
        var condition = new MacroCondition { Kind = ConditionKind.Image, Name = name };
        return new MacroAction
        {
            Type = MacroActionType.If,
            Name = name,
            If = new MacroIf { Conditions = { condition }, Then = then.ToList(), Else = (otherwise ?? Array.Empty<MacroAction>()).ToList() },
        };
    }

    private static List<string> Names(IEnumerable<MacroAction> actions) => actions.Select(a => a.Name).ToList();

    [Fact]
    public void A_whole_group_moves_into_a_then_with_its_actions_and_stays_one_group()
    {
        var group = new MacroGroup { Name = "G" };
        MacroAction a = Key("a", group.Id), b = Key("b", group.Id);
        var check = IfWith("If", new[] { Key("x") });
        var list = new MacroEditList(new[] { a, b, check }, new[] { group });

        Assert.True(list.Move(Array.Empty<MacroAction>(), new[] { group }, check.If!.Then, 1, null));

        Assert.Equal(new[] { "If" }, Names(list.Actions));
        Assert.Equal(new[] { "x", "a", "b" }, Names(check.If.Then));
        Assert.Same(check.If.Then, list.ListOf(group));
        Assert.Equal(new[] { a, b }, list.ActionsOf(group));
    }

    [Fact]
    public void Moving_down_inside_a_then_keeps_every_action_once()
    {
        MacroAction x = Key("x"), y = Key("y"), z = Key("z");
        var check = IfWith("If", new[] { x, y, z });
        var list = new MacroEditList(new[] { Key("before"), check, Key("after") }, NoGroups);

        Assert.True(list.MoveDown(new[] { x }, NoGroups));
        Assert.Equal(new[] { "y", "x", "z" }, Names(check.If!.Then));
        Assert.True(list.MoveDown(new[] { x }, NoGroups));
        Assert.True(list.MoveDown(new[] { x }, NoGroups)); // out of Then, after its If
        Assert.Equal(new[] { "before", "If", "x", "after" }, Names(list.Actions));
        Assert.Equal(new[] { "y", "z" }, Names(check.If.Then));
        Assert.Equal(6, list.All.Count());
    }

    [Fact]
    public void Moving_an_if_into_the_then_of_an_if_inside_it_is_refused()
    {
        var inner = IfWith("Inner", new[] { Key("x") });
        var outer = IfWith("Outer", new[] { inner });
        var list = new MacroEditList(new[] { outer }, NoGroups);

        Assert.False(list.Move(new[] { outer }, NoGroups, inner.If!.Then, 0, null));
        Assert.False(list.Move(new[] { outer }, NoGroups, inner.If.Else, 0, null));
        Assert.Equal(new[] { "Outer", "Inner", "x" }, Names(list.All));
    }

    [Fact]
    public void Pasting_an_if_twice_names_its_conditions_apart_and_its_clicks_follow_each_copy()
    {
        var click = new MacroAction { Type = MacroActionType.Click, Vk = 1, Name = "click" };
        var nested = IfWith("Inner", new[] { click });
        var check = IfWith("Outer", new[] { nested });
        click.FoundId = check.If!.Conditions[0].Id; // the outer If's find, two levels up
        var list = new MacroEditList(new[] { check }, NoGroups);
        var block = list.Copy(new[] { check }, NoGroups);

        var first = list.Insert(block, 1, null).Single();
        var second = list.Insert(block, 2, null).Single();

        var names = list.All.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var copy in new[] { first, second })
        {
            var copiedClick = copy.If!.Then[0].If!.Then[0];
            Assert.Equal(copy.If.Conditions[0].Id, copiedClick.FoundId);
        }
        Assert.Equal(check.If.Conditions[0].Id, click.FoundId);
    }

    [Fact]
    public void Copying_an_if_with_a_group_in_its_then_pastes_a_new_group_there()
    {
        var group = new MacroGroup { Name = "Inside" };
        var check = IfWith("If", new[] { Key("a", group.Id), Key("b", group.Id) });
        var list = new MacroEditList(new[] { check }, new[] { group });

        var pasted = list.Insert(list.Copy(new[] { check }, NoGroups), 1, null).Single();

        Assert.Equal(2, list.Groups.Count);
        var copy = list.Groups.Single(g => g.Id != group.Id);
        Assert.Equal("Inside", copy.Name);
        Assert.Same(pasted.If!.Then, list.ListOf(copy));
        Assert.Same(check.If!.Then, list.ListOf(group));
    }

    [Fact]
    public void Removing_the_only_action_of_a_group_and_putting_others_in_its_place_keeps_the_group()
    {
        // What the macro editor does for Unlink (a component row becomes the component's actions) and Save as component
        // (the selected rows become one row): it takes the row's index and group, removes the row, then inserts the new
        // actions there, in that group (MacroEditorWindow.Unlink, OnSaveAsComponentClick). A group of one linked row.
        var group = new MacroGroup { Name = "Combo" };
        var row = new MacroAction { Type = MacroActionType.Component, ComponentId = Guid.NewGuid(), GroupId = group.Id };
        var list = new MacroEditList(new[] { Key("before"), row, Key("after") }, new[] { group });

        int index = list.Actions.IndexOf(row);
        MacroGroup? into = list.GroupOf(row);
        list.Remove(new[] { row }, NoGroups);
        var block = new ActionBlock();
        block.Actions.AddRange(new[] { Key("one"), Key("two") });
        var inserted = list.Insert(block, list.Actions, Math.Min(index, list.Actions.Count), into);

        Assert.Equal(new[] { "before", "one", "two", "after" }, Names(list.Actions));
        Assert.Contains(group, list.Groups);
        Assert.All(inserted, a => Assert.Equal(group.Id, a.GroupId));
    }

    /// <summary>
    /// The macro has a condition named "Café" written with a combining accent ("Cafe" + U+0301, as text pasted from some
    /// apps is), and an If whose condition is "Café" with the precomposed é is pasted. The two names look the same, and the
    /// macro's checks (MacroJson, the editor's Save) compare names with the culture, which takes them as one name: Paste
    /// must give the copy the name of a copy, or the macro can't be saved ("Two conditions have the same name"). Paste
    /// compares them ordinally (MacroEditList.RenameConditions, ItemNames.Copy), sees two names and keeps the name.
    /// </summary>
    [Fact]
    public void A_pasted_condition_whose_name_the_macro_checks_take_as_taken_gets_the_name_of_a_copy()
    {
        var list = new MacroEditList(new[] { IfWith("Café", new[] { Key("a") }) }, NoGroups);
        var block = new ActionBlock();
        block.Actions.Add(IfWith("Café", new[] { Key("b") }));

        list.Insert(block, 1, null);

        var macro = new MacroDefinition { Name = "M", Actions = list.Actions, Groups = list.Groups };
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out List<CodeProblem> problems);
        Assert.DoesNotContain(problems, p => p.Message.EndsWith(PuppyMacro.Resources.Strings.Code_ConditionNameTwice));
    }
}
