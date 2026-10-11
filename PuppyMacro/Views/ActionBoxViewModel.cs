using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>The kinds of action a <see cref="ActionBox"/> edits (a loop has Press key, Click and Paste text).</summary>
public enum ActionKind
{
    PressKey,
    KeyDown,
    KeyUp,
    Click,
    MouseDown,
    MouseUp,
    MoveTo,
    Scroll,
    PasteText,
}

/// <summary>
/// One action in a <see cref="ActionBox"/>: a row of the loop editor, or the action of the macro's action
/// window. The same box in both; opened, it shows the action's own options and the loop's (While the loop
/// runs, Every) or nothing more in a macro (its Name and Delay are outside the box). Every option is a
/// property, every row a Show… property, in the order of <c>ActionBox.xaml</c>.
/// </summary>
public sealed class ActionBoxViewModel : INotifyPropertyChanged
{
    public const double NewKeyIntervalMs = 40;
    public const double NewTextIntervalSeconds = 5;

    private HotkeyBinding? _key;
    private int _mouseVk = KeyNames.VK_LBUTTON;
    private double _holdMs = LoopAction.DefaultHoldMs;
    private double _times = 1;
    private double _pauseMs = LoopAction.DefaultRepeatPauseMs;
    private bool _doubleClick;
    private bool _fixedPoint;
    private double _x;
    private double _y;
    private string _text = "";
    private bool _enterBefore;
    private bool _enterAfter;
    private bool _smooth;
    private double _smoothSpeed = 4;
    private int _directionIndex = (int)ScrollDirection.Down;
    private double _steps = 1;
    private bool _holdDown;
    private double? _intervalValue;
    private int _unitIndex;
    private bool _captureOnLoad;
    private bool _isExpanded;
    private readonly bool _holdWasDefault;

    private ActionBoxViewModel(ActionKind kind, bool inLoop)
    {
        Kind = kind;
        InLoop = inLoop;
    }

    /// <summary>A loop row (Press key, Click or Paste text).</summary>
    public ActionBoxViewModel(LoopAction action) : this(KindOf(action), inLoop: true)
    {
        if (action.Type == ActionType.Key && !action.IsClick)
            _key = action.KeyVk == 0 ? null : action.Key;
        if (action.IsClick)
            _mouseVk = action.KeyVk;
        _holdWasDefault = action.HoldMs == null;
        _holdMs = action.EffectiveHoldMs;
        _times = action.Times;
        _pauseMs = action.EffectiveRepeatPauseMs;
        _doubleClick = action.IsDoubleClick;
        _fixedPoint = action.HasFixedPoint;
        _x = action.X ?? 0;
        _y = action.Y ?? 0;
        _text = action.Text;
        _enterBefore = action.EnterBefore;
        _enterAfter = action.EnterAfter;
        _holdDown = action.HoldDown;
        _intervalValue = action.IntervalValue;
        _unitIndex = (int)action.IntervalUnit;
    }

    /// <summary>The action of the macro's action window (not Move path: it cannot be edited).</summary>
    public ActionBoxViewModel(MacroAction action) : this(KindOf(action.Type), inLoop: false)
    {
        if (Kind is ActionKind.PressKey or ActionKind.KeyDown or ActionKind.KeyUp)
            _key = action.Vk == 0 ? null : action.Key;
        if (Kind is ActionKind.Click or ActionKind.MouseDown or ActionKind.MouseUp)
            _mouseVk = KeyNames.IsMouse(action.Vk) ? action.Vk : KeyNames.VK_LBUTTON;
        _holdMs = action.HoldMs;
        _times = Math.Max(1, action.Repeat);
        _pauseMs = action.RepeatPauseMs;
        _doubleClick = action.ClickCount == 2;
        _x = action.X;
        _y = action.Y;
        _text = action.Text;
        _enterBefore = action.EnterBefore;
        _enterAfter = action.EnterAfter;
        _smooth = action.Smooth;
        _smoothSpeed = Math.Clamp(action.SmoothSpeed, 1, 5);
        _directionIndex = (int)action.ScrollDirection;
        _steps = Math.Max(1, action.ScrollSteps);
    }

