using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class MacroEditListTests
{
    private static readonly MacroGroup[] NoGroups = Array.Empty<MacroGroup>();
    private static readonly MacroAction[] NoActions = Array.Empty<MacroAction>();

    /// <summary>
    /// A list from a short layout: letters are actions (named after the letter), "[G ab]" puts
    /// a and b in group G. Example: "a [G bc] d".
    /// </summary>
    private static MacroEditList Build(string layout)
    {
        var actions = new List<MacroAction>();
        var groups = new List<MacroGroup>();
        MacroGroup? open = null;
        string[] tokens = layout.Replace("[", " [ ").Replace("]", " ] ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token == "[")
            {
                open = new MacroGroup { Name = tokens[++i] };
                groups.Add(open);
            }
            else if (token == "]")
            {
                open = null;
            }
            else
            {
                foreach (char c in token)
                    actions.Add(new MacroAction { Type = MacroActionType.PressKey, Name = c.ToString(), GroupId = open?.Id });
            }
        }
        return new MacroEditList(actions, groups);
    }

    /// <summary>The list in the same layout as <see cref="Build"/>.</summary>
    private static string Layout(MacroEditList list)
    {
        var parts = new List<string>();
        MacroGroup? current = null;
        string pending = "";
        foreach (var action in list.Actions)
        {
            var group = list.GroupOf(action);
            if (group != current)
            {
                if (pending.Length > 0)
                    parts.Add(current == null ? pending : $"[{current.Name} {pending}]");
                pending = "";
                current = group;
            }
            pending += action.Name;
        }
        if (pending.Length > 0)
            parts.Add(current == null ? pending : $"[{current.Name} {pending}]");
        return string.Join(" ", parts);
    }

    private static MacroAction A(MacroEditList list, char name) => list.Actions.Single(a => a.Name == name.ToString());

    private static MacroGroup G(MacroEditList list, string name) => list.Groups.Single(g => g.Name == name);

    [Fact]
    public void Grouping_puts_the_actions_together_where_the_first_one_was()
    {
        var list = Build("a b c d");

        var group = list.Group(new[] { A(list, 'b'), A(list, 'd') }, NoGroups, "G");

        Assert.Equal("a [G bd] c", Layout(list));
        Assert.Equal("G", group!.Name);
    }

    [Fact]
    public void Grouping_actions_taken_from_the_middle_of_a_group_puts_the_new_group_after_it()
    {
        var list = Build("[G abcd] e");

        list.Group(new[] { A(list, 'b'), A(list, 'c') }, NoGroups, "H");

        Assert.Equal("[G ad] [H bc] e", Layout(list));
    }

    [Fact]
    public void A_group_that_loses_all_its_actions_is_removed()
    {
        var list = Build("[G ab] c");

        list.Group(new[] { A(list, 'a'), A(list, 'b'), A(list, 'c') }, NoGroups, "H");

        Assert.Equal("[H abc]", Layout(list));
        Assert.Equal(new[] { "H" }, list.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Ungroup_keeps_the_actions_in_place()
    {
        var list = Build("a [G bc] d");

        list.Ungroup(G(list, "G"));

        Assert.Equal("abcd", Layout(list));
        Assert.Empty(list.Groups);
    }

    [Fact]
    public void Removing_a_group_removes_its_actions()
    {
        var list = Build("a [G bc] d");

        list.Remove(NoActions, new[] { G(list, "G") });

        Assert.Equal("ad", Layout(list));
        Assert.Empty(list.Groups);
    }

    [Fact]
    public void A_copied_group_is_inserted_as_a_new_group_with_the_same_name()
    {
        var list = Build("a [G bc] d");
        var group = G(list, "G");

        var inserted = list.Insert(list.Copy(NoActions, new[] { group }), list.LastIndex(group) + 1, null);

        Assert.Equal("a [G bc] [G bc] d", Layout(list));
        Assert.Equal(2, list.Groups.Count);
        Assert.NotEqual(list.Groups[0].Id, list.Groups[1].Id);
        Assert.All(inserted, a => Assert.Equal(list.Groups[1].Id, a.GroupId));
    }

    [Fact]
    public void Copies_are_independent_of_the_originals()
    {
        var list = Build("a b");

        var inserted = list.Insert(list.Copy(new[] { A(list, 'a') }, NoGroups), 2, null);
        inserted[0].Name = "x";

        Assert.Equal("abx", Layout(list));
    }

    [Fact]
    public void Actions_pasted_inside_a_group_join_it_and_groups_are_not_nested()
    {
        var list = Build("[G ab] [H cd]");
        var block = list.Copy(NoActions, new[] { G(list, "H") });

        list.Insert(block, 1, G(list, "G"));

        Assert.Equal("[G acdb] [H cd]", Layout(list));
        Assert.Equal(2, list.Groups.Count);
    }

    [Fact]
    public void Inserting_outside_groups_never_splits_a_group()
    {
        var list = Build("[G ab] c");

        list.Insert(list.Copy(new[] { A(list, 'c') }, NoGroups), 1, null);

        Assert.Equal("[G ab] cc", Layout(list));
    }

    [Fact]
    public void Dragging_an_action_into_another_group()
    {
        var list = Build("a [G bc] [H de]");

        // Dropped after d, inside H.
        Assert.True(list.Move(new[] { A(list, 'b') }, NoGroups, list.Actions.IndexOf(A(list, 'd')) + 1, G(list, "H")));

        Assert.Equal("a [G c] [H dbe]", Layout(list));
    }

    [Fact]
    public void Dragging_actions_out_of_their_group()
    {
        var list = Build("[G abc] d");

        Assert.True(list.Move(new[] { A(list, 'a'), A(list, 'b') }, NoGroups, list.Actions.Count, null));

        Assert.Equal("[G c] dab", Layout(list));
    }

    [Fact]
    public void Dragging_selected_actions_keeps_their_order_and_puts_them_together()
    {
        var list = Build("a b c d e");

        list.Move(new[] { A(list, 'd'), A(list, 'a') }, NoGroups, list.Actions.IndexOf(A(list, 'c')), null);

        Assert.Equal("badce", Layout(list));
    }

    [Fact]
    public void A_whole_group_moves_with_its_actions_but_not_into_another_group()
    {
        var list = Build("[G ab] c [H de]");

        Assert.False(list.Move(NoActions, new[] { G(list, "G") }, 4, G(list, "H")));
        Assert.True(list.Move(NoActions, new[] { G(list, "G") }, list.Actions.Count, null));

        Assert.Equal("c [H de] [G ab]", Layout(list));
    }

    [Theory]
    [InlineData("a [G bc] d", 'b', "ab [G c] d")] // leaves the group at its top
    [InlineData("a [G bc] d", 'c', "a [G cb] d")] // up inside the group
    [InlineData("a [G bc] d", 'd', "a [G bcd]")]  // enters the group at its end
    [InlineData("a b", 'a', "ab")]                // already first
    public void Alt_Up_moves_one_step_entering_and_leaving_groups(string layout, char moved, string expected)
    {
        var list = Build(layout);

        list.MoveUp(new[] { A(list, moved) }, NoGroups);

        Assert.Equal(expected, Layout(list));
    }

    [Theory]
    [InlineData("a [G bc] d", 'c', "a [G b] cd")]  // leaves the group at its bottom
    [InlineData("a [G bc] d", 'b', "a [G cb] d")]  // down inside the group
    [InlineData("a [G bc] d", 'a', "[G abc] d")]   // enters the group at its start
    public void Alt_Down_moves_one_step_entering_and_leaving_groups(string layout, char moved, string expected)
    {
        var list = Build(layout);

        list.MoveDown(new[] { A(list, moved) }, NoGroups);

        Assert.Equal(expected, Layout(list));
    }

    [Fact]
    public void Alt_Down_jumps_over_a_collapsed_group()
    {
        var list = Build("a [G bc] d");
        G(list, "G").Collapsed = true;

        list.MoveDown(new[] { A(list, 'a') }, NoGroups);

        Assert.Equal("[G bc] ad", Layout(list));
    }

    [Fact]
    public void Alt_Up_moves_a_whole_group_over_another_group()
    {
        var list = Build("[G ab] [H cd]");

        list.MoveUp(NoActions, new[] { G(list, "H") });

        Assert.Equal("[H cd] [G ab]", Layout(list));
    }
}

public class MacroGroupsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void NormalizeGroups_fixes_unknown_split_and_empty_groups()
    {
        var used = new MacroGroup { Name = "Used" };
        var empty = new MacroGroup { Name = "Empty" };
        var actions = new List<MacroAction>
        {
            new() { GroupId = Guid.NewGuid() }, // unknown group
            new() { GroupId = used.Id },
            new() { GroupId = used.Id },
            new(),
            new() { GroupId = used.Id },        // separated from its group
        };
        var groups = new List<MacroGroup> { empty, used };

        MacroDefinition.NormalizeGroups(actions, groups);

        Assert.Equal(new Guid?[] { null, used.Id, used.Id, null, null }, actions.Select(a => a.GroupId));
        Assert.Equal(new[] { used }, groups);
    }

    [Fact]
    public void Clone_copies_names_and_groups()
    {
        var group = new MacroGroup { Name = "Open bench", Collapsed = true };
        var macro = new MacroDefinition
        {
            Actions = { new MacroAction { Name = "Open inventory", GroupId = group.Id } },
            Groups = { group },
        };

        var copy = macro.Clone();
        copy.Groups[0].Name = "Changed";

        Assert.Equal("Open bench", macro.Groups[0].Name);
        Assert.Equal("Open inventory", copy.Actions[0].Name);
        Assert.Equal(group.Id, copy.Actions[0].GroupId);
        Assert.True(copy.Groups[0].Collapsed);
    }

    [Fact]
    public void Names_and_groups_are_saved_and_loaded_back()
    {
        var group = new MacroGroup { Name = "Open bench", Collapsed = true };
        var macro = new MacroDefinition
        {
            Name = "Craft",
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x49, Name = "Open inventory", GroupId = group.Id },
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x0D },
            },
            Groups = { group },
        };
        Assert.True(new MacroLibrary(_folder.Path).TrySave(macro, out _));

        var library = new MacroLibrary(_folder.Path);
        library.Load(Array.Empty<Guid>(), out _);
        var loaded = Assert.Single(library.Macros);

        Assert.Equal("Open inventory", loaded.Actions[0].Name);
        Assert.Equal(group.Id, loaded.Actions[0].GroupId);
        Assert.Null(loaded.Actions[1].GroupId);
        var loadedGroup = Assert.Single(loaded.Groups);
        Assert.Equal("Open bench", loadedGroup.Name);
        Assert.True(loadedGroup.Collapsed);
    }

    [Fact]
    public void Macro_files_from_before_groups_load_without_names_and_groups()
    {
        System.IO.File.WriteAllText(_folder.File($"{Guid.NewGuid()}.json"), """
            {
              "Name": "Old",
              "Actions": [ { "Type": "PressKey", "Vk": 65 }, { "Type": "Click", "Vk": 1, "DelayMs": 100 } ],
              "Repeat": "Once"
            }
            """);

        var library = new MacroLibrary(_folder.Path);
        library.Load(Array.Empty<Guid>(), out string? warning);
        var loaded = Assert.Single(library.Macros);

        Assert.Null(warning);
        Assert.Equal(2, loaded.Actions.Count);
        Assert.All(loaded.Actions, a => Assert.Equal("", a.Name));
        Assert.All(loaded.Actions, a => Assert.Null(a.GroupId));
        Assert.Empty(loaded.Groups);
    }

    [Fact]
    public void Blank_group_names_are_replaced_when_loading()
    {
        var group = new MacroGroup { Name = "  " };
        var macro = new MacroDefinition { Name = "M", Actions = { new MacroAction { GroupId = group.Id } }, Groups = { group } };
        Assert.True(new MacroLibrary(_folder.Path).TrySave(macro, out _));

        var library = new MacroLibrary(_folder.Path);
        library.Load(Array.Empty<Guid>(), out _);

        Assert.Equal("Group", Assert.Single(Assert.Single(library.Macros).Groups).Name);
    }
}
