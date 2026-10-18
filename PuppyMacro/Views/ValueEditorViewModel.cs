using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Media;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Views;

/// <summary>
/// One value of a component's parameter (an Example, a Default, a call's argument, the value the guided update gives the
/// calls), edited with the controls the app uses for that kind of field (<see cref="ValueEditor"/>): a point with Pick on
/// screen, a number with its unit, a text, a key, a color picked on the screen, images taken on the screen, an area drawn
/// on the screen. It reads and writes the value's JSON (<see cref="ComponentValues"/>).
/// </summary>
public sealed class ValueEditorViewModel : INotifyPropertyChanged
{
    private double _x, _y, _number;
    private string _text = "";
    private HotkeyBinding? _key;
    private string _color = "";
    private List<ConditionImage> _images = new();
    private AreaValue? _area;

    internal ValueEditorViewModel(ParameterType type, NumberUnit? unit, JsonNode? value, ImageStore? store, ValueRule? rule = null)
    {
        Type = type;
        Unit = unit;
        Store = store;
        Rule = rule ?? ValueRule.Any;
        Load(value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ParameterType Type { get; }
    public NumberUnit? Unit { get; }

    /// <summary>The limits a value keeps here (the fields its parameter goes to).</summary>
    internal ValueRule Rule { get; set; }

    internal ImageStore? Store { get; }

    public bool IsPoint => Type == ParameterType.Point;
    public bool IsNumber => Type == ParameterType.Number;
    public bool IsText => Type == ParameterType.Text;
    public bool IsKey => Type == ParameterType.Key;
    public bool IsColor => Type == ParameterType.Color;
    public bool IsImage => Type == ParameterType.Image;
    public bool IsArea => Type == ParameterType.Area;

    public string UnitText => Unit is NumberUnit u ? ComponentChecks.UnitName(u) : "";

    // ---- The values, one per type ----

    public double X
    {
        get => _x;
        set { _x = value; Changed(); }
    }

    public double Y
    {
        get => _y;
        set { _y = value; Changed(); }
    }

    public double Number
    {
        get => _number;
        set { _number = value; Changed(); }
    }

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Changed(); }
    }

    public HotkeyBinding? Key
    {
        get => _key;
        set { _key = value?.Clone(); Changed(); }
    }

    public string Color
    {
        get => _color;
        set { _color = value ?? ""; Changed(); Changed(nameof(Swatch)); }
    }

    public Brush Swatch => MacroJson.IsColor(_color.Trim().ToUpperInvariant()) && ColorConverter.ConvertFromString(_color.Trim()) is System.Windows.Media.Color c
        ? new SolidColorBrush(c)
        : Brushes.Transparent;

    internal List<ConditionImage> Images
    {
        get => _images;
        set { _images = value; Changed(); Changed(nameof(ImagesText)); }
    }

    /// <summary>"2 images", or that none is taken yet.</summary>
    public string ImagesText => _images.Count == 0 ? Strings.Condition_ImageNotTaken : Plurals.Format("Param_Images", _images.Count);

    internal AreaValue? Area
    {
        get => _area;
        set { _area = value; Changed(); Changed(nameof(AreaText)); }
    }

    public string AreaText => _area is { } a
        ? string.Format(CultureInfo.CurrentCulture, Strings.Condition_AreaInfo, a.X, a.Y, a.Width, a.Height)
        : Strings.Condition_AreaNotDrawn;

    // ---- JSON ----

    /// <summary>Shows a value (none: the type's empty one).</summary>
    internal void Load(JsonNode? value)
    {
        switch (ComponentValues.Read(value, Type, ValueRule.Any, out _, null))
        {
            case PointValue p: (_x, _y) = (p.X, p.Y); break;
            case double n: _number = n; break;
            case HotkeyBinding key: _key = key; break;
            case string s when Type == ParameterType.Color: _color = s; break;
            case string s: _text = s; break;
            case List<ConditionImage> images: _images = images; break;
            case AreaValue a: _area = a; break;
        }
        Changed(string.Empty);
    }

    /// <summary>The value as JSON, also when it isn't valid yet (<see cref="Problem"/> says so).</summary>
    internal JsonNode? ToJson() => Type switch
    {
        ParameterType.Point => new JsonObject { ["X"] = Whole(_x), ["Y"] = Whole(_y) },
        ParameterType.Number => double.IsNaN(_number) ? null : JsonValue.Create(_number),
        ParameterType.Text => JsonValue.Create(_text),
        ParameterType.Key => _key is { IsSet: true } key ? ComponentValues.ToJson(key) : null,
        ParameterType.Color => JsonValue.Create(_color.Trim().ToUpperInvariant()),
        ParameterType.Image => ComponentValues.ToJson(_images),
        _ => _area is { } a ? ComponentValues.ToJson(a) : null,
    };

    private static int Whole(double value) => double.IsNaN(value) ? 0 : (int)Math.Round(value);

    /// <summary>Why the value can't be used as it is (another type, outside its limits, missing), or null.</summary>
    public string? Problem
    {
        get
        {
            ComponentValues.Read(ToJson(), Type, Rule, out string? problem, Store == null ? null : Store.Has);
            return problem;
        }
    }

    /// <summary>The value in a few words ("3986, 1056", "100 ms").</summary>
    public string Description => ComponentValues.Describe(ToJson(), Type, Unit);

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
