using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>A row of Import's and Export's lists (<see cref="ImportItemsView"/>, <see cref="ExportItemsView"/>): a check box with the item.</summary>
public abstract class ItemFileRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The user checked or unchecked it.</summary>
    public event Action<ItemFileRow>? CheckedChanged;

    /// <summary>"Loops", "Macros" or "Remaps": the list's groups.</summary>
    public string Group { get; protected init; } = "";

    public string Title { get; protected set; } = "";

    /// <summary>What it does: the card's summary, a remap's apps and note.</summary>
    public string Summary { get; protected set; } = "";

    public bool HasSummary => Summary.Length > 0;

    /// <summary>"Toggle with", before the hotkey's keys; empty for remaps.</summary>
    public string Lead { get; protected set; } = "";

    public List<string> HotkeyParts { get; protected set; } = new();

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
            CheckedChanged?.Invoke(this);
        }
    }

    /// <summary>Sets the box without telling <see cref="CheckedChanged"/> (Select all, a row that can't be imported any more).</summary>
    public void SetChecked(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    /// <summary>The card's lines for a loop, macro or remap.</summary>
    protected void Describe(object item)
    {
        switch (item)
        {
            case LoopDefinition loop:
                var loopRow = new LoopItemViewModel(loop);
                Title = loop.Name;
                Summary = loopRow.Summary;
                Lead = loopRow.ActivationLead;
                HotkeyParts = loopRow.HotkeyParts;
                break;
            case MacroDefinition macro:
                var macroRow = new MacroItemViewModel(macro);
                Title = macro.Name;
                Summary = macroRow.Summary;
                Lead = macroRow.ActivationLead;
                HotkeyParts = macroRow.HotkeyParts;
                break;
            case RemapDefinition remap:
                Title = $"{KeyNames.Format(remap.Source)} → {KeyNames.Format(remap.Target)}";
                Summary = (remap.AppExe ?? "All apps") + (remap.Note.Trim().Length > 0 ? " · " + remap.Note.Trim() : "");
                Lead = "";
                HotkeyParts = new List<string>();
                break;
        }
    }

    protected static string GroupOf(string kind) => kind switch
    {
        "loop" => "Loops",
        "macro" => "Macros",
        "remap" => "Remaps",
        _ => "Other",
    };

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>An item of the file in Import's list: checked when it will be added, with what changes or why it can't.</summary>
public sealed class ImportItemRow : ItemFileRow
{
    internal ImportItemRow(ItemFileEntry entry)
    {
        Index = entry.Index;
        Group = GroupOf(entry.Kind);
        Update(entry);
        SetChecked(entry.Selected);
    }

    /// <summary>Its place in the file.</summary>
    public int Index { get; }

    /// <summary>What changes on import (a new name, a hotkey left out), one per line.</summary>
    public string Notes { get; private set; } = "";

    public bool HasNotes => Notes.Length > 0;

    /// <summary>Why it can't be imported.</summary>
    public string Problem { get; private set; } = "";

    public bool HasProblem => Problem.Length > 0;

    public bool CanImport => !HasProblem;

    /// <summary>The same item read again with the user's current choices: its notes and problem can change.</summary>
    internal void Update(ItemFileEntry entry)
    {
        if (entry.Item != null)
            Describe(entry.Item);
        else
            Title = entry.Title;
        Notes = string.Join("\n", entry.Notes);
        Problem = entry.Problem ?? "";
        if (HasProblem && IsChecked)
            SetChecked(false);
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One of the user's loops, macros or remaps in Export's list.</summary>
public sealed class ExportItemRow : ItemFileRow
{
    internal ExportItemRow(object item, Guid id)
    {
        Item = item;
        Id = id;
        Group = GroupOf(item switch
        {
            LoopDefinition => "loop",
            MacroDefinition => "macro",
            _ => "remap",
        });
        Describe(item);
        SearchText = $"{Title} {Summary} {string.Join(" ", HotkeyParts)} {(item is RemapDefinition r ? r.Note : "")}";
    }

    internal object Item { get; }

    public Guid Id { get; }

    /// <summary>What Search looks in.</summary>
    public string SearchText { get; }
}
