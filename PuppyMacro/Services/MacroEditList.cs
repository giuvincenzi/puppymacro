using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// Copied actions and groups, independent of any macro (the editor's Copy / Paste and
/// Duplicate). Actions of <see cref="Groups"/> point to them by id; the others have no group.
/// </summary>
internal sealed class ActionBlock
{
    public List<MacroAction> Actions { get; } = new();
    public List<MacroGroup> Groups { get; } = new();

    public bool IsEmpty => Actions.Count == 0;
}

/// <summary>
/// The actions and groups of the macro editor, with the editing operations. Groups are never
/// nested and a group's actions are always consecutive: every operation keeps it that way.
/// </summary>
internal sealed class MacroEditList
{
    public MacroEditList(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        Actions = actions.ToList();
        Groups = groups.ToList();
        Normalize();
    }

    public List<MacroAction> Actions { get; }
    public List<MacroGroup> Groups { get; }

    public MacroGroup? GroupOf(MacroAction action) =>
        action.GroupId is Guid id ? Groups.Find(g => g.Id == id) : null;

    public List<MacroAction> ActionsOf(MacroGroup group) => Actions.FindAll(a => a.GroupId == group.Id);

    public int FirstIndex(MacroGroup group) => Actions.FindIndex(a => a.GroupId == group.Id);

    public int LastIndex(MacroGroup group) => Actions.FindLastIndex(a => a.GroupId == group.Id);

    /// <summary>The selection: actions selected one by one and whole groups.</summary>
    public List<MacroAction> Expand(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var set = new HashSet<MacroAction>(actions);
        foreach (var group in groups)
            set.UnionWith(ActionsOf(group));
        return Actions.Where(set.Contains).ToList();
    }

    /// <summary>Copies of the actions and of the whole groups, in list order.</summary>
    public ActionBlock Copy(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var block = new ActionBlock();
        foreach (var action in Expand(actions, whole))
        {
            var copy = action.Clone();
            if (GroupOf(action) is { } group && whole.Contains(group))
            {
                if (!block.Groups.Any(g => g.Id == group.Id))
                    block.Groups.Add(group.Clone());
            }
            else
            {
                copy.GroupId = null;
            }
            block.Actions.Add(copy);
        }
        return block;
    }

    /// <summary>
    /// Inserts copies of the block at <paramref name="index"/>. Inside a group (<paramref name="into"/>)
    /// every action joins it; outside, the block's groups become new groups.
    /// </summary>
    public List<MacroAction> Insert(ActionBlock block, int index, MacroGroup? into)
    {
        var newIds = block.Groups.ToDictionary(g => g.Id, _ => Guid.NewGuid());
        var inserted = new List<MacroAction>();
        foreach (var action in block.Actions)
        {
            var copy = action.Clone();
            if (into != null)
                copy.GroupId = into.Id;
            else if (copy.GroupId is Guid id && newIds.TryGetValue(id, out Guid newId))
                copy.GroupId = newId;
            else
                copy.GroupId = null;
            inserted.Add(copy);
        }
        if (into == null)
        {
            foreach (var group in block.Groups)
            {
                var copy = group.Clone();
                copy.Id = newIds[group.Id];
                Groups.Add(copy);
            }
        }
        Actions.InsertRange(OutsideGroups(Math.Clamp(index, 0, Actions.Count), into), inserted);
        Normalize();
        return inserted;
    }

    /// <summary>
    /// Moves the actions and whole groups to <paramref name="index"/> (counted before the move).
    /// Actions selected one by one join <paramref name="into"/> (or leave their group when it is
    /// null); whole groups keep their actions. Returns false when nothing can move there: a group
    /// cannot go into another group.
    /// </summary>
    public bool Move(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups, int index, MacroGroup? into)
    {
        var whole = groups.ToHashSet();
        if (into != null && (whole.Count > 0 || whole.Contains(into)))
            return false;
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;

        index = Math.Clamp(index, 0, Actions.Count);
        int removedBefore = moved.Count(a => Actions.IndexOf(a) < index);
        foreach (var action in moved)
        {
            Actions.Remove(action);
            if (GroupOf(action) is not { } group || !whole.Contains(group))
                action.GroupId = into?.Id;
        }
        Actions.InsertRange(OutsideGroups(index - removedBefore, into), moved);
        Normalize();
        return true;
    }

