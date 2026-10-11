using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Bindable wrapper around a <see cref="MacroDefinition"/> for the Macros list and overlay mode.</summary>
public sealed class MacroItemViewModel : INotifyPropertyChanged, IListItem
{
    private bool _isRunning;
    private bool _dropBefore;
    private bool _dropAfter;
    private bool _isDragging;

    public MacroItemViewModel(MacroDefinition definition) => Definition = definition;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user flips the Enabled switch.</summary>
    public event Action<MacroItemViewModel>? EnabledChanged;

    public MacroDefinition Definition { get; }

    public Guid Id => Definition.Id;
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
    public bool ShowInOverlay => Definition.ShowInOverlay;
    public bool HideInOverlayWhenDisabled => Definition.HideInOverlayWhenDisabled;
    public bool OverlayClickPassesThrough => Definition.OverlayClickPassesThrough;
    public string? AppExe => Definition.AppExe;
    public bool HasApp => Definition.AppExe != null;
    public bool IsItemEnabled => Definition.Enabled;

    public string Summary
    {
        get
        {
            int count = Definition.Actions.Count;
            string repeat = Definition.Repeat switch
            {
                RepeatMode.Loop => "loop",
                RepeatMode.Times => $"{Definition.RepeatCount} times",
                _ => "once",
            };
            string speed = Definition.Speed == 1 ? "" : $", speed ×{Definition.Speed.ToString(CultureInfo.CurrentCulture)}";
            return $"{count} {(count == 1 ? "action" : "actions")}, {FormatDuration(Definition.TotalMs)}, repeat {repeat}{speed}";
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
    public string Status => IsRunning ? "Running" : (Definition.Enabled ? "Idle" : "Disabled");

    /// <summary>Start works only when enabled; a running one can always be stopped.</summary>
    public bool CanStartStop => Definition.Enabled || IsRunning;

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

    public bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging != value) { _isDragging = value; OnPropertyChanged(); } }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string FormatDuration(double ms)
    {
        if (ms < 1000)
            return $"{Math.Round(ms)} ms";
        if (ms < 60_000)
            return $"{(ms / 1000).ToString("0.#", CultureInfo.CurrentCulture)} s";
        return $"{(ms / 60_000).ToString("0.#", CultureInfo.CurrentCulture)} min";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
