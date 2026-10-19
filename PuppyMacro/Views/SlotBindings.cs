using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Views;

/// <summary>
/// Where one field's value comes from, inside a component (approved: as the If's Wait card, a list before the field):
/// "A value" (the field itself) or "From Slot", one of the component's parameters of the field's type and unit. Shown only
/// when the component has such a parameter; the field shows only with "A value".
/// </summary>
public sealed class SlotChoice : INotifyPropertyChanged
{
    private readonly List<ComponentParameter> _matching;
    private int _index;

    internal SlotChoice(string slot, IEnumerable<ComponentParameter> matching, string? bound)
    {
        Slot = slot;
        _matching = matching.ToList();
        _index = bound == null ? 0 : _matching.FindIndex(p => p.Name == bound) + 1;
    }

    /// <summary>A field that can't come from a parameter here: nothing to choose.</summary>
    internal static SlotChoice None(string slot) => new(slot, Array.Empty<ComponentParameter>(), null);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Slot { get; }

    public IReadOnlyList<string> Choices =>
        new[] { Strings.Param_AValue }.Concat(_matching.Select(p => string.Format(CultureInfo.CurrentCulture,
            p.Type == ParameterType.Grid ? Strings.Action_PositionCell : Strings.Param_FromParameter, p.Name))).ToList();

    /// <summary>The parameter chosen is a Grid: the point is one of its cells (a Cell card shows).</summary>
    public bool IsGrid => _index > 0 && _matching[_index - 1].Type == ParameterType.Grid;

    public int Index
    {
        get => _index;
        set
        {
            int index = Math.Clamp(value, 0, _matching.Count);
            if (index == _index)
                return;
            _index = index;
            Changed();
            Changed(nameof(IsBound));
            Changed(nameof(ShowValue));
            Changed(nameof(Parameter));
            Changed(nameof(IsGrid));
        }
    }

    /// <summary>The component has a parameter that fits: the list shows.</summary>
    public bool Show => _matching.Count > 0;

    public bool IsBound => _index > 0;

    /// <summary>The field itself shows (its own value).</summary>
    public bool ShowValue => _index == 0;

    /// <summary>The parameter chosen, or null for the field's own value.</summary>
    public string? Parameter => _index > 0 ? _matching[_index - 1].Name : null;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The choices of every field of one action or condition that can come from a component's parameter (its slots,
/// <see cref="ComponentSlots"/>), by slot name, for the XAML ("Slots.HoldMs.Index"). Outside a component (a macro, a loop)
/// every one is <see cref="SlotChoice.None"/>: no list shows.
/// </summary>
public sealed class SlotBindings
{
    private readonly Dictionary<string, SlotChoice> _choices = new();

    public static SlotBindings None { get; } = new(Array.Empty<ComponentSlot>(), null, null);

    internal SlotBindings(IEnumerable<ComponentSlot> slots, IReadOnlyList<ComponentParameter>? parameters, Dictionary<string, string>? bind)
    {
        if (parameters == null)
            return;
        foreach (var slot in slots)
        {
            // A point can also be a cell of a Grid parameter (ComponentSlots.Read).
            var matching = parameters.Where(p => (p.Type == slot.Type && (p.Type != ParameterType.Number || p.Unit == slot.Unit))
                                                 || (slot.Name == ComponentSlots.Point && p.Type == ParameterType.Grid));
            _choices[slot.Name] = new SlotChoice(slot.Name, matching, bind?.GetValueOrDefault(slot.Name));
        }
    }

    public SlotChoice this[string slot] => _choices.TryGetValue(slot, out var choice) ? choice : SlotChoice.None(slot);

    /// <summary>Every field's choice (a change of any of them changes what the model will be).</summary>
    internal IEnumerable<SlotChoice> All => _choices.Values;

    public SlotChoice DelayMs => this[ComponentSlots.DelayMs];
    public SlotChoice Key => this[ComponentSlots.Key];
    public SlotChoice HoldMs => this[ComponentSlots.HoldMs];
    public SlotChoice Repeat => this[ComponentSlots.Repeat];
    public SlotChoice RepeatPauseMs => this[ComponentSlots.RepeatPauseMs];
    public SlotChoice ScrollSteps => this[ComponentSlots.ScrollSteps];
    public SlotChoice Text => this[ComponentSlots.Text];
    public SlotChoice WaitSeconds => this[ComponentSlots.WaitSeconds];
    public SlotChoice Point => this[ComponentSlots.Point];
    public SlotChoice Cell => this[ComponentSlots.Cell];
    public SlotChoice Color => this[ComponentSlots.Color];
    public SlotChoice Tolerance => this[ComponentSlots.Tolerance];
    public SlotChoice Images => this[ComponentSlots.Images];
    public SlotChoice Similarity => this[ComponentSlots.Similarity];
    public SlotChoice Area => this[ComponentSlots.Area];

    /// <summary>
    /// The Bind of the model as it is now: the chosen parameter of each slot it uses (<paramref name="slots"/>, those of the
    /// action or condition as it will be saved), and <paramref name="extra"/> (a slot chosen elsewhere: Position); null for none.
    /// </summary>
    internal Dictionary<string, string>? ToBind(IEnumerable<ComponentSlot> slots, IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var bind = new Dictionary<string, string>();
        foreach (var slot in slots.Where(s => s.Applies))
            if (_choices.TryGetValue(slot.Name, out var choice) && choice.Parameter is string parameter)
                bind[slot.Name] = parameter;
        foreach (var (slot, parameter) in extra ?? Enumerable.Empty<KeyValuePair<string, string>>())
            bind[slot] = parameter;
        return bind.Count == 0 ? null : bind;
    }
}
