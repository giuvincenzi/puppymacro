using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Components;

/// <summary>
/// One field of an action or condition that can come from a component's parameter (a "slot"): its name in
/// <see cref="MacroAction.Bind"/>, the type and unit of the value it takes and the rule of its field (the same limits as the
/// editor's, from <see cref="MacroJson"/>). <see cref="Applies"/>: whether the field is used as the action is set (a click
/// that goes where a condition found something uses no Point; WaitSeconds only with Up to; an Area only when drawn).
/// </summary>
internal sealed record ComponentSlot(string Name, ParameterType Type, NumberUnit? Unit, ValueRule Rule, bool Applies);

/// <summary>
/// The table of slots: which fields of which actions and conditions can take a parameter's value, and how a value goes into
/// them. Written once here, in code: the JSON only names a slot. Not bindable: names, groups, the type of action, which
/// condition a "Where … was found" uses, and a linked row (its arguments are its values).
/// </summary>
internal static class ComponentSlots
{
    public const string DelayMs = "DelayMs";
    public const string Key = "Key";
    public const string Point = "Point";
    public const string HoldMs = "HoldMs";
    public const string Repeat = "Repeat";
    public const string RepeatPauseMs = "RepeatPauseMs";
    public const string ScrollSteps = "ScrollSteps";
    public const string Text = "Text";
    public const string WaitSeconds = "WaitSeconds";
    public const string Color = "Color";
    public const string Tolerance = "Tolerance";
    public const string Images = "Images";
    public const string Similarity = "Similarity";
    public const string Area = "Area";

    private static ComponentSlot Ms(string name, (double, double) range, bool applies = true) =>
        new(name, ParameterType.Number, NumberUnit.Ms, ValueRule.Number(range), applies);

    private static ComponentSlot Count(string name, (double, double) range) =>
        new(name, ParameterType.Number, NumberUnit.Count, ValueRule.Number(range, whole: true), true);

    private static ComponentSlot Percent(string name, (double, double) range) =>
        new(name, ParameterType.Number, NumberUnit.Percent, ValueRule.Number(range, whole: true), true);

    /// <summary>The slots of an action of this type, whether they apply as it is set or not.</summary>
    public static IReadOnlyList<ComponentSlot> Of(MacroAction a)
    {
        var slots = new List<ComponentSlot>();
        if (a.Type == MacroActionType.Component)
            return slots; // a linked row: its arguments are its values
        slots.Add(Ms(DelayMs, (0, MacroJson.MaxDelayMs)));
        bool fixedPoint = a.FoundId == null;
        switch (a.Type)
        {
            case MacroActionType.PressKey:
                slots.Add(new ComponentSlot(Key, ParameterType.Key, null, new ValueRule { Keyboard = true }, true));
                AddPresses(slots);
                break;
            case MacroActionType.KeyDown:
            case MacroActionType.KeyUp:
                slots.Add(new ComponentSlot(Key, ParameterType.Key, null, new ValueRule { Keyboard = true, KeyAlone = true }, true));
                break;
            case MacroActionType.Click:
                slots.Add(new ComponentSlot(Point, ParameterType.Point, null, ValueRule.Any, fixedPoint));
                AddPresses(slots);
                break;
            case MacroActionType.MouseDown:
            case MacroActionType.MouseUp:
            case MacroActionType.MoveTo:
                slots.Add(new ComponentSlot(Point, ParameterType.Point, null, ValueRule.Any, fixedPoint));
                break;
            case MacroActionType.Scroll:
                slots.Add(Count(ScrollSteps, MacroJson.ScrollSteps));
                break;
            case MacroActionType.PasteText:
                slots.Add(new ComponentSlot(Text, ParameterType.Text, null, new ValueRule { NotEmpty = true }, true));
                break;
            case MacroActionType.If:
                slots.Add(new ComponentSlot(WaitSeconds, ParameterType.Number, NumberUnit.Seconds, ValueRule.Number(MacroJson.WaitSeconds),
                    a.If?.Wait == ConditionWait.UpTo));
                break;
        }
        return slots;
    }

    private static void AddPresses(List<ComponentSlot> slots)
    {
        slots.Add(Ms(HoldMs, MacroJson.HoldMs));
        slots.Add(Count(Repeat, MacroJson.Repeat));
        slots.Add(Ms(RepeatPauseMs, MacroJson.RepeatPauseMs));
    }

