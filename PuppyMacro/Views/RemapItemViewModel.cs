using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
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
    public List<string> TargetParts => KeyNames.Parts(Definition.Target);
    public bool IsAllApps => Definition.AppExe == null;
    public bool IsSpecificApp => Definition.AppExe != null;
    public string ScopeText => Definition.AppExe ?? "All apps";
    public string Note => Definition.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Definition.Note);
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
    public string Status => Definition.Enabled ? "Enabled" : "Disabled";

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
