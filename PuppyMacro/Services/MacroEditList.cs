using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// Copied actions and groups, independent of any macro (the editor's Copy / Paste and
/// Duplicate). Actions of <see cref="Groups"/> point to them by id; the others have no group.
/// An If carries its Then and Else, and the groups used inside them are in <see cref="Groups"/> too.
/// </summary>
internal sealed class ActionBlock
{
    public List<MacroAction> Actions { get; } = new();
    public List<MacroGroup> Groups { get; } = new();

    public bool IsEmpty => Actions.Count == 0;
}

/// <summary>
/// The actions and groups of the macro editor, with the editing operations. The actions form a tree: the macro's own
/// list, and the Then and Else lists of each If. Groups are never nested, and a group's actions are consecutive in one
/// list: every operation keeps it that way. An operation without a list works on the macro's own list.
/// </summary>
internal sealed class MacroEditList
{
    public MacroEditList(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        Actions = actions.ToList();
        Groups = groups.ToList();
        Normalize();
    }

    /// <summary>The macro's own list (the root of the tree).</summary>
    public List<MacroAction> Actions { get; }
    public List<MacroGroup> Groups { get; }

    /// <summary>Every action, also inside Then and Else, in playback order.</summary>
    public IEnumerable<MacroAction> All => MacroDefinition.Walk(Actions);

    /// <summary>The list that holds <paramref name="action"/> (the macro's own or a Then or Else), or null.</summary>
    public List<MacroAction>? ListOf(MacroAction action) =>
        MacroDefinition.Lists(Actions).FirstOrDefault(l => l.Contains(action));

    /// <summary>The If whose Then or Else is <paramref name="list"/>, or null for the macro's own list.</summary>
    public MacroAction? OwnerOf(List<MacroAction> list) =>
        All.FirstOrDefault(a => a.Branches.Any(b => ReferenceEquals(b, list)));

    /// <summary>How deep <paramref name="action"/> is: 0 in the macro's own list, 1 inside an If's Then or Else…</summary>
    public int DepthOf(MacroAction action)
    {
        int depth = 0;
        var list = ListOf(action);
        while (list != null && OwnerOf(list) is { } owner)
        {
            depth++;
            list = ListOf(owner);
        }
        return depth;
    }

    public MacroGroup? GroupOf(MacroAction action) =>
        action.GroupId is Guid id ? Groups.Find(g => g.Id == id) : null;

    /// <summary>The list a group's actions are in.</summary>
    public List<MacroAction> ListOf(MacroGroup group) =>
        MacroDefinition.Lists(Actions).FirstOrDefault(l => l.Any(a => a.GroupId == group.Id)) ?? Actions;

    public List<MacroAction> ActionsOf(MacroGroup group) => ListOf(group).FindAll(a => a.GroupId == group.Id);

    /// <summary>The group's first action's index in its list (<see cref="ListOf(MacroGroup)"/>).</summary>
    public int FirstIndex(MacroGroup group) => ListOf(group).FindIndex(a => a.GroupId == group.Id);

    public int LastIndex(MacroGroup group) => ListOf(group).FindLastIndex(a => a.GroupId == group.Id);

    /// <summary>
    /// The selection: actions selected one by one and whole groups, in playback order. An action inside a selected If
    /// is left out: it goes where its If goes.
    /// </summary>
    public List<MacroAction> Expand(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var set = new HashSet<MacroAction>(actions);
        foreach (var group in groups)
            set.UnionWith(ActionsOf(group));
        var inside = new HashSet<MacroAction>(set.SelectMany(a => MacroDefinition.Walk(a.Branches.SelectMany(b => b))));
        return All.Where(a => set.Contains(a) && !inside.Contains(a)).ToList();
    }