    /// <summary>
    /// Puts the actions (and the actions of whole groups) in a new group, where the first of
    /// them was. Groups they were in lose them, groups that end up empty are removed.
    /// </summary>
    public MacroGroup? Group(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups, string name)
    {
        var moved = Expand(actions, groups);
        if (moved.Count == 0)
            return null;
        var group = new MacroGroup { Name = name };
        int index = Actions.IndexOf(moved[0]);
        foreach (var action in moved)
        {
            Actions.Remove(action);
            action.GroupId = group.Id;
        }
        Groups.Add(group);
        Actions.InsertRange(OutsideGroups(index, null), moved);
        Normalize();
        return group;
    }

    /// <summary>The group's actions stay where they are, without a group.</summary>
    public void Ungroup(MacroGroup group)
    {
        foreach (var action in ActionsOf(group))
            action.GroupId = null;
        Normalize();
    }

    /// <summary>Removes the actions and the whole groups with their actions.</summary>
    public void Remove(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        foreach (var action in Expand(actions, groups))
            Actions.Remove(action);
        Normalize();
    }

    /// <summary>Alt+Up: one step up, entering or leaving groups at their edges.</summary>
    public bool MoveUp(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;
        int first = Actions.IndexOf(moved[0]);
        var own = GroupOf(moved[0]);
        bool groupsMove = whole.Count > 0;

        if (own != null && !whole.Contains(own))
        {
            // Inside a group: up within it, or out of it at its top.
            if (first == FirstIndex(own) || groupsMove)
                return Move(actions, whole, FirstIndex(own), null);
            return Move(actions, whole, first - 1, own);
        }
        if (first == 0)
            return false;
        var above = Actions[first - 1];
        var aboveGroup = GroupOf(above);
        if (aboveGroup == null)
            return Move(actions, whole, first - 1, null);
        if (!groupsMove && !aboveGroup.Collapsed)
            return Move(actions, whole, first, aboveGroup); // into the group, at its end
        return Move(actions, whole, FirstIndex(aboveGroup), null); // over the group
    }

    /// <summary>Alt+Down: one step down, entering or leaving groups at their edges.</summary>
    public bool MoveDown(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;
        int last = Actions.IndexOf(moved[^1]);
        var own = GroupOf(moved[^1]);
        bool groupsMove = whole.Count > 0;

        if (own != null && !whole.Contains(own))
        {
            if (last == LastIndex(own) || groupsMove)
                return Move(actions, whole, LastIndex(own) + 1, null);
            return Move(actions, whole, last + 2, own);
        }
        if (last == Actions.Count - 1)
            return false;
        var below = Actions[last + 1];
        var belowGroup = GroupOf(below);
        if (belowGroup == null)
            return Move(actions, whole, last + 2, null);
        if (!groupsMove && !belowGroup.Collapsed)
            return Move(actions, whole, last + 1, belowGroup); // into the group, at its start
        return Move(actions, whole, LastIndex(belowGroup) + 1, null); // over the group
    }

    /// <summary>Outside groups, an index in the middle of a group moves to the group's end.</summary>
    private int OutsideGroups(int index, MacroGroup? into)
    {
        if (into != null || index <= 0 || index >= Actions.Count)
            return index;
        Guid? before = Actions[index - 1].GroupId;
        if (before == null || before != Actions[index].GroupId)
            return index;
        while (index < Actions.Count && Actions[index].GroupId == before)
            index++;
        return index;
    }

    private void Normalize() => MacroDefinition.NormalizeGroups(Actions, Groups);
}
