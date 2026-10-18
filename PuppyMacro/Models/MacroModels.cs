using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

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
    If,

    /// <summary>A row linked to a <see cref="LibraryComponent"/> (<see cref="MacroAction.ComponentId"/>): it plays the component.</summary>
    Component,
}

/// <summary>What a condition of an If action looks at.</summary>
public enum ConditionKind
{
    Pixel,
    Image,
    Text,
}

/// <summary>Whether Then runs when all the conditions are true or when one of them is.</summary>
public enum ConditionMatch
{
    All,
    Any,
}

/// <summary>How long an If action waits for its conditions.</summary>
public enum ConditionWait
{
    /// <summary>Checks once, without waiting.</summary>
    Once,

    /// <summary>Waits up to <see cref="MacroIf.WaitSeconds"/>.</summary>
    UpTo,

    /// <summary>Waits as long as it takes (until the macro is stopped).</summary>
    Forever,
}

/// <summary>What an If action does when its conditions are not true in time.</summary>
public enum ConditionFail
{
    /// <summary>Runs the Else actions, then goes on.</summary>
    Else,

    /// <summary>Stops the macro.</summary>
    Stop,
}

/// <summary>Where an Image or Text condition looks.</summary>
public enum SearchArea
{
    /// <summary>A little larger than where each image was taken.</summary>
    Around,

    /// <summary>The area the user drew (<see cref="MacroCondition.AreaX"/>…).</summary>
    Drawn,

    /// <summary>The whole screen whose bounds are <see cref="MacroCondition.AreaX"/>….</summary>
    Screen,
}

/// <summary>How a Text condition compares the text it reads.</summary>
public enum TextMatch
{
    Contains,
    Exact,
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
/// One condition of an If action. Only the fields of its <see cref="Kind"/> are set; the others keep their defaults and
/// are left out of the file. Positions and areas are physical screen pixels.
/// </summary>
public sealed class MacroCondition
{
    private const JsonIgnoreCondition Default = JsonIgnoreCondition.WhenWritingDefault;
    private const JsonIgnoreCondition Null = JsonIgnoreCondition.WhenWritingNull;

    /// <summary>Lets a mouse action inside Then use where this condition found its image or text.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public ConditionKind Kind { get; set; }

    /// <summary>
    /// True when what it looks for is not there: the pixel has another color, the image or the text is not on the screen.
    /// It finds no place for the actions in Then. False in older files.
    /// </summary>
    [JsonIgnore(Condition = Default)]
    public bool Not { get; set; }

    /// <summary>Image and Text: the name the actions in Then use for where it was found.</summary>
    [JsonIgnore(Condition = Null)]
    public string? Name { get; set; }

    /// <summary>Pixel: the point.</summary>
    [JsonIgnore(Condition = Default)]
    public int X { get; set; }

    [JsonIgnore(Condition = Default)]
    public int Y { get; set; }

    /// <summary>Pixel: the color, "#RRGGBB".</summary>
    [JsonIgnore(Condition = Null)]
    public string? Color { get; set; }

    /// <summary>Pixel: how far each of red, green and blue may be from <see cref="Color"/>, in percent of 255.</summary>
    [JsonIgnore(Condition = Default)]
    public int Tolerance { get; set; }

    /// <summary>Image: the images to look for; the condition is true when one of them is on the screen.</summary>
    [JsonIgnore(Condition = Null)]
    public List<ConditionImage>? Images { get; set; }

    /// <summary>Image: also look for the images a little bigger or smaller (85% to 115%), when they change size.</summary>
    [JsonIgnore(Condition = Default)]
    public bool NearbySizes { get; set; }

    /// <summary>Image and Text: where to look.</summary>
    [JsonIgnore(Condition = Default)]
    public SearchArea Area { get; set; }

    /// <summary>The drawn area or the screen's bounds (<see cref="SearchArea.Drawn"/>, <see cref="SearchArea.Screen"/>).</summary>
    [JsonIgnore(Condition = Default)]
    public int AreaX { get; set; }

    [JsonIgnore(Condition = Default)]
    public int AreaY { get; set; }

    [JsonIgnore(Condition = Default)]
    public int AreaWidth { get; set; }

    [JsonIgnore(Condition = Default)]
    public int AreaHeight { get; set; }

    /// <summary>Image: how closely the screen must look like the image, in percent.</summary>
    [JsonIgnore(Condition = Default)]
    public int Similarity { get; set; }

    /// <summary>Text: the text to find.</summary>
    [JsonIgnore(Condition = Null)]
    public string? Text { get; set; }

    [JsonIgnore(Condition = Default)]
    public TextMatch TextMatch { get; set; }

    [JsonIgnore(Condition = Default)]
    public bool MatchCase { get; set; }

