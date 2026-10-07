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

    public string SourceText => KeyNames.Get(Definition.SourceVk);
    public List<string> TargetParts => KeyNames.Parts(Definition.Target);
    public string TargetText => KeyNames.Format(Definition.Target);
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
            EnabledChanged?.Invoke(this);
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
