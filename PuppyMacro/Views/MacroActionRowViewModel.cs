using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using FontIconData = iNKORE.UI.WPF.Modern.Common.IconKeys.FontIconData;
using SegoeFluentIcons = iNKORE.UI.WPF.Modern.Common.IconKeys.SegoeFluentIcons;

namespace PuppyMacro.Views;

/// <summary>An item of the macro editor's list: an action row or a group header.</summary>
public abstract class MacroEditorItem : INotifyPropertyChanged
{
    private bool _dropBefore;
    private bool _dropAfter;
    private bool _dropIndented;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Drop indicator above or below the item while dragging.</summary>
    public bool DropBefore
    {
        get => _dropBefore;
        set { if (_dropBefore != value) { _dropBefore = value; OnPropertyChanged(); } }
    }

    public bool DropAfter
    {
        get => _dropAfter;
        set { if (_dropAfter != value) { _dropAfter = value; OnPropertyChanged(); } }
    }

    /// <summary>The drop indicator is indented: the dragged actions go into a group.</summary>
    public bool DropIndented
    {
        get => _dropIndented;
        set { if (_dropIndented != value) { _dropIndented = value; OnPropertyChanged(); } }
    }

    /// <summary>Name of the list item for screen readers and UI Automation.</summary>
    public abstract string AccessibleName { get; }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The header of a group in the macro editor.</summary>
public sealed class MacroGroupViewModel : MacroEditorItem
{
    private string _info = "";

    public MacroGroupViewModel(MacroGroup group) => Group = group;

    public MacroGroup Group { get; }

    public string Name
    {
        get => Group.Name;
        set
        {
            if (Group.Name == value)
                return;
            Group.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsCollapsed
    {
        get => Group.Collapsed;
        set
        {
            if (Group.Collapsed == value)
                return;
            Group.Collapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(ToggleText));
        }
    }

    public bool IsExpanded => !IsCollapsed;

    public string ToggleText => IsCollapsed ? Strings.MacroEditor_ExpandGroup : Strings.MacroEditor_CollapseGroup;

    /// <summary>Range, count and duration, e.g. "#4–8 · 5 actions · 2.1 s".</summary>
    public string Info
    {
        get => _info;
        set { if (_info != value) { _info = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccessibleName)); } }
    }

    public override string AccessibleName => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_GroupAccessibleName, Name, Info);
}

/// <summary>One action row of the macro editor.</summary>
public sealed class MacroActionRowViewModel : MacroEditorItem
{
    private int _number;
    private bool _isInGroup;

    public MacroActionRowViewModel(MacroAction action) => Action = action;

    public MacroAction Action { get; private set; }

