using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Views;

/// <summary>
/// One argument of a call (a linked row, a Component condition), as <see cref="ArgumentsEditor"/> shows it: a card per
/// parameter of the called component, its name and what it is, and where its value comes from, a list as the If's Wait card
/// (approved: the app's own pattern): A value (the value editor under it), its default when it has one, a parameter of the
/// component around the call (From), or where a condition around it found something (a point: Found, with how far from
/// the center).
/// </summary>
public sealed class ArgumentViewModel : INotifyPropertyChanged
{
    private enum Source { Value, Default, From, Found }

    private readonly List<(Source Source, string? Name, Guid? Condition)> _sources = new();
    private int _index;
    private double _offsetX, _offsetY;

    internal ArgumentViewModel(ComponentParameter parameter, ValueRule rule, JsonNode? argument, IReadOnlyList<ComponentParameter>? outer,
        IReadOnlyDictionary<Guid, string> found, ImageStore? store)
    {
        Parameter = parameter;
        _sources.Add((Source.Value, null, null));
        if (parameter.Default != null)
            _sources.Add((Source.Default, null, null));
        foreach (var own in outer ?? Array.Empty<ComponentParameter>())
            if (own.Type == parameter.Type && (own.Type != ParameterType.Number || own.Unit == parameter.Unit))
                _sources.Add((Source.From, own.Name, null));
        if (parameter.Type == ParameterType.Point)
            foreach (var (id, name) in found)
                _sources.Add((Source.Found, name, id));

        var reference = ComponentValues.Reference(argument);
        Value = new ValueEditorViewModel(parameter.Type, parameter.Unit, reference == null ? argument ?? parameter.Example : parameter.Example, store, rule);
        Value.PropertyChanged += (_, _) => Changed(nameof(Problem));
        _index = argument == null
            ? (parameter.Default != null ? 1 : 0)
            : reference is { From: string from } ? Math.Max(0, _sources.FindIndex(s => s.Source == Source.From && s.Name == from))
            : reference is { Found: Guid condition } ? Math.Max(0, _sources.FindIndex(s => s.Source == Source.Found && s.Condition == condition))
            : 0;
        if (reference is { Found: not null })
            (_offsetX, _offsetY) = (reference.OffsetX, reference.OffsetY);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ComponentParameter Parameter { get; }

    public string Name => Parameter.Name;

    /// <summary>"The inventory slot · Point · every call gives a value", "Number (ms) · 100 ms when left out".</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Parameter.Description))
                parts.Add(Parameter.Description.Trim());
            parts.Add(ComponentChecks.TypeName(Parameter.Type, Parameter.Unit));
            parts.Add(Parameter.Default == null ? Strings.Param_Required
                : string.Format(CultureInfo.CurrentCulture, Strings.Param_DefaultIs, ComponentValues.Describe(Parameter.Default, Parameter.Type, Parameter.Unit)));
            return string.Join(" · ", parts);
        }
    }

    public ValueEditorViewModel Value { get; }

    public IReadOnlyList<string> Choices => _sources.Select(s => s.Source switch
    {
        Source.Value => Strings.Param_AValue,
        Source.Default => string.Format(CultureInfo.CurrentCulture, Strings.Param_ItsDefault, ComponentValues.Describe(Parameter.Default, Parameter.Type, Parameter.Unit)),
        Source.From => string.Format(CultureInfo.CurrentCulture, Strings.Param_FromParameter, s.Name),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Action_PositionFound, s.Name),
    }).ToList();

    /// <summary>More than one source: the list shows.</summary>
    public bool ShowChoices => _sources.Count > 1;

    public int Index
    {
        get => _index;
        set
        {
            _index = Math.Clamp(value, 0, _sources.Count - 1);
            Changed();
            Changed(nameof(ShowValue));
            Changed(nameof(ShowOffset));
            Changed(nameof(Problem));
        }
    }

    public bool ShowValue => _sources[_index].Source == Source.Value;

    public bool ShowOffset => _sources[_index].Source == Source.Found;

    public double OffsetX
    {
        get => _offsetX;
        set { _offsetX = value; Changed(); }
    }

    public double OffsetY
    {
        get => _offsetY;
        set { _offsetY = value; Changed(); }
    }

    /// <summary>The argument as the call writes it; null for the default (left out).</summary>
    internal JsonNode? ToJson()
    {
        var (source, name, condition) = _sources[_index];
        return source switch
        {
            Source.Value => Value.ToJson(),
            Source.Default => null,
            Source.From => ComponentValues.FromArgument(name!),
            _ => ComponentValues.FoundArgument(condition!.Value, Whole(_offsetX), Whole(_offsetY)),
        };
    }

    private static int Whole(double value) => double.IsNaN(value) ? 0 : (int)Math.Round(value);

    /// <summary>Why the argument can't be used as it is ("Slot: write a point…"), or null.</summary>
    public string? Problem => ShowValue && Value.Problem is string problem
        ? string.Format(CultureInfo.CurrentCulture, Strings.Param_ArgumentProblem, Parameter.Name, problem)
        : null;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// The arguments of a call to <paramref name="callee"/>, one per parameter in its order, from the call's
    /// <paramref name="arguments"/>; <paramref name="outer"/>: the parameters of the component around the call (none in a
    /// macro); <paramref name="found"/>: the conditions around it whose place a point can take.
    /// </summary>
    internal static List<ArgumentViewModel> For(LibraryComponent callee, Dictionary<string, JsonNode?>? arguments, ComponentSignatures signatures,
        IReadOnlyList<ComponentParameter>? outer, IReadOnlyDictionary<Guid, string> found, ImageStore? store) =>
        callee.Parameters.Select(p => new ArgumentViewModel(p, signatures.RuleOf(callee.Id, p.Name), arguments?.GetValueOrDefault(p.Name),
            outer, found, store)).ToList();

    /// <summary>The call's arguments from the cards; null when every one is left to its default.</summary>
    internal static Dictionary<string, JsonNode?>? ToArguments(IEnumerable<ArgumentViewModel> cards)
    {
        var result = new Dictionary<string, JsonNode?>();
        foreach (var card in cards)
            if (card.ToJson() is { } value)
                result[card.Name] = value;
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// The arguments a new call starts with: each required parameter its Example, so the call works at once and the user
    /// changes what differs.
    /// </summary>
    internal static Dictionary<string, JsonNode?>? Starting(LibraryComponent callee)
    {
        var result = callee.Parameters.Where(p => p.Required && p.Example != null).ToDictionary(p => p.Name, p => p.Example?.DeepClone());
        return result.Count == 0 ? null : result;
    }
}