    /// <summary>Copies of the actions (an If with its Then and Else) and of the whole groups, in playback order.</summary>
    public ActionBlock Copy(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var block = new ActionBlock();
        void Keep(MacroGroup group)
        {
            if (!block.Groups.Any(g => g.Id == group.Id))
                block.Groups.Add(group.Clone());
        }
        foreach (var action in Expand(actions, whole))
        {
            var copy = action.Clone();
            if (GroupOf(action) is { } group && whole.Contains(group))
                Keep(group);
            else
                copy.GroupId = null;
            // The groups inside a copied If go with it.
            foreach (var inner in MacroDefinition.Walk(copy.Branches.SelectMany(b => b)))
                if (GroupOf(inner) is { } innerGroup)
                    Keep(innerGroup);
            block.Actions.Add(copy);
        }
        return block;
    }

    /// <summary>Inserts copies of the block at <paramref name="index"/> of the macro's own list.</summary>
    public List<MacroAction> Insert(ActionBlock block, int index, MacroGroup? into) => Insert(block, Actions, index, into);

    /// <summary>
    /// Inserts copies of the block at <paramref name="index"/> of <paramref name="list"/>. Inside a group
    /// (<paramref name="into"/>) every action joins it; outside, the block's groups become new groups. The copies get new
    /// group and condition ids; an action of the block that goes where a condition of the block found something follows
    /// that condition's copy.
    /// </summary>
    public List<MacroAction> Insert(ActionBlock block, List<MacroAction> list, int index, MacroGroup? into)
    {
        var newGroups = block.Groups.ToDictionary(g => g.Id, _ => Guid.NewGuid());
        var inserted = new List<MacroAction>();
        foreach (var action in block.Actions)
        {
            var copy = action.Clone();
            if (into != null)
                copy.GroupId = into.Id;
            else if (copy.GroupId is Guid id && newGroups.TryGetValue(id, out Guid newId))
                copy.GroupId = newId;
            else
                copy.GroupId = null;
            foreach (var inner in MacroDefinition.Walk(copy.Branches.SelectMany(b => b)))
                inner.GroupId = inner.GroupId is Guid innerId && newGroups.TryGetValue(innerId, out Guid newInner) ? newInner : null;
            inserted.Add(copy);
        }
        RenewConditions(inserted);
        foreach (var group in block.Groups)
        {
            if (into != null && !inserted.SelectMany(a => MacroDefinition.Walk(a.Branches.SelectMany(b => b))).Any(a => a.GroupId == newGroups[group.Id]))
                continue; // inside a group only the groups of the inserted If's branches are new
            var copy = group.Clone();
            copy.Id = newGroups[group.Id];
            Groups.Add(copy);
        }
        RenameConditions(inserted, All);
        // A group whose only rows were just taken out (Unlink, Save as component: the rows' place keeps their group) comes
        // back with the actions put in it.
        if (into != null && !Groups.Any(g => g.Id == into.Id))
            Groups.Add(into);
        list.InsertRange(OutsideGroups(list, Math.Clamp(index, 0, list.Count), into), inserted);
        Normalize();
        return inserted;
    }