    /// <summary>A new loop row of <paramref name="kind"/>: Press key waits for its key at once.</summary>
    public static ActionBoxViewModel NewLoopRow(ActionKind kind)
    {
        bool text = kind == ActionKind.PasteText;
        var row = new ActionBoxViewModel(new LoopAction
        {
            Type = text ? ActionType.Text : ActionType.Key,
            KeyVk = kind == ActionKind.Click ? KeyNames.VK_LBUTTON : 0,
            IntervalValue = text ? NewTextIntervalSeconds : NewKeyIntervalMs,
            IntervalUnit = text ? IntervalUnit.Seconds : IntervalUnit.Milliseconds,
        })
        {
            IsExpanded = true,
            CaptureOnLoad = kind == ActionKind.PressKey,
        };
        return row;
    }

    public static ActionKind KindOf(LoopAction action) =>
        action.Type == ActionType.Text ? ActionKind.PasteText : action.IsClick ? ActionKind.Click : ActionKind.PressKey;

    public static ActionKind KindOf(MacroActionType type) => type switch
    {
        MacroActionType.KeyDown => ActionKind.KeyDown,
        MacroActionType.KeyUp => ActionKind.KeyUp,
        MacroActionType.Click => ActionKind.Click,
        MacroActionType.MouseDown => ActionKind.MouseDown,
        MacroActionType.MouseUp => ActionKind.MouseUp,
        MacroActionType.MoveTo => ActionKind.MoveTo,
        MacroActionType.Scroll => ActionKind.Scroll,
        MacroActionType.PasteText => ActionKind.PasteText,
        _ => ActionKind.PressKey,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActionKind Kind { get; }
    public bool InLoop { get; }
    public bool InMacro => !InLoop;

    // ---- What the box is ----

    public bool IsPressKey => Kind == ActionKind.PressKey;
    public bool IsKeyKind => Kind is ActionKind.PressKey or ActionKind.KeyDown or ActionKind.KeyUp;
    public bool IsMouseKind => Kind is ActionKind.Click or ActionKind.MouseDown or ActionKind.MouseUp;
    public bool IsClick => Kind == ActionKind.Click;
    public bool IsMoveTo => Kind == ActionKind.MoveTo;
    public bool IsScroll => Kind == ActionKind.Scroll;
    public bool IsText => Kind == ActionKind.PasteText;

    /// <summary>The box's title, the type's name ("Press key", "Mouse button down").</summary>
    public string Title => Kind switch
    {
        ActionKind.PressKey => "Press key",
        ActionKind.KeyDown => "Key down",
        ActionKind.KeyUp => "Key up",
        ActionKind.Click => "Click",
        ActionKind.MouseDown => "Mouse button down",
        ActionKind.MouseUp => "Mouse button up",
        ActionKind.MoveTo => "Move to",
        ActionKind.Scroll => "Scroll",
        _ => "Paste text",
    };

    // ---- Rows (ActionBox.xaml, in this order) ----

    private bool PressOrClick => Kind is ActionKind.PressKey or ActionKind.Click;

    /// <summary>A loop's Press key or Click held down while the loop runs: only that choice (and Position) stay.</summary>
    public bool IsHolding => InLoop && PressOrClick && _holdDown;

    public bool ShowRun => InLoop && PressOrClick;
    public bool ShowEvery => InLoop && !IsHolding;
    public bool ShowPositionFirst => InMacro && IsMouseKind;
    public bool ShowModeFirst => InMacro && IsClick;
    public bool ShowHoldFor => PressOrClick && !IsHolding;
    public bool ShowModeAfterHold => InLoop && IsClick && !IsHolding;
    public bool ShowTimes => PressOrClick && !IsHolding;
    public bool ShowPositionLast => InLoop && IsClick;
    public bool ShowMovement => IsMoveTo;
    public bool ShowSpeed => IsMoveTo && _smooth;
    public bool ShowSteps => IsScroll;

    /// <summary>Key down and Key up have no options: a plain card, with nothing to open.</summary>
    public bool HasOptions => Kind is not (ActionKind.KeyDown or ActionKind.KeyUp);
    public bool HasNoOptions => !HasOptions;

    /// <summary>Pause between counts only with more than one press.</summary>
    public bool PauseEnabled => Times > 1;

    public string HoldDescription => IsClick ? "How long the button stays pressed each time" : "How long the key stays pressed each time";

    // ---- The key field (Press key: a combination; Key down / up: one key or a modifier alone) ----

    public bool KeyFieldKeyOnly => !IsPressKey;
    public bool KeyFieldModifierAlone => Kind is ActionKind.KeyDown or ActionKind.KeyUp;

    public string KeyPrompt => IsPressKey
        ? "Press the key or combination (Esc cancels)"
        : "Press a key, or Ctrl, Alt, Shift or Win alone (Esc cancels)";

    // ---- Values ----

    /// <summary>Press key: the key or combination; Key down, Key up: one key, or a modifier alone.</summary>
    public HotkeyBinding? Key
    {
        get => _key;
        set { _key = value?.Clone(); Changed(); Changed(nameof(Summary)); }
    }

    /// <summary>Click, Mouse button down / up: the button.</summary>
    public int MouseVk
    {
        get => _mouseVk;
        set { _mouseVk = value; Changed(); Changed(nameof(Summary)); }
    }

    public double HoldMs
    {
        get => _holdMs;
        set { _holdMs = value; Changed(); Changed(nameof(Summary)); }
    }

    public double Times
    {
        get => _times;
        set { _times = value; Changed(); Changed(nameof(PauseEnabled)); Changed(nameof(Summary)); }
    }

    public double PauseMs
    {
        get => _pauseMs;
        set { _pauseMs = value; Changed(); }
    }

    /// <summary>Mode of a click: 0 single, 1 double.</summary>
    public int ClickModeIndex
    {
        get => _doubleClick ? 1 : 0;
        set { _doubleClick = value == 1; Changed(); Changed(nameof(Summary)); }
    }

    /// <summary>A loop's Click at a fixed point (off: where the pointer is).</summary>
    public bool FixedPoint
    {
        get => _fixedPoint;
        set { _fixedPoint = value; Changed(); Changed(nameof(PointText)); Changed(nameof(Summary)); }
    }

    public string PointText => _fixedPoint ? "A fixed point" : "Where the pointer is";

    public double X
    {
        get => _x;
        set { _x = value; Changed(); Changed(nameof(Summary)); }
    }

    public double Y
    {
        get => _y;
        set { _y = value; Changed(); Changed(nameof(Summary)); }
    }

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Changed(); Changed(nameof(Summary)); }
    }

    public bool EnterBefore
    {
        get => _enterBefore;
        set { _enterBefore = value; Changed(); }
    }

    public bool EnterAfter
    {
        get => _enterAfter;
        set { _enterAfter = value; Changed(); Changed(nameof(Summary)); }
    }

    /// <summary>Move to: 0 instantly, 1 smoothly.</summary>
    public int MovementIndex
    {
        get => _smooth ? 1 : 0;
        set { _smooth = value == 1; Changed(); Changed(nameof(ShowSpeed)); Changed(nameof(Summary)); }
    }

    public double SmoothSpeed
    {
        get => _smoothSpeed;
        set { _smoothSpeed = Math.Clamp(Math.Round(value), 1, 5); Changed(); Changed(nameof(SpeedText)); }
    }

    /// <summary>Under the speed slider: "Speed: fast, about 0.2 s".</summary>
    public string SpeedText
    {
        get
        {
            int speed = (int)_smoothSpeed;
            string name = speed switch { 1 => "very slow", 2 => "slow", 3 => "normal", 4 => "fast", _ => "very fast" };
            double seconds = MacroAction.SmoothDurationMs(speed) / 1000;
            return $"Speed: {name}, about {seconds.ToString("0.##", CultureInfo.CurrentCulture)} s";
        }
    }

    /// <summary>Scroll: 0 up, 1 down, 2 left, 3 right.</summary>
    public int DirectionIndex
    {
        get => _directionIndex;
        set { _directionIndex = value; Changed(); Changed(nameof(Summary)); }
    }

    public double Steps
    {
        get => _steps;
        set { _steps = value; Changed(); Changed(nameof(Summary)); }
    }

    /// <summary>A loop's Press key or Click: kept held down while the loop runs (0 Repeat, 1 Hold down).</summary>
    public int RunIndex
    {
        get => _holdDown ? 1 : 0;
        set
        {
            _holdDown = value == 1;
            Changed();
            foreach (string name in new[]
            {
                nameof(IsHolding), nameof(ShowEvery), nameof(ShowHoldFor), nameof(ShowModeAfterHold), nameof(ShowTimes), nameof(Summary),
            })
                Changed(name);
        }
    }

    public double? IntervalValue
    {
        get => _intervalValue;
        set { _intervalValue = value; Changed(); Changed(nameof(Summary)); }
    }

    /// <summary>0 = ms, 1 = s, 2 = min, 3 = h.</summary>
    public int UnitIndex
    {
        get => _unitIndex;
        set { _unitIndex = value; Changed(); Changed(nameof(Summary)); }
    }

    public IntervalUnit Unit => (IntervalUnit)Math.Clamp(UnitIndex, 0, 3);

    public double IntervalMs => LoopAction.ToMilliseconds(IntervalValue ?? 0, Unit);

    /// <summary>A loop row just added: its key field waits for the key at once.</summary>
    public bool CaptureOnLoad
    {
        get => _captureOnLoad;
        set { _captureOnLoad = value; Changed(); }
    }

    /// <summary>The box is open (a new loop row, the macro's action window).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Changed(); }
    }

    // ---- Summary under the title ----

    private static readonly string[] UnitNames = { "ms", "s", "min", "h" };

    private string Every => $"every {(IntervalValue ?? 0).ToString("0.##", CultureInfo.CurrentCulture)} {UnitNames[Math.Clamp(UnitIndex, 0, 3)]}";

    private string TimesText => Times > 1 ? $" {Times:0} times" : "";

    private string Point => $"at {X:0}, {Y:0}";

    /// <summary>Under the title: what the action does (and, in a loop, how often).</summary>
    public string Summary
    {
        get
        {
            string what = Kind switch
            {
                ActionKind.PressKey => _key is { IsSet: true } ? KeyNames.Format(_key) + TimesText : "No key yet",
                ActionKind.KeyDown => _key is { IsSet: true } ? $"{KeyNames.Format(_key)}, until Key up" : "No key yet",
                ActionKind.KeyUp => _key is { IsSet: true } ? KeyNames.Format(_key) : "No key yet",
                ActionKind.Click => $"{MacroActionRowViewModel.ButtonName(_mouseVk)}{(_doubleClick ? " double click" : "")}{TimesText}, "
                    + (InLoop ? (_fixedPoint ? Point : "where the pointer is") : Point),
                ActionKind.MouseDown or ActionKind.MouseUp => $"{MacroActionRowViewModel.ButtonName(_mouseVk)}, {Point}",
                ActionKind.MoveTo => _smooth ? "Smoothly" : "Instantly",
                ActionKind.Scroll => $"{(ScrollDirection)Math.Clamp(_directionIndex, 0, 3)}, {Steps:0} {(Steps == 1 ? "step" : "steps")}",
                _ => string.IsNullOrEmpty(_text) ? "No text yet" : $"\"{FirstLine(_text)}\"" + (_enterAfter ? ", then Enter" : ""),
            };
            if (!InLoop)
                return what;
            return IsHolding ? $"{what}, held down while the loop runs" : $"{what}, {Every}";
        }
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    // ---- Checks ----

    /// <summary>Why the box cannot be saved as it is, or null. The windows show it.</summary>
    public string? Problem
    {
        get
        {
            if (IsKeyKind && _key is not { IsSet: true })
                return InLoop ? "Choose the key of every Press key." : "Choose a key.";
            if (IsText && string.IsNullOrEmpty(_text))
                return InLoop ? "Enter the text of every Paste text." : "Enter the text to paste.";
            if (IsHolding && _key is { HasModifiers: true })
                return "Hold down takes a key without Ctrl, Alt, Shift or Win: use Repeat for a combination.";
            return null;
        }
    }

    // ---- Back to the models ----

    public LoopAction ToLoopAction()
    {
        bool key = Kind != ActionKind.PasteText;
        var action = new LoopAction
        {
            Type = key ? ActionType.Key : ActionType.Text,
            Text = key ? "" : _text,
            EnterBefore = !key && _enterBefore,
            EnterAfter = !key && _enterAfter,
            HoldDown = key && _holdDown,
            IntervalValue = IntervalValue ?? 0,
            IntervalUnit = Unit,
        };
        if (IsClick)
        {
            action.KeyVk = _mouseVk;
            action.ClickCount = _doubleClick ? 2 : null;
            if (_fixedPoint)
            {
                action.X = (int)Math.Round(_x);
                action.Y = (int)Math.Round(_y);
            }
        }
        else if (key)
        {
            action.Key = _key ?? new HotkeyBinding();
        }
        if (key)
        {
            // The defaults are left out of the file, so a loop saved without changing them is as before.
            double hold = Number(_holdMs, LoopAction.DefaultHoldMs);
            action.HoldMs = hold == LoopAction.DefaultHoldMs && _holdWasDefault ? null : hold;
            int times = (int)Number(_times, 1);
            action.Repeat = times > 1 ? times : null;
            double pause = Number(_pauseMs, LoopAction.DefaultRepeatPauseMs);
            action.RepeatPauseMs = pause == LoopAction.DefaultRepeatPauseMs ? null : pause;
        }
        action.Normalize();
        return action;
    }

    /// <summary><paramref name="original"/> with the box's values (its Name, Delay and group are kept).</summary>
    public MacroAction ToMacroAction(MacroAction original)
    {
        var a = original.Clone();
        if (IsKeyKind)
        {
            a.Key = _key ?? new HotkeyBinding();
            if (Kind != ActionKind.PressKey)
                a.ClearModifiers();
            a.HoldMs = Number(_holdMs, 30);
        }
        else
        {
            a.ClearModifiers();
        }
        if (IsMouseKind)
        {
            a.Vk = _mouseVk;
            a.ClickCount = _doubleClick ? 2 : 1;
            a.HoldMs = Number(_holdMs, 30);
        }
        if (IsMouseKind || IsMoveTo)
        {
            a.X = (int)Number(_x, 0);
            a.Y = (int)Number(_y, 0);
        }
        if (IsMoveTo)
        {
            a.Smooth = _smooth;
            a.SmoothSpeed = (int)_smoothSpeed;
        }
        if (IsScroll)
        {
            a.ScrollDirection = (ScrollDirection)Math.Clamp(_directionIndex, 0, 3);
            a.ScrollSteps = (int)Math.Max(1, Number(_steps, 1));
        }
        if (IsText)
        {
            a.Text = _text;
            a.EnterBefore = _enterBefore;
            a.EnterAfter = _enterAfter;
        }
        if (PressOrClick)
        {
            a.Repeat = (int)Math.Max(1, Number(_times, 1));
            a.RepeatPauseMs = Math.Max(0, Number(_pauseMs, 50));
        }
        return a;
    }

    /// <summary>The whole number in a number field, or <paramref name="empty"/> when it is empty.</summary>
    private static double Number(double value, double empty) => double.IsNaN(value) ? empty : Math.Round(value);

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