    public int Number
    {
        get => _number;
        set { if (_number != value) { _number = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccessibleName)); } }
    }

    /// <summary>The row is indented under its group's header.</summary>
    public bool IsInGroup
    {
        get => _isInGroup;
        set { if (_isInGroup != value) { _isInGroup = value; OnPropertyChanged(); } }
    }

    /// <summary>Delay before the action, in ms.</summary>
    public double? Delay
    {
        get => Action.DelayMs;
        set
        {
            // An emptied number box gives NaN: no delay.
            double v = value is double d && !double.IsNaN(d) ? System.Math.Clamp(d, 0, 86_400_000) : 0;
            if (Action.DelayMs == v)
                return;
            Action.DelayMs = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayText));
        }
    }

    /// <summary>The delay shown on the row, e.g. "50 ms".</summary>
    public string DelayText => string.Format(CultureInfo.CurrentCulture, Strings.MacroEditor_DelayMs, Action.DelayMs);

    public bool IsKeyIcon => Action.Type is MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp;
    public bool IsMouseIcon => Action.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp;
    public bool IsMoveIcon => Action.Type is MacroActionType.MoveTo or MacroActionType.MovePath;
    public bool IsScrollIcon => Action.Type == MacroActionType.Scroll;
    public bool IsTextIcon => Action.Type == MacroActionType.PasteText;

    /// <summary>The icon of a type of action, the same as its row's (used by the Add action menu).</summary>
    public static FontIconData IconOf(MacroActionType type) => type switch
    {
        MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp => SegoeFluentIcons.KeyboardClassic,
        MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp => SegoeFluentIcons.Mouse,
        MacroActionType.MoveTo or MacroActionType.MovePath => SegoeFluentIcons.Move,
        MacroActionType.Scroll => SegoeFluentIcons.ScrollUpDown,
        _ => SegoeFluentIcons.Font,
    };

    /// <summary>Recorded paths cannot be edited by hand (only their delay).</summary>
    public bool IsEditable => Action.Type != MacroActionType.MovePath;

    public bool IsNamed => !string.IsNullOrWhiteSpace(Action.Name);

    /// <summary>The action's name, or its description when it has none.</summary>
    public string Title => IsNamed ? Action.Name : Description;

    /// <summary>With a name, the description follows it: "Open inventory  Press I · held 30 ms".</summary>
    public string Detail => !IsNamed ? DescriptionDetail
        : DescriptionDetail.Length == 0 ? Description : $"{Description} · {DescriptionDetail}";

    public override string AccessibleName => $"{Number}. {Title} {Detail}".TrimEnd();

    private string Description => Action.Type switch
    {
        MacroActionType.PressKey => WithTimes(Fill(Strings.MacroEditor_RowPress, KeyNames.Format(Action.Key))),
        MacroActionType.KeyDown => Fill(Strings.MacroEditor_RowKeyDown, KeyNames.Get(Action.Vk)),
        MacroActionType.KeyUp => Fill(Strings.MacroEditor_RowKeyUp, KeyNames.Get(Action.Vk)),
        MacroActionType.Click => WithTimes(Action.ClickCount == 2
            ? MouseButtonBox.DoubleClickName(Action.Vk)
            : MouseButtonBox.ClickName(Action.Vk)),
        MacroActionType.MouseDown => Fill(Strings.MacroEditor_RowButtonDown, ButtonName(Action.Vk)),
        MacroActionType.MouseUp => Fill(Strings.MacroEditor_RowButtonUp, ButtonName(Action.Vk)),
        MacroActionType.MoveTo => Action.Smooth ? Strings.MacroEditor_RowMoveSmoothly : Strings.ActionWindow_Type_MoveTo,
        MacroActionType.MovePath => Strings.ActionWindow_Type_MovePath,
        MacroActionType.Scroll => Action.ScrollDirection switch
        {
            ScrollDirection.Up => Strings.MacroEditor_RowScrollUp,
            ScrollDirection.Left => Strings.MacroEditor_RowScrollLeft,
            ScrollDirection.Right => Strings.MacroEditor_RowScrollRight,
            _ => Strings.MacroEditor_RowScrollDown,
        },
        MacroActionType.PasteText => Strings.ActionWindow_Type_PasteText,
        _ => Action.Type.ToString(),
    };

    private string DescriptionDetail => Action.Type switch
    {
        MacroActionType.PressKey => Fill(Strings.MacroEditor_RowHeld, System.Math.Round(Action.HoldMs)),
        MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp
            or MacroActionType.MoveTo => Fill(Strings.MacroEditor_RowAt, Action.X, Action.Y),
        MacroActionType.MovePath => Plurals.Format("MacroEditor_RowPath", Action.Path.Count, MacroItemViewModel.FormatDuration(Action.DurationMs)),
        MacroActionType.Scroll => Plurals.Format("MacroEditor_RowScrollSteps", Action.ScrollSteps),
        MacroActionType.PasteText => Fill(Strings.MacroEditor_RowQuoted, Shorten(Action.Text)),
        _ => "",
    };

    /// <summary>"Press A 3 times": the action repeated, when it is.</summary>
    private string WithTimes(string what) => Action.Repeat > 1 ? Plurals.Format("MacroEditor_RowTimes", Action.Repeat, what) : what;

    private static string Fill(string text, params object[] values) => string.Format(CultureInfo.CurrentCulture, text, values);

    public void Replace(MacroAction action)
    {
        Action = action;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The button's name ("Left", "Back (X1)"), as in the mouse button list.</summary>
    public static string ButtonName(int vk) => MouseButtonBox.ButtonName(vk);

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 32 ? text[..32] + "…" : text;
    }
}