    /// <summary>The slots of a condition of this kind, whether they apply as it is set or not.</summary>
    public static IReadOnlyList<ComponentSlot> Of(MacroCondition c)
    {
        var slots = new List<ComponentSlot>();
        bool drawn = c.Area != SearchArea.Around;
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
                slots.Add(new ComponentSlot(Point, ParameterType.Point, null, ValueRule.Any, true));
                slots.Add(new ComponentSlot(Color, ParameterType.Color, null, ValueRule.Any, true));
                slots.Add(Percent(Tolerance, MacroJson.Tolerance));
                break;
            case ConditionKind.Image:
                slots.Add(new ComponentSlot(Images, ParameterType.Image, null, ValueRule.Any, true));
                slots.Add(Percent(Similarity, MacroJson.Similarity));
                slots.Add(new ComponentSlot(Area, ParameterType.Area, null, ValueRule.Any, drawn));
                break;
            case ConditionKind.Text:
                slots.Add(new ComponentSlot(Text, ParameterType.Text, null, new ValueRule { NotEmpty = true }, true));
                slots.Add(new ComponentSlot(Area, ParameterType.Area, null, ValueRule.Any, drawn));
                break;
        }
        return slots;
    }

    public static ComponentSlot? Find(MacroAction a, string slot) => Of(a).FirstOrDefault(s => s.Name == slot);

    public static ComponentSlot? Find(MacroCondition c, string slot) => Of(c).FirstOrDefault(s => s.Name == slot);

    /// <summary>Writes a value read by <see cref="ComponentValues.Read"/> into an action's slot.</summary>
    public static void Write(MacroAction a, string slot, object value)
    {
        switch (slot)
        {
            case DelayMs: a.DelayMs = (double)value; break;
            case Key: a.Key = ((HotkeyBinding)value).Clone(); break;
            case Point: (a.X, a.Y) = ((PointValue)value).X_Y(); break;
            case HoldMs: a.HoldMs = (double)value; break;
            case Repeat: a.Repeat = (int)(double)value; break;
            case RepeatPauseMs: a.RepeatPauseMs = (double)value; break;
            case ScrollSteps: a.ScrollSteps = (int)(double)value; break;
            case Text: a.Text = (string)value; break;
            case WaitSeconds when a.If != null: a.If.WaitSeconds = (double)value; break;
        }
    }

    /// <summary>Writes a value read by <see cref="ComponentValues.Read"/> into a condition's slot.</summary>
    public static void Write(MacroCondition c, string slot, object value)
    {
        switch (slot)
        {
            case Point: (c.X, c.Y) = ((PointValue)value).X_Y(); break;
            case Color: c.Color = (string)value; break;
            case Tolerance: c.Tolerance = (int)(double)value; break;
            case Images: c.Images = ((List<ConditionImage>)value).ConvertAll(i => i.Clone()); break;
            case Similarity: c.Similarity = (int)(double)value; break;
            case Text: c.Text = (string)value; break;
            case Area:
                var area = (AreaValue)value;
                (c.AreaX, c.AreaY, c.AreaWidth, c.AreaHeight) = (area.X, area.Y, area.Width, area.Height);
                break;
        }
    }

    /// <summary>What an action's slot holds now, as a value of its type (what <see cref="Write(MacroAction,string,object)"/> takes).</summary>
    public static object? Value(MacroAction a, string slot) => slot switch
    {
        DelayMs => a.DelayMs,
        Key => a.Key,
        Point => new PointValue(a.X, a.Y),
        HoldMs => a.HoldMs,
        Repeat => (double)a.Repeat,
        RepeatPauseMs => a.RepeatPauseMs,
        ScrollSteps => (double)a.ScrollSteps,
        Text => a.Text,
        WaitSeconds => a.If?.WaitSeconds,
        _ => null,
    };

    /// <summary>What a condition's slot holds now, as a value of its type.</summary>
    public static object? Value(MacroCondition c, string slot) => slot switch
    {
        Point => new PointValue(c.X, c.Y),
        Color => c.Color,
        Tolerance => (double)c.Tolerance,
        Images => c.Images?.ConvertAll(i => i.Clone()),
        Similarity => (double)c.Similarity,
        Text => c.Text,
        Area => new AreaValue(c.AreaX, c.AreaY, c.AreaWidth, c.AreaHeight),
        _ => null,
    };

    private static (int, int) X_Y(this PointValue p) => (p.X, p.Y);
}
