using System;
using System.Collections.Generic;
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

    public List<MacroAction> Actions { get; set; } = new();

    public LibraryComponent Clone()
    {
        var copy = (LibraryComponent)MemberwiseClone();
        copy.Actions = Actions.ConvertAll(a => a.Clone());
        return copy;
    }
}