    /// <summary>Text: the language of the text (a BCP 47 tag), or null for Windows' language.</summary>
    [JsonIgnore(Condition = Null)]
    public string? Language { get; set; }

    /// <summary>Image and Text: it has a name and looks in an area.</summary>
    [JsonIgnore]
    public bool Finds => Kind is ConditionKind.Image or ConditionKind.Text;

    /// <summary>Whether a mouse action in Then can go where it was found: not when it is true for not being there.</summary>
    [JsonIgnore]
    public bool GivesPlace => Finds && !Not;

    public MacroCondition Clone()
    {
        var copy = (MacroCondition)MemberwiseClone();
        copy.Images = Images?.ConvertAll(i => i.Clone());
        return copy;
    }
}

/// <summary>One image of an Image condition and where it was taken (its top-left corner, physical pixels).</summary>
public sealed class ConditionImage
{
    /// <summary>The SHA-256 of its PNG file in the data folder's images folder (64 lowercase hexadecimal digits).</summary>
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }

    public ConditionImage Clone() => (ConditionImage)MemberwiseClone();
}

/// <summary>
/// An If action's conditions and branches: it checks the conditions, waiting as <see cref="Wait"/> says, then runs
/// <see cref="Then"/>; when they are not true in time it runs <see cref="Else"/> or stops the macro.
/// </summary>
public sealed class MacroIf
{
    public List<MacroCondition> Conditions { get; set; } = new();
    public ConditionMatch Match { get; set; }
    public ConditionWait Wait { get; set; }

    /// <summary>With <see cref="ConditionWait.UpTo"/>: the longest wait, in seconds (not changed by the macro's speed).</summary>
    public double WaitSeconds { get; set; }

    public ConditionFail OnFail { get; set; }
    public List<MacroAction> Then { get; set; } = new();
    public List<MacroAction> Else { get; set; } = new();

    public MacroIf Clone()
    {
        var copy = (MacroIf)MemberwiseClone();
        copy.Conditions = Conditions.ConvertAll(c => c.Clone());
        copy.Then = Then.ConvertAll(a => a.Clone());
        copy.Else = Else.ConvertAll(a => a.Clone());
        return copy;
    }
}

