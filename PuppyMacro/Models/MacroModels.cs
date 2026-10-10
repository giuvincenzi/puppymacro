using System;
using System.Collections.Generic;

namespace PuppyMacro.Models;

public enum MacroActionType
{
    PressKey,
    KeyDown,
    KeyUp,
    Click,
    MouseDown,
    MouseUp,
    MoveTo,
    MovePath,
    Scroll,
    PasteText,
}

public enum RepeatMode
{
    Once,
    Loop,
    Times,
}

public enum ScrollDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>One point of a recorded mouse path, in screen pixels, <see cref="T"/> ms after the path starts.</summary>
public sealed class PathPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public double T { get; set; }
}

/// <summary>
/// One step of a macro. <see cref="DelayMs"/> is the wait before the step starts,
/// counted from the end of the previous step. Positions are physical screen pixels.
/// </summary>
public sealed class MacroAction
{
    public MacroActionType Type { get; set; }
    public double DelayMs { get; set; }

    /// <summary>Optional name shown in the editor instead of the generated description.</summary>
    public string Name { get; set; } = "";

    /// <summary>The <see cref="MacroGroup"/> the action belongs to, or null.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Key for PressKey / KeyDown / KeyUp; mouse button (virtual-key code) for Click / MouseDown / MouseUp.</summary>
    public int Vk { get; set; }

    /// <summary>How long a key or button stays down (PressKey, Click).</summary>
    public double HoldMs { get; set; } = 30;

    /// <summary>1 = single click, 2 = double click.</summary>
    public int ClickCount { get; set; } = 1;

    /// <summary>PressKey / Click: how many times to press (default 1).</summary>
    public int Repeat { get; set; } = 1;

    /// <summary>PressKey / Click: pause between repeated presses.</summary>
    public double RepeatPauseMs { get; set; } = 50;

    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>MoveTo: travel to the point instead of jumping.</summary>
    public bool Smooth { get; set; }

    /// <summary>MoveTo smooth speed, 1 (very slow) to 5 (very fast).</summary>
    public int SmoothSpeed { get; set; } = 4;

    public List<PathPoint> Path { get; set; } = new();

    public ScrollDirection ScrollDirection { get; set; } = ScrollDirection.Down;
    public int ScrollSteps { get; set; } = 1;

    public string Text { get; set; } = "";
    public bool EnterBefore { get; set; }
    public bool EnterAfter { get; set; }

    public MacroAction Clone()
    {
        var copy = (MacroAction)MemberwiseClone();
        copy.Path = Path.ConvertAll(p => new PathPoint { X = p.X, Y = p.Y, T = p.T });
        return copy;
    }

    /// <summary>Time the step itself takes at ×1 speed (holds, paths, smooth moves).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double DurationMs => Type switch
    {
        MacroActionType.PressKey => HoldMs * Times + RepeatPauseMs * (Times - 1),
        MacroActionType.Click => (HoldMs * ClickCount + (ClickCount - 1) * 60) * Times + RepeatPauseMs * (Times - 1),
        MacroActionType.MovePath => Path.Count > 0 ? Path[^1].T : 0,
        MacroActionType.MoveTo => Smooth ? SmoothDurationMs(SmoothSpeed) : 0,
        MacroActionType.Scroll => Math.Max(0, ScrollSteps - 1) * 30,
        _ => 0,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    private int Times => Math.Max(1, Repeat);

    public static double SmoothDurationMs(int speed) => Math.Clamp(speed, 1, 5) switch
    {
        1 => 1000,
        2 => 600,
        3 => 350,
        4 => 200,
        _ => 100,
    };
}

/// <summary>
/// A named block of consecutive actions in the editor. Playback ignores groups: the actions
/// run in their order, grouped or not.
/// </summary>
public sealed class MacroGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The editor shows only the group's header.</summary>
    public bool Collapsed { get; set; }

    public MacroGroup Clone() => (MacroGroup)MemberwiseClone();
}

