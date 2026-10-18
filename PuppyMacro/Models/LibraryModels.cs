using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PuppyMacro.Models;

/// <summary>What kind of app a <see cref="LibraryCollection"/> is for (the first part of a component's structured name).</summary>
public enum LibraryDomain
{
    Game,
    Office,
    Browser,
    Creative,
    Dev,
    Chat,
    System,
    Other,
}

/// <summary>What a <see cref="LibraryComponent"/> does (the third part of its structured name).</summary>
public enum ComponentPurpose
{
    /// <summary>Recognizes something on the screen (an If).</summary>
    Find,

    /// <summary>Waits until something shows or goes away (an If).</summary>
    Wait,

    /// <summary>Reads a text (an If).</summary>
    Read,

    /// <summary>Tells which screen or state the app is in (an If).</summary>
    State,

    /// <summary>A point to click or move to.</summary>
    Point,

    /// <summary>Does an operation: a sequence of actions.</summary>
    Act,
}

/// <summary>
/// The screen a collection was made on: the monitor its points are on (bounds on the desktop, physical pixels) and Windows'
/// scale. Points are screen coordinates, so another screen configuration needs them changed (a warning, never a conversion).
/// </summary>
public sealed class LibraryScreen
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Windows' scale of that monitor, in percent (100, 125, 150…).</summary>
    public int ScalePercent { get; set; } = 100;

    public LibraryScreen Clone() => (LibraryScreen)MemberwiseClone();

    public bool SameAs(LibraryScreen? other) =>
        other != null && X == other.X && Y == other.Y && Width == other.Width && Height == other.Height && ScalePercent == other.ScalePercent;
}

/// <summary>
/// A collection of components for one kind of app and screen, shared whole (export and import never split it). Its Id never
/// changes; its content's fingerprint (<c>ComponentLibrary.Fingerprint</c>) tells two copies apart.
/// </summary>
public sealed class LibraryCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public LibraryDomain Domain { get; set; } = LibraryDomain.Other;

    /// <summary>The app it is for, as <see cref="MacroDefinition.AppExe"/> ("Diablo IV.exe"), or null for any app.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppExe { get; set; }

    /// <summary>The screen it was made on, or null when not known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LibraryScreen? Screen { get; set; }

    public List<LibraryComponent> Components { get; set; } = new();

    public LibraryCollection Clone()
    {
        var copy = (LibraryCollection)MemberwiseClone();
        copy.Screen = Screen?.Clone();
        copy.Components = Components.ConvertAll(c => c.Clone());
        return copy;
    }
}

/// <summary>
/// A piece of macro that works, kept to be linked in macros: one or more actions (a point, an If, a sequence). A macro row
/// of type <see cref="MacroActionType.Component"/> links it and plays it as it is in the Library. Its Id never changes, and it
/// never moves to another collection.
/// </summary>
public sealed class LibraryComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name people read ("Remove one affix").</summary>
    public string Name { get; set; } = "";

    public ComponentPurpose Purpose { get; set; } = ComponentPurpose.Act;

    /// <summary>The last part of its structured name, in English: lowercase letters, digits and '_' ("remove_one_affix").</summary>
    public string Concept { get; set; } = "";

    /// <summary>A short English description, written by the AI app that made it (optional).</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Its signature: the values a call gives it (<see cref="MacroAction.Arguments"/>), each bound to fields of its actions
    /// (<see cref="MacroAction.Bind"/>). Empty: it takes none.
    /// </summary>
    public List<ComponentParameter> Parameters { get; set; } = new();

    /// <summary>What it finds, when it can be used as a condition (<see cref="ConditionKind.Component"/>); null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ComponentReturns? Returns { get; set; }

    public List<MacroAction> Actions { get; set; } = new();

    public LibraryComponent Clone()
    {
        var copy = (LibraryComponent)MemberwiseClone();
        copy.Parameters = Parameters.ConvertAll(p => p.Clone());
        copy.Returns = Returns?.Clone();
        copy.Actions = Actions.ConvertAll(a => a.Clone());
        return copy;
    }
}

/// <summary>The type of a component's parameter: the kind of field it gives a value to.</summary>
public enum ParameterType
{
    /// <summary>A point on the screen, {"X", "Y"} in physical pixels.</summary>
    Point,

    /// <summary>A number, with its <see cref="NumberUnit"/>.</summary>
    Number,

    Text,

    /// <summary>A key with its modifiers, as a hotkey is written.</summary>
    Key,

    /// <summary>"#RRGGBB".</summary>
    Color,

    /// <summary>The images of an Image condition: [{"Id", "X", "Y"}].</summary>
    Image,

    /// <summary>A part of the screen, {"X", "Y", "Width", "Height"}.</summary>
    Area,
}

/// <summary>What a <see cref="ParameterType.Number"/> counts: a number can only go where the same unit does.</summary>
public enum NumberUnit
{
    Pixels,
    Ms,
    Seconds,
    Count,
    Percent,
}

/// <summary>
/// One parameter of a component. Required when it has no <see cref="Default"/>. <see cref="Example"/> is the value its bound
/// fields hold, what the editor shows and a Test plays.
/// </summary>
public sealed class ComponentParameter
{
    /// <summary>Letters, digits and '_', starting with a letter ("Slot").</summary>
    public string Name { get; set; } = "";

    public ParameterType Type { get; set; }

    /// <summary>Number only: what it counts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NumberUnit? Unit { get; set; }

    public JsonNode? Example { get; set; }

    /// <summary>The value a call that leaves it out gives; null: every call gives one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Default { get; set; }

    /// <summary>A short English description, for the AI apps (optional).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Description { get; set; } = "";

    [JsonIgnore]
    public bool Required => Default == null;

    public ComponentParameter Clone()
    {
        var copy = (ComponentParameter)MemberwiseClone();
        copy.Example = Example?.DeepClone();
        copy.Default = Default?.DeepClone();
        return copy;
    }

    /// <summary>The same name, type, unit, example, default and description.</summary>
    public bool SameAs(ComponentParameter other) =>
        Name == other.Name && Type == other.Type && Unit == other.Unit && Description == other.Description
        && JsonNode.DeepEquals(Example, other.Example) && JsonNode.DeepEquals(Default, other.Default);
}

/// <summary>What a component finds: one of its conditions (<see cref="Found"/>), true or not, and the place it finds.</summary>
public sealed class ComponentReturns
{
    /// <summary>The <see cref="MacroCondition.Id"/> of one of the component's conditions.</summary>
    public Guid Found { get; set; }

    public ComponentReturns Clone() => (ComponentReturns)MemberwiseClone();
}