/// <summary>
/// One step of a macro. <see cref="DelayMs"/> is the wait before the step starts,
/// counted from the end of the previous step. Positions are physical screen pixels.
/// </summary>
public sealed class MacroAction : KeyModifierFields
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

    /// <summary>If: its conditions and branches; null for the other types.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MacroIf? If { get; set; }

    /// <summary>
    /// Click, MouseDown, MouseUp, MoveTo inside an If's Then: the <see cref="MacroCondition.Id"/> whose image or text
    /// gives the point (its center, moved by <see cref="FoundOffsetX"/> / <see cref="FoundOffsetY"/>) instead of
    /// <see cref="X"/> / <see cref="Y"/>; null = the fixed point.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? FoundId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FoundOffsetX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FoundOffsetY { get; set; }

    /// <summary>Component: the <see cref="LibraryComponent.Id"/> it links; null for the other types.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// Component, at run time only: the component's actions, each link with its own condition Ids
    /// (<c>ComponentLibrary.Resolve</c>, when the engine's snapshot is made); never saved.
    /// </summary>
    [JsonIgnore]
    public List<MacroAction>? Resolved { get; set; }

    /// <summary>Component, at run time only: the component's name when it was resolved (for the Debug log).</summary>
    [JsonIgnore]
    public string ResolvedName { get; set; } = "";

    /// <summary>The actions of its Then and Else (an If's), none for the other types.</summary>
    [JsonIgnore]
    public IEnumerable<List<MacroAction>> Branches
    {
        get
        {
            if (If?.Then != null)
                yield return If.Then;
            if (If?.Else != null)
                yield return If.Else;
        }
    }

    /// <summary>
    /// PressKey: the key with its Ctrl, Alt, Shift and Win (the other types have none); KeyDown, KeyUp: the key,
    /// which can be a modifier key alone.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HotkeyBinding Key
    {
        get => Combine(Vk);
        set
        {
            Vk = value.Vk;
            SetModifiers(value);
        }
    }

    public MacroAction Clone()
    {
        var copy = (MacroAction)MemberwiseClone();
        copy.Path = Path.ConvertAll(p => new PathPoint { X = p.X, Y = p.Y, T = p.T });
        copy.If = If?.Clone();
        copy.Resolved = Resolved?.ConvertAll(a => a.Clone());
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

/// <summary>The shortest and the longest time of some actions at ×1, in milliseconds.</summary>
public readonly record struct DurationRange(double MinMs, double MaxMs, bool Endless)
{
    public static DurationRange Of(IEnumerable<MacroAction> actions)
    {
        double min = 0, max = 0;
        bool endless = false;
        foreach (var action in actions)
        {
            var range = Of(action);
            min += range.MinMs;
            max += range.MaxMs;
            endless |= range.Endless;
        }
        return new DurationRange(min, max, endless);
    }

    private static DurationRange Of(MacroAction action)
    {
        double own = action.DelayMs + action.DurationMs;
        if (action.If is not { } block)
            return new DurationRange(own, own, false);
        var then = Of(block.Then);
        double wait = block.Wait == ConditionWait.UpTo ? Math.Max(0, block.WaitSeconds) * 1000 : 0;
        var fail = block.OnFail == ConditionFail.Else ? Of(block.Else) : default;
        // True at once: Then right away. Never true: the whole wait, then Else (or the macro stops).
        double min = Math.Min(then.MinMs, wait + fail.MinMs);
        double max = Math.Max(wait + then.MaxMs, wait + fail.MaxMs);
        bool endless = block.Wait == ConditionWait.Forever || then.Endless || fail.Endless;
        return new DurationRange(own + min, own + max, endless);
    }
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

    /// <summary>
    /// How many times a second its screen conditions look while they wait; 0 = every new frame of the screen. More checks
    /// notice changes sooner and use more of the computer.
    /// </summary>
    public int ChecksPerSecond { get; set; } = DefaultChecksPerSecond;

    public const int DefaultChecksPerSecond = 30;

    /// <summary>The values offered for <see cref="ChecksPerSecond"/> (0 = every frame).</summary>
    public static readonly int[] ChecksPerSecondSteps = { 10, 30, 60, 0 };

    /// <summary>
    /// Keeps the log of its last run (<c>RunLog</c>): what each action did, what each If saw, pictures of the screen when
    /// an If was not true and at the end. False in older files.
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>Executable name (e.g. "Diablo IV.exe") it works in; null = all apps (<see cref="AppScope"/>).</summary>
    public string? AppExe { get; set; }

    /// <summary>Shown in overlay mode: its row in the overlay panel or its floating button.</summary>
    public bool ShowInOverlay { get; set; } = true;

    /// <summary>Left out of the overlay while it is disabled (otherwise shown as disabled).</summary>
    public bool HideInOverlayWhenDisabled { get; set; }

    /// <summary>A click on it in the overlay also reaches the window under it.</summary>
    public bool OverlayClickPassesThrough { get; set; }

    /// <summary>
    /// How long one round takes at ×1: the shortest and the longest. An If makes them differ (it may wait, then run Then
    /// or Else); a wait as long as it takes makes the longest unknown (<see cref="DurationRange.Endless"/>).
    /// </summary>
    [JsonIgnore]
    public DurationRange Duration => DurationRange.Of(Actions);

    /// <summary>The shortest round at ×1 (<see cref="Duration"/>).</summary>
    [JsonIgnore]
    public double TotalMs => Duration.MinMs;

    /// <summary>Every action, also those inside Then and Else, in playback order.</summary>
    public static IEnumerable<MacroAction> Walk(IEnumerable<MacroAction> actions)
    {
        foreach (var action in actions)
        {
            yield return action;
            foreach (var branch in action.Branches)
                foreach (var inner in Walk(branch))
                    yield return inner;
        }
    }

    /// <summary>The macro's action lists: its own, then each Then and Else, in playback order.</summary>
    public static IEnumerable<List<MacroAction>> Lists(List<MacroAction> actions)
    {
        yield return actions;
        foreach (var action in actions)
            foreach (var branch in action.Branches)
                foreach (var list in Lists(branch))
                    yield return list;
    }

    [JsonIgnore]
    public IEnumerable<MacroAction> AllActions => Walk(Actions);

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
        ChecksPerSecond = other.ChecksPerSecond;
        Debug = other.Debug;
        ShowInOverlay = other.ShowInOverlay;
        HideInOverlayWhenDisabled = other.HideInOverlayWhenDisabled;
        OverlayClickPassesThrough = other.OverlayClickPassesThrough;
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

        // A group lives in one list (the macro's own, or one Then or Else): its actions there are consecutive.
        var ordered = new List<MacroGroup>();
        var seen = new HashSet<Guid>();
        foreach (var list in Lists(actions))
        {
            Guid? previous = null;
            foreach (var action in list)
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
        }
        // Lists are walked list by list; the groups are listed in the order their first action plays.
        var position = new Dictionary<Guid, int>();
        int index = 0;
        foreach (var action in Walk(actions))
            if (action.GroupId is Guid id)
                position.TryAdd(id, index++);
        groups.Clear();
        groups.AddRange(ordered.OrderBy(g => position[g.Id]));
    }

    public void NormalizeGroups() => NormalizeGroups(Actions, Groups);
}