    /// <summary>
    /// The inserted conditions whose name the macro already has get the name of a copy, "Image 1 (2)" (names are unique in
    /// a macro: the actions name them for "Where … was found").
    /// </summary>
    private static void RenameConditions(List<MacroAction> inserted, IEnumerable<MacroAction> existing)
    {
        var taken = existing.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
            .Where(c => c.Finds && !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in MacroDefinition.Walk(inserted).SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()))
        {
            if (!condition.Finds || string.IsNullOrWhiteSpace(condition.Name))
                continue;
            if (taken.Contains(condition.Name.Trim()))
                condition.Name = ItemNames.Copy(condition.Name, taken);
            taken.Add(condition.Name.Trim());
        }
    }

    /// <summary>
    /// New ids for the conditions of the inserted Ifs, and what used them follows: the inserted actions' "Where … was found"
    /// and the {"Found"} arguments of their calls. Answers the old ids with their new ones.
    /// </summary>
    internal static Dictionary<Guid, Guid> RenewConditions(List<MacroAction> inserted)
    {
        var all = MacroDefinition.Walk(inserted).ToList();
        var renamed = new Dictionary<Guid, Guid>();
        foreach (var condition in all.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()))
        {
            Guid newId = Guid.NewGuid();
            renamed[condition.Id] = newId;
            condition.Id = newId;
        }
        foreach (var action in all)
        {
            if (action.FoundId is Guid found && renamed.TryGetValue(found, out Guid newFound))
                action.FoundId = newFound;
            RenewFound(action.Arguments, renamed);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
                RenewFound(condition.Arguments, renamed);
        }
        return renamed;
    }

    /// <summary>The {"Found"} arguments of a call that name a renewed condition name its new id.</summary>
    private static void RenewFound(Dictionary<string, System.Text.Json.Nodes.JsonNode?>? arguments, Dictionary<Guid, Guid> renamed)
    {
        foreach (var value in arguments?.Values ?? Enumerable.Empty<System.Text.Json.Nodes.JsonNode?>())
            if (value is System.Text.Json.Nodes.JsonObject o && Components.ComponentValues.Reference(o) is { Found: Guid id }
                && renamed.TryGetValue(id, out Guid renewed))
                o["Found"] = renewed.ToString();
    }

    /// <summary>Moves to <paramref name="index"/> of the macro's own list.</summary>
    public bool Move(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups, int index, MacroGroup? into) =>
        Move(actions, groups, Actions, index, into);

    /// <summary>
    /// Moves the actions and whole groups to <paramref name="index"/> of <paramref name="list"/> (counted before the
    /// move). Actions selected one by one join <paramref name="into"/> (or leave their group when it is null); whole
    /// groups keep their actions. Returns false when nothing can move there: a group cannot go into another group, and an
    /// If cannot go inside itself.
    /// </summary>
    public bool Move(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups, List<MacroAction> list, int index, MacroGroup? into)
    {
        var whole = groups.ToHashSet();
        if (into != null && (whole.Count > 0 || whole.Contains(into)))
            return false;
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;
        // The lists inside a moved If (its Then, its Else and those of the Ifs in them) cannot receive it.
        if (moved.Any(a => MacroDefinition.Lists(new List<MacroAction> { a }).Skip(1).Any(l => ReferenceEquals(l, list))))
            return false;

        index = Math.Clamp(index, 0, list.Count);
        int removedBefore = moved.Count(a => list.IndexOf(a) is int i && i >= 0 && i < index);
        foreach (var action in moved)
        {
            ListOf(action)?.Remove(action);
            if (GroupOf(action) is not { } group || !whole.Contains(group))
                action.GroupId = into?.Id;
        }
        list.InsertRange(OutsideGroups(list, Math.Clamp(index - removedBefore, 0, list.Count), into), moved);
        Normalize();
        return true;
    }

    /// <summary>
    /// Puts the actions (and the actions of whole groups) in a new group, where the first of them was. They must be in one
    /// list (a group never spans lists): otherwise nothing changes and the result is null. Groups they were in lose them,
    /// groups that end up empty are removed.
    /// </summary>
    public MacroGroup? Group(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups, string name)
    {
        var moved = Expand(actions, groups);
        if (moved.Count == 0)
            return null;
        var list = ListOf(moved[0])!;
        if (moved.Any(a => !list.Contains(a)))
            return null;
        var group = new MacroGroup { Name = name };
        int index = list.IndexOf(moved[0]);
        foreach (var action in moved)
        {
            list.Remove(action);
            action.GroupId = group.Id;
        }
        Groups.Add(group);
        list.InsertRange(OutsideGroups(list, index, null), moved);
        Normalize();
        return group;
    }

    /// <summary>Whether the actions and groups can become one group: all in one list.</summary>
    public bool CanGroup(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var moved = Expand(actions, groups);
        return moved.Count > 0 && ListOf(moved[0]) is { } list && moved.All(list.Contains);
    }

    /// <summary>The group's actions stay where they are, without a group.</summary>
    public void Ungroup(MacroGroup group)
    {
        foreach (var action in ActionsOf(group))
            action.GroupId = null;
        Normalize();
    }

    /// <summary>Removes the actions (an If with its Then and Else) and the whole groups with their actions.</summary>
    public void Remove(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        foreach (var action in Expand(actions, groups))
            ListOf(action)?.Remove(action);
        Normalize();
    }

    /// <summary>
    /// Alt+Up: one step up in the list of the first selected action, entering or leaving groups at their edges; at the top
    /// of a Then or Else, out of it, before its If. An If above is passed over, not entered.
    /// </summary>
    public bool MoveUp(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;
        var list = ListOf(moved[0])!;
        if (moved.Any(a => !list.Contains(a)))
            return false;
        int first = list.IndexOf(moved[0]);
        var own = GroupOf(moved[0]);
        bool groupsMove = whole.Count > 0;

        if (own != null && !whole.Contains(own))
        {
            // Inside a group: up within it, or out of it at its top.
            if (first == FirstIndex(own) || groupsMove)
                return Move(actions, whole, list, FirstIndex(own), null);
            return Move(actions, whole, list, first - 1, own);
        }
        if (first == 0)
        {
            // At the top of a Then or Else: out of it, before its If.
            if (OwnerOf(list) is not { } owner || ListOf(owner) is not { } parent)
                return false;
            return Move(actions, whole, parent, parent.IndexOf(owner), GroupOf(owner));
        }
        var above = list[first - 1];
        var aboveGroup = GroupOf(above);
        if (aboveGroup == null)
            return Move(actions, whole, list, first - 1, null);
        if (!groupsMove && !aboveGroup.Collapsed)
            return Move(actions, whole, list, first, aboveGroup); // into the group, at its end
        return Move(actions, whole, list, FirstIndex(aboveGroup), null); // over the group
    }

    /// <summary>Alt+Down: as <see cref="MoveUp"/>, down; at the bottom of a Then or Else, out of it, after its If.</summary>
    public bool MoveDown(IEnumerable<MacroAction> actions, IEnumerable<MacroGroup> groups)
    {
        var whole = groups.ToHashSet();
        var moved = Expand(actions, whole);
        if (moved.Count == 0)
            return false;
        var list = ListOf(moved[^1])!;
        if (moved.Any(a => !list.Contains(a)))
            return false;
        int last = list.IndexOf(moved[^1]);
        var own = GroupOf(moved[^1]);
        bool groupsMove = whole.Count > 0;

        if (own != null && !whole.Contains(own))
        {
            if (last == LastIndex(own) || groupsMove)
                return Move(actions, whole, list, LastIndex(own) + 1, null);
            return Move(actions, whole, list, last + 2, own);
        }
        if (last == list.Count - 1)
        {
            if (OwnerOf(list) is not { } owner || ListOf(owner) is not { } parent)
                return false;
            return Move(actions, whole, parent, parent.IndexOf(owner) + 1, GroupOf(owner));
        }
        var below = list[last + 1];
        var belowGroup = GroupOf(below);
        if (belowGroup == null)
            return Move(actions, whole, list, last + 2, null);
        if (!groupsMove && !belowGroup.Collapsed)
            return Move(actions, whole, list, last + 1, belowGroup); // into the group, at its start
        return Move(actions, whole, list, LastIndex(belowGroup) + 1, null); // over the group
    }

    /// <summary>Outside groups, an index in the middle of a group moves to the group's end.</summary>
    private static int OutsideGroups(List<MacroAction> list, int index, MacroGroup? into)
    {
        if (into != null || index <= 0 || index >= list.Count)
            return index;
        Guid? before = list[index - 1].GroupId;
        if (before == null || before != list[index].GroupId)
            return index;
        while (index < list.Count && list[index].GroupId == before)
            index++;
        return index;
    }

    /// <summary>Every group's actions together, one group per list (after an action's window changed an If's groups).</summary>
    public void Normalize() => MacroDefinition.NormalizeGroups(Actions, Groups);
}
