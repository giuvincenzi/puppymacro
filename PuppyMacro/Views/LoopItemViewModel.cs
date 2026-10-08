using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
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
    public string ModeText => Definition.Mode.ToString();
    public string HotkeyText => KeyNames.Format(Definition.Hotkey);
    public bool HasHotkey => Definition.Hotkey is { IsSet: true };

    /// <summary>The card's second line before the hotkey's keys: "Toggle with", or "Toggle, no hotkey".</summary>
    public string ActivationLead => HasHotkey ? $"{ModeText} with" : $"{ModeText}, no hotkey";

    /// <summary>The hotkey's keys on the card, in the accent color (none without a hotkey).</summary>
    public List<string> HotkeyParts => HasHotkey ? KeyNames.Parts(Definition.Hotkey) : new List<string>();

    /// <summary>Edit can be used: no loop or macro runs (the main window sets it).</summary>
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

    public bool ShowKeyIcon => Definition.Actions.Count == 1 && Definition.Actions[0].Type == ActionType.Key;
    public bool ShowTextIcon => Definition.Actions.Count == 1 && Definition.Actions[0].Type == ActionType.Text;
    public bool ShowListIcon => Definition.Actions.Count != 1;

    /// <summary>One line describing what the loop does.</summary>
    public string Summary
    {
        get
        {
            var actions = Definition.Actions;
            if (actions.Count == 0)
                return "No actions";
            if (actions.Count == 1)
            {
                string single = Describe(actions[0]);
                return char.ToUpper(single[0], CultureInfo.CurrentCulture) + single[1..];
            }
            return $"{actions.Count} actions: " + string.Join(", ", actions.Select(Describe));
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
            OnPropertyChanged(nameof(Status));
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
    public string Status => IsRunning ? "Running" : (Definition.Enabled ? "Idle" : "Disabled");

    /// <summary>Start works only when enabled; a running one can always be stopped.</summary>
    public bool CanStartStop => Definition.Enabled || IsRunning;

    /// <summary>Call after the definition was edited.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string FormatInterval(LoopAction action)
    {
        string unit = action.IntervalUnit switch
        {
            IntervalUnit.Seconds => "s",
            IntervalUnit.Minutes => "min",
            IntervalUnit.Hours => "h",
            _ => "ms",
        };
        return $"{action.IntervalValue.ToString("0.##", CultureInfo.CurrentCulture)} {unit}";
    }

    private static string Describe(LoopAction action)
    {
        if (action.Type == ActionType.Key && action.HoldDown)
            return $"{KeyNames.Get(action.KeyVk)} held down";

        string every = "every " + FormatInterval(action);
        if (action.Type == ActionType.Key)
            return $"{KeyNames.Get(action.KeyVk)} {every}";

        string text = action.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 28)
            text = text[..28] + "…";
        return $"pastes \"{text}\" {every}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