/// <summary>A recorded or hand-made sequence of actions.</summary>
public sealed class MacroDefinition
{
    public static readonly double[] SpeedSteps = { 0.25, 0.5, 1, 2, 4 };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<MacroAction> Actions { get; set; } = new();

    /// <summary>Groups of <see cref="Actions"/>, in the order they appear (see <see cref="NormalizeGroups"/>).</summary>
    public List<MacroGroup> Groups { get; set; } = new();

    public RepeatMode Repeat { get; set; } = RepeatMode.Once;
    public int RepeatCount { get; set; } = 3;
    public double Speed { get; set; } = 1;
    public ActivationMode Mode { get; set; } = ActivationMode.Toggle;
    public HotkeyBinding? Hotkey { get; set; }
    public bool Enabled { get; set; } = true;
    public bool SoundEnabled { get; set; }
    public string SoundName { get; set; } = "Chime";
    public FloatingButton FloatingButton { get; set; } = new();

    /// <summary>Executable name (e.g. "Diablo IV.exe") it works in; null = all apps (<see cref="AppScope"/>).</summary>
    public string? AppExe { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double TotalMs
    {
        get
        {
            double total = 0;
            foreach (var a in Actions)
                total += a.DelayMs + a.DurationMs;
            return total;
        }
    }

    public MacroDefinition Clone()
    {
        var copy = (MacroDefinition)MemberwiseClone();
        copy.Actions = Actions.ConvertAll(a => a.Clone());
        copy.Groups = Groups.ConvertAll(g => g.Clone());
        copy.Hotkey = Hotkey?.Clone();
        copy.FloatingButton = FloatingButton.Clone();
        return copy;
    }

    /// <summary>
    /// The macro the editor's Test plays: a copy of <paramref name="action"/> alone, right away
    /// (no delay before it), once, at ×1, without sound.
    /// </summary>
    public static MacroDefinition ForTest(MacroAction action)
    {
        var copy = action.Clone();
        copy.DelayMs = 0;
        copy.GroupId = null;
        return new MacroDefinition
        {
            Name = "Test",
            Actions = { copy },
            Repeat = RepeatMode.Once,
            Speed = 1,
            Enabled = false,
            SoundEnabled = false,
        };
    }

    public void CopyFrom(MacroDefinition other)
    {
        Name = other.Name;
        Actions = other.Actions.ConvertAll(a => a.Clone());
        Groups = other.Groups.ConvertAll(g => g.Clone());
        Repeat = other.Repeat;
        RepeatCount = other.RepeatCount;
        Speed = other.Speed;
        Mode = other.Mode;
        Hotkey = other.Hotkey?.Clone();
        Enabled = other.Enabled;
        SoundEnabled = other.SoundEnabled;
        SoundName = other.SoundName;
        FloatingButton = other.FloatingButton.Clone();
        AppExe = other.AppExe;
    }

    /// <summary>
    /// Keeps <see cref="Groups"/> consistent with <see cref="Actions"/>: an action whose group does
    /// not exist leaves it, a group's actions are consecutive (an action separated from the
    /// first run of its group leaves it), groups without actions are removed, and the groups are
    /// listed in the order they appear.
    /// </summary>
    public static void NormalizeGroups(List<MacroAction> actions, List<MacroGroup> groups)
    {
        var known = new Dictionary<Guid, MacroGroup>();
        foreach (var group in groups)
            known.TryAdd(group.Id, group);

        var ordered = new List<MacroGroup>();
        var seen = new HashSet<Guid>();
        Guid? previous = null;
        foreach (var action in actions)
        {
            if (action.GroupId is Guid id)
            {
                if (!known.TryGetValue(id, out var group))
                    action.GroupId = null;
                else if (id != previous && !seen.Add(id))
                    action.GroupId = null;
                else if (id != previous)
                    ordered.Add(group);
            }
            previous = action.GroupId;
        }
        groups.Clear();
        groups.AddRange(ordered);
    }

    public void NormalizeGroups() => NormalizeGroups(Actions, Groups);
}
