using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Bindable wrapper around a <see cref="RemapDefinition"/> for the Remap list.</summary>
public sealed class RemapItemViewModel : INotifyPropertyChanged
{
    public RemapItemViewModel(RemapDefinition definition) => Definition = definition;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user flips the Enabled switch.</summary>
    public event Action<RemapItemViewModel>? EnabledChanged;

    public RemapDefinition Definition { get; }

    public string SourceText => KeyNames.Format(Definition.Source);
    public string TargetText => KeyNames.Format(Definition.Target);
    /// <summary>The title's keys, in the accent color: the key or combination pressed, then the one it sends.</summary>
    public List<string> SourceParts => KeyNames.Parts(Definition.Source);
    /// <summary>
    /// No hotkey: Import took it off because it was taken. The remap can't be turned on until the user gives it one in the
    /// editor (the card shows "No hotkey yet" and why, its switch off).
    /// </summary>
    public bool HasNoSource => Definition.Source is not { IsSet: true };
    public bool HasSource => !HasNoSource;
    public List<string> TargetParts => KeyNames.Parts(Definition.Target);
    public bool IsAllApps => Definition.AppExe == null;
    public bool IsSpecificApp => Definition.AppExe != null;
    public string ScopeText => Definition.AppExe ?? Strings.Remaps_AllApps;
    /// <summary>The list's and the grid's group: the app it works in, or All apps.</summary>
    public string GroupName => ScopeText;
    /// <summary>What the lists show as its name: its note, or its keys when it has none.</summary>
    public string Title => AccessibleName;
    public string Note => Definition.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Definition.Note);
    /// <summary>The row's accessible name: its note, or its keys when it has none ("J → C").</summary>
    public string AccessibleName => HasNote ? Definition.Note
        : string.Format(CultureInfo.CurrentCulture, Strings.Ai_RemapTitle, HasNoSource ? Strings.Remaps_NoHotkey : SourceText, TargetText);
    public string SearchText => $"{SourceText} {TargetText} {ScopeText} {Note}";

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
            EnabledChanged?.Invoke(this);
        }
    }

    /// <summary>Shown next to the remap, as the loops' and macros' status.</summary>
    public string Status => Definition.Enabled ? Strings.Card_StatusEnabled : Strings.Card_StatusDisabled;

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
