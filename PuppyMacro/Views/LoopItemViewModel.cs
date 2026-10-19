using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Bindable wrapper around a <see cref="LoopDefinition"/>, shared by the main window and overlay mode.</summary>
public sealed class LoopItemViewModel : INotifyPropertyChanged, IListItem
{
    private bool _isRunning;

    public LoopItemViewModel(LoopDefinition definition) => Definition = definition;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user flips the Enabled switch.</summary>
    public event Action<LoopItemViewModel>? EnabledChanged;

    public LoopDefinition Definition { get; }

    public Guid Id => Definition.Id;
    public bool IsItemEnabled => Definition.Enabled;

    public string Name => Definition.Name;
    public string HotkeyText => KeyNames.Format(Definition.Hotkey);
    public bool HasHotkey => Definition.Hotkey is { IsSet: true };

    /// <summary>The card's second line before the hotkey's keys: "Toggle with", or "Toggle, no hotkey".</summary>
    public string ActivationLead => CardTexts.ActivationLead(Definition.Mode, HasHotkey);

    /// <summary>The hotkey's keys on the card, in the accent color (none without a hotkey).</summary>
    public List<string> HotkeyParts => HasHotkey ? KeyNames.Parts(Definition.Hotkey) : new List<string>();

    /// <summary>The item can be opened in its editor (a click on it): no loop or macro runs (the main window sets it).</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set
        {
            if (_canEdit == value)
                return;
            _canEdit = value;
            OnPropertyChanged();
        }
    }
    public bool IsHoldMode => Definition.Mode == ActivationMode.Hold;
    public FloatingButton FloatingButton => Definition.FloatingButton;
    public bool ShowInOverlay => Definition.ShowInOverlay;
    public bool HideInOverlayWhenDisabled => Definition.HideInOverlayWhenDisabled;
    public bool OverlayClickPassesThrough => Definition.OverlayClickPassesThrough;
    public string? AppExe => Definition.AppExe;
    public bool HasApp => Definition.AppExe != null;
    /// <summary>The list's and the grid's group: the app it works in, or All apps.</summary>
    public string GroupName => Definition.AppExe ?? Strings.Remaps_AllApps;
    /// <summary>No hotkey: the lists say so where the keys would be.</summary>
    public bool HasNoHotkey => !HasHotkey;
    /// <summary>Hold mode with a hotkey: the table says "Hold" after the keys.</summary>
    public bool IsHoldWithHotkey => IsHoldMode && HasHotkey;
    /// <summary>The row's accessible name in the lists (ui.md): its name.</summary>
    public string AccessibleName => Name;

    public bool ShowKeyIcon => Definition.Actions.Count == 1 && Definition.Actions[0].Type == ActionType.Key && !Definition.Actions[0].IsClick;
    public bool ShowMouseIcon => Definition.Actions.Count == 1 && Definition.Actions[0].IsClick;
    public bool ShowTextIcon => Definition.Actions.Count == 1 && Definition.Actions[0].Type == ActionType.Text;
    public bool ShowListIcon => Definition.Actions.Count != 1;

    /// <summary>One line describing what the loop does.</summary>
    public string Summary
    {
        get
        {
            var actions = Definition.Actions;
            if (actions.Count == 0)
                return Strings.Loops_NoActions;
            if (actions.Count == 1)
            {
                string single = Describe(actions[0]);
                return char.ToUpper(single[0], CultureInfo.CurrentCulture) + single[1..];
            }
            return Plurals.Format("Loops_ActionsSummary", actions.Count, string.Join(Strings.ListSeparator, actions.Select(Describe)));
        }
    }

    public bool IsLoopEnabled
    {
        get => Definition.Enabled;
        set
        {
            if (Definition.Enabled == value)
                return;
            Definition.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsItemEnabled));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(PanelAccessibleName));
            OnPropertyChanged(nameof(CanStartStop));
            EnabledChanged?.Invoke(this);
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotRunning));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(PanelAccessibleName));
            OnPropertyChanged(nameof(CanStartStop));
        }
    }

    public bool IsNotRunning => !_isRunning;

    private bool _canEdit = true;

    // ---- Drag and drop reordering (main window) ----
    private bool _dropBefore;
    private bool _dropAfter;
    private bool _isDragging;

    /// <summary>Shows the insertion line above the card.</summary>
    public bool DropBefore
    {
        get => _dropBefore;
        set { if (_dropBefore != value) { _dropBefore = value; OnPropertyChanged(); } }
    }

    /// <summary>Shows the insertion line below the card.</summary>
    public bool DropAfter
    {
        get => _dropAfter;
        set { if (_dropAfter != value) { _dropAfter = value; OnPropertyChanged(); } }
    }

    /// <summary>True for the card being dragged (shown faded).</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging != value) { _isDragging = value; OnPropertyChanged(); } }
    }
    public string Status => CardTexts.Status(IsRunning, Definition.Enabled);

    /// <summary>The overlay panel row's accessible name: the name and the state ("Spam, Disabled").</summary>
    public string PanelAccessibleName => AccessibleNames.Join(Name, Status);

    /// <summary>Start works only when enabled; a running one can always be stopped.</summary>
    public bool CanStartStop => Definition.Enabled || IsRunning;

    /// <summary>Call after the definition was edited.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string FormatInterval(LoopAction action)
    {
        string format = action.IntervalUnit switch
        {
            IntervalUnit.Seconds => Strings.Loops_IntervalSeconds,
            IntervalUnit.Minutes => Strings.Loops_IntervalMinutes,
            IntervalUnit.Hours => Strings.Loops_IntervalHours,
            _ => Strings.Loops_IntervalMilliseconds,
        };
        return string.Format(CultureInfo.CurrentCulture, format, action.IntervalValue.ToString("0.##", CultureInfo.CurrentCulture));
    }

    private static string Describe(LoopAction action)
    {
        // Press key: the key or combination ("Left Ctrl + C"); Click: the button ("Left click", "Left double click").
        string what = action.IsClick
            ? action.IsDoubleClick ? MouseButtonBox.DoubleClickName(action.KeyVk) : MouseButtonBox.ClickName(action.KeyVk)
            : KeyNames.Format(action.Key);
        if (action.Type == ActionType.Key && action.HoldDown)
            return string.Format(CultureInfo.CurrentCulture, Strings.Loops_HeldDown, what);

        string interval = FormatInterval(action);
        if (action.Type == ActionType.Key)
            return action.Times > 1
                ? Plurals.Format("Loops_KeyTimesEvery", action.Times, what, interval)
                : string.Format(CultureInfo.CurrentCulture, Strings.Loops_KeyEvery, what, interval);

        string text = TextCut.Shorten(action.Text.Replace("\r", " ").Replace("\n", " ").Trim(), 28);
        return string.Format(CultureInfo.CurrentCulture, Strings.Loops_PastesEvery, text, interval);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The texts shared by the loop and macro cards.</summary>
internal static class CardTexts
{
    /// <summary>The second line before the hotkey's keys: "Toggle with", or "Toggle, no hotkey".</summary>
    public static string ActivationLead(ActivationMode mode, bool hasHotkey) => (mode, hasHotkey) switch
    {
        (ActivationMode.Hold, true) => Strings.Card_HoldWith,
        (ActivationMode.Hold, false) => Strings.Card_HoldNoHotkey,
        (_, true) => Strings.Card_ToggleWith,
        _ => Strings.Card_ToggleNoHotkey,
    };

    /// <summary>The status next to the name: Running, Idle or Disabled.</summary>
    public static string Status(bool running, bool enabled) =>
        running ? Strings.Card_StatusRunning : enabled ? Strings.Card_StatusIdle : Strings.Card_StatusDisabled;
}
