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
    public List<string> HotkeyParts => KeyNames.Parts(Definition.Hotkey);
    public string HotkeyText => KeyNames.Format(Definition.Hotkey);
    public bool HasHotkey => Definition.Hotkey is { IsSet: true };

    /// <summary>"Toggle with" before the key caps, or "Toggle, no hotkey".</summary>
    public string ActivationText => HasHotkey ? $"{ModeText} with" : $"{ModeText}, no hotkey";
    public bool IsToggleMode => Definition.Mode == ActivationMode.Toggle;
    public bool IsHoldMode => Definition.Mode == ActivationMode.Hold;
    public FloatingButton FloatingButton => Definition.FloatingButton;
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
            OnPropertyChanged(nameof(StartStopText));
        }
    }

    public bool IsNotRunning => !_isRunning;
    public string Status => IsRunning ? "Running" : (Definition.Enabled ? "Idle" : "Disabled");
    public string StartStopText => IsRunning ? "Stop" : "Play";

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
