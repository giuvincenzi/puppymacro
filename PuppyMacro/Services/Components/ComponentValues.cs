using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services.Components;

/// <summary>A point on the screen, physical pixels.</summary>
internal readonly record struct PointValue(int X, int Y);

/// <summary>A part of the screen, physical pixels.</summary>
internal readonly record struct AreaValue(int X, int Y, int Width, int Height);

/// <summary>
/// Cells all alike in rows (<see cref="ParameterType.Grid"/>): the same point of the first cell (top left) and of the last
/// (bottom right), and how many columns and rows. Cell n is counted row by row from the top left, starting at 1.
/// </summary>
internal readonly record struct GridValue(PointValue First, PointValue Last, int Columns, int Rows)
{
    /// <summary>The most columns, and the most rows, a grid can have.</summary>
    public const int MaxSide = 1000;

    /// <summary>The most cells a grid can have.</summary>
    public const int MaxCells = 10_000;

    public int Cells => Columns * Rows;

    /// <summary>
    /// The point of cell <paramref name="cell"/> (1 to <see cref="Cells"/>), or null outside the grid. The step from one cell
    /// to the next is the distance from the first to the last over the steps between them, so it can have decimals (73.5);
    /// the distance from the first cell is rounded to the nearest pixel, a half away from zero, so a cell is the same
    /// pixels wherever the grid is on the desktop (also at negative coordinates).
    /// </summary>
    public PointValue? PointOf(int cell)
    {
        if (cell < 1 || cell > Cells)
            return null;
        int column = (cell - 1) % Columns, row = (cell - 1) / Columns;
        return new PointValue(Along(First.X, Last.X, column, Columns), Along(First.Y, Last.Y, row, Rows));
    }

    private static int Along(int first, int last, int index, int count) =>
        count == 1 ? first : first + (int)Math.Round((double)(last - first) * index / (count - 1), MidpointRounding.AwayFromZero);
}

/// <summary>
/// What a value of a parameter must be: the limits of the fields it goes to, joined. A parameter's rule starts from its type
/// and unit (<see cref="ComponentValues.BaseRule"/>) and takes the rule of every field bound to it and of every parameter it
/// passes its value on to (<see cref="ComponentSignatures"/>).
/// </summary>
internal sealed record ValueRule
{
    /// <summary>A number's limits (a Number's value).</summary>
    public (double Min, double Max) Range { get; init; } = (double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>A number without decimals.</summary>
    public bool Whole { get; init; }

    /// <summary>A text that isn't empty.</summary>
    public bool NotEmpty { get; init; }

    /// <summary>A text with more than spaces: what a Text condition looks for (<see cref="MacroJson"/>'s rule for it).</summary>
    public bool NotBlank { get; init; }

    /// <summary>A key of the keyboard, not a mouse button.</summary>
    public bool Keyboard { get; init; }

    /// <summary>A key alone, without Ctrl, Alt, Shift or Win.</summary>
    public bool KeyAlone { get; init; }

    public static readonly ValueRule Any = new();

    /// <summary>Both rules: the tighter limits.</summary>
    public ValueRule And(ValueRule other) => new()
    {
        Range = (Math.Max(Range.Min, other.Range.Min), Math.Min(Range.Max, other.Range.Max)),
        Whole = Whole || other.Whole,
        NotEmpty = NotEmpty || other.NotEmpty,
        NotBlank = NotBlank || other.NotBlank,
        Keyboard = Keyboard || other.Keyboard,
        KeyAlone = KeyAlone || other.KeyAlone,
    };

    public static ValueRule Number((double Min, double Max) range, bool whole = false) => new() { Range = range, Whole = whole };
}

/// <summary>
/// The values of parameters and arguments: read from their JSON for a type (<see cref="Read"/>), checked against a rule,
/// written back (<see cref="ToJson"/>) and said in a few words (<see cref="Describe"/>). The one place that knows how each
/// type is written.
/// </summary>
internal static class ComponentValues
{
    /// <summary>The rule of a type and unit before any field: the limits every field of that kind has.</summary>
    public static ValueRule BaseRule(ParameterType type, NumberUnit? unit) => type switch
    {
        ParameterType.Number => unit switch
        {
            NumberUnit.Pixels => ValueRule.Number(MacroJson.Coordinate, whole: true),
            NumberUnit.Ms => ValueRule.Number((0, MacroJson.MaxDelayMs)),
            NumberUnit.Seconds => ValueRule.Number((0, MacroJson.WaitSeconds.Max)),
            NumberUnit.Count => ValueRule.Number((0, MacroJson.Repeat.Max), whole: true),
            NumberUnit.Percent => ValueRule.Number((0, 100), whole: true),
            _ => ValueRule.Any,
        },
        _ => ValueRule.Any,
    };

    /// <summary>
    /// Reads <paramref name="node"/> as a value of <paramref name="type"/> that keeps <paramref name="rule"/>: a
    /// <see cref="PointValue"/>, a double, a string, a <see cref="HotkeyBinding"/>, a color string, a list of
    /// <see cref="ConditionImage"/> or an <see cref="AreaValue"/>. Null, with why, when it isn't one.
    /// </summary>
    /// <param name="hasImage">Whether PuppyMacro has an image; null skips that check.</param>
    public static object? Read(JsonNode? node, ParameterType type, ValueRule rule, out string? problem, Func<string, bool>? hasImage = null)
    {
        problem = null;
        try
        {
            switch (type)
            {
                case ParameterType.Point:
                {
                    if (!Object(node, out var o, "X", "Y") || !Whole(o["X"], out int x) || !Whole(o["Y"], out int y))
                        return Fail(out problem, Strings.Code_ValuePoint);
                    if (!In(x, MacroJson.Coordinate) || !In(y, MacroJson.Coordinate))
                        return Fail(out problem, Between(MacroJson.Coordinate));
                    return new PointValue(x, y);
                }
                case ParameterType.Number:
                {
                    if (!Numeric(node, out double n))
                        return Fail(out problem, Strings.Code_ValueNumber);
                    if (rule.Whole && n != Math.Floor(n))
                        return Fail(out problem, Strings.Code_ValueWhole);
                    if (n < rule.Range.Min || n > rule.Range.Max)
                        return Fail(out problem, rule.Range.Min > rule.Range.Max ? Strings.Code_ValueNoRoom : Between(rule.Range));
                    return n;
                }
                case ParameterType.Text:
                {
                    if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                        return Fail(out problem, Strings.Code_ValueText);
                    string text = v.GetValue<string>();
                    if (rule.NotEmpty && text.Length == 0)
                        return Fail(out problem, Strings.Code_ValueTextEmpty);
                    if (rule.NotBlank && string.IsNullOrWhiteSpace(text))
                        return Fail(out problem, Strings.Code_EnterConditionText);
                    return text;
                }
                case ParameterType.Key:
                {
                    if (node is not JsonObject)
                        return Fail(out problem, Strings.Code_ValueKey);
                    var key = node.Deserialize<HotkeyBinding>(CodeJson.StrictOptions);
                    if (key == null || !CodeJson.IsKeyCode(key.Vk))
                        return Fail(out problem, Strings.Code_ValueKey);
                    if (rule.Keyboard && KeyNames.IsMouse(key.Vk))
                        return Fail(out problem, Strings.Code_ValueKeyboard);
                    if (rule.KeyAlone && key.HasModifiers)
                        return Fail(out problem, Strings.Code_ValueKeyAlone);
                    return key;
                }
                case ParameterType.Color:
                {
                    if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String || !MacroJson.IsColor(v.GetValue<string>()))
                        return Fail(out problem, Strings.Code_ConditionColor);
                    return v.GetValue<string>();
                }
                case ParameterType.Image:
                {
                    if (node is not JsonArray array || array.Count == 0)
                        return Fail(out problem, Strings.Code_ValueImage);
                    var images = new List<ConditionImage>();
                    foreach (JsonNode? item in array)
                    {
                        if (!Object(item, out var o, "Id", "X", "Y") || o["Id"] is not JsonValue idValue
                            || idValue.GetValueKind() != JsonValueKind.String || !Whole(o["X"], out int x) || !Whole(o["Y"], out int y))
                            return Fail(out problem, Strings.Code_ValueImage);
                        string id = idValue.GetValue<string>();
                        if (!MacroJson.IsImageId(id))
                            return Fail(out problem, Strings.Code_TakeImage);
                        if (hasImage != null && !hasImage(id))
                            return Fail(out problem, Strings.Code_ImageMissing);
                        if (!In(x, MacroJson.Coordinate) || !In(y, MacroJson.Coordinate))
                            return Fail(out problem, Between(MacroJson.Coordinate));
                        images.Add(new ConditionImage { Id = id, X = x, Y = y });
                    }
                    return images;
                }
                case ParameterType.Area:
                {
                    if (!Object(node, out var o, "X", "Y", "Width", "Height") || !Whole(o["X"], out int x) || !Whole(o["Y"], out int y)
                        || !Whole(o["Width"], out int w) || !Whole(o["Height"], out int h))
                        return Fail(out problem, Strings.Code_ValueArea);
                    if (!In(x, MacroJson.Coordinate) || !In(y, MacroJson.Coordinate))
                        return Fail(out problem, Between(MacroJson.Coordinate));
                    if (!In(w, MacroJson.AreaSize) || !In(h, MacroJson.AreaSize))
                        return Fail(out problem, Between(MacroJson.AreaSize));
                    return new AreaValue(x, y, w, h);
                }
                case ParameterType.Grid:
                {
                    if (!Object(node, out var o, "First", "Last", "Columns", "Rows")
                        || Read(o["First"], ParameterType.Point, ValueRule.Any, out _) is not PointValue first
                        || Read(o["Last"], ParameterType.Point, ValueRule.Any, out _) is not PointValue last
                        || !Whole(o["Columns"], out int columns) || !Whole(o["Rows"], out int rows))
                        return Fail(out problem, Strings.Code_ValueGrid);
                    if (columns < 1 || rows < 1 || columns > GridValue.MaxSide || rows > GridValue.MaxSide
                        || (long)columns * rows > GridValue.MaxCells)
                        return Fail(out problem, string.Format(CultureInfo.CurrentCulture, Strings.Code_GridSize, GridValue.MaxSide, GridValue.MaxCells));
                    if (columns == 1 && first.X != last.X)
                        return Fail(out problem, Strings.Code_GridOneColumn);
                    if (rows == 1 && first.Y != last.Y)
                        return Fail(out problem, Strings.Code_GridOneRow);
                    return new GridValue(first, last, columns, rows);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A node of another kind than asked for (a string where a number is…): not a value of this type.
        }
        return Fail(out problem, type switch
        {
            ParameterType.Point => Strings.Code_ValuePoint,
            ParameterType.Number => Strings.Code_ValueNumber,
            ParameterType.Text => Strings.Code_ValueText,
            ParameterType.Key => Strings.Code_ValueKey,
            ParameterType.Color => Strings.Code_ConditionColor,
            ParameterType.Image => Strings.Code_ValueImage,
            ParameterType.Grid => Strings.Code_ValueGrid,
            _ => Strings.Code_ValueArea,
        });
    }

    /// <summary>A value read by <see cref="Read"/> written as its JSON.</summary>
    public static JsonNode ToJson(object value) => value switch
    {
        PointValue p => new JsonObject { ["X"] = p.X, ["Y"] = p.Y },
        double n => JsonValue.Create(n),
        int n => JsonValue.Create(n),
        string s => JsonValue.Create(s),
        HotkeyBinding key => JsonSerializer.SerializeToNode(key, CodeJson.Options)!,
        List<ConditionImage> images => new JsonArray(images.Select(i => (JsonNode)new JsonObject { ["Id"] = i.Id, ["X"] = i.X, ["Y"] = i.Y }).ToArray()),
        AreaValue a => new JsonObject { ["X"] = a.X, ["Y"] = a.Y, ["Width"] = a.Width, ["Height"] = a.Height },
        GridValue g => new JsonObject { ["First"] = ToJson(g.First), ["Last"] = ToJson(g.Last), ["Columns"] = g.Columns, ["Rows"] = g.Rows },
        _ => throw new ArgumentException($"Not a parameter's value: {value.GetType().Name}", nameof(value)),
    };

    /// <summary>
    /// A value in a few words, in the language of the moment ("3986, 1056", "100 ms", "Ctrl + S", "2 images"): the editor's
    /// rows and the Debug log (which runs it in English).
    /// </summary>
    public static string Describe(JsonNode? node, ParameterType type, NumberUnit? unit)
    {
        object? value = Read(node, type, ValueRule.Any, out _);
        return value switch
        {
            PointValue p => string.Format(CultureInfo.CurrentCulture, "{0}, {1}", p.X, p.Y),
            double n => unit switch
            {
                NumberUnit.Ms => string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", n, Strings.Action_UnitMs),
                NumberUnit.Seconds => string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", n, Strings.Action_UnitS),
                NumberUnit.Percent => string.Format(CultureInfo.CurrentCulture, "{0:0}%", n),
                _ => n.ToString("0.##", CultureInfo.CurrentCulture),
            },
            string s => s,
            HotkeyBinding key => KeyNames.Format(key),
            List<ConditionImage> images => Plurals.Format("Param_Images", images.Count),
            AreaValue a => string.Format(CultureInfo.CurrentCulture, "{0}, {1}, {2} × {3}", a.X, a.Y, a.Width, a.Height),
            GridValue g => string.Format(CultureInfo.CurrentCulture, Strings.Param_GridValue, g.Columns, g.Rows, g.First.X, g.First.Y),
            _ => "",
        };
    }

    /// <summary>The value an argument written as <c>{"From": …}</c> or <c>{"Found": …}</c> refers to, or null for a value.</summary>
    public static ArgumentReference? Reference(JsonNode? argument)
    {
        if (argument is not JsonObject o)
            return null;
        if (o.ContainsKey("From"))
        {
            string? from = o["From"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            return new ArgumentReference(from, null, 0, 0, from != null && o.Count == 1);
        }
        if (o.ContainsKey("Found"))
        {
            Guid? id = o["Found"] is JsonValue f && f.GetValueKind() == JsonValueKind.String && Guid.TryParse(f.GetValue<string>(), out Guid g) ? g : null;
            int dx = 0, dy = 0;
            bool ok = o.All(p => p.Key is "Found" or "OffsetX" or "OffsetY")
                      && (!o.ContainsKey("OffsetX") || Whole(o["OffsetX"], out dx))
                      && (!o.ContainsKey("OffsetY") || Whole(o["OffsetY"], out dy));
            return new ArgumentReference(null, id, dx, dy, ok && id != null);
        }
        return null;
    }

    public static JsonObject FromArgument(string parameter) => new() { ["From"] = parameter };

    public static JsonObject FoundArgument(Guid condition, int offsetX, int offsetY)
    {
        var o = new JsonObject { ["Found"] = condition.ToString() };
        if (offsetX != 0)
            o["OffsetX"] = offsetX;
        if (offsetY != 0)
            o["OffsetY"] = offsetY;
        return o;
    }

    private static bool Object(JsonNode? node, out JsonObject o, params string[] keys)
    {
        o = node as JsonObject ?? new JsonObject();
        return node is JsonObject && o.Count == keys.Length && keys.All(o.ContainsKey);
    }

    internal static bool Whole(JsonNode? node, out int value)
    {
        value = 0;
        if (!Numeric(node, out double d)
            || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            return false;
        value = (int)d;
        return true;
    }

    /// <summary>
    /// A JSON number as a double, whatever made the node: one read from text, or one made in memory from an int or a double
    /// (whose TryGetValue gives only its own type).
    /// </summary>
    internal static bool Numeric(JsonNode? node, out double value)
    {
        value = 0;
        // A number made in memory can be infinite or NaN, which JSON can't write: not a value (and its text would throw).
        if (node is JsonValue made && ((made.TryGetValue(out double d) && !double.IsFinite(d)) || (made.TryGetValue(out float f) && !float.IsFinite(f))))
            return false;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
               && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool In(double value, (double Min, double Max) range) => value >= range.Min && value <= range.Max;

    private static string Between((double Min, double Max) range) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Code_Between,
            range.Min.ToString("0.##", CultureInfo.CurrentCulture), range.Max.ToString("0.##", CultureInfo.CurrentCulture));

    private static object? Fail(out string? problem, string message)
    {
        problem = message;
        return null;
    }
}

/// <summary>
/// An argument that refers to something instead of being a value: a parameter of the component around the call
/// (<see cref="From"/>), or where a condition around it found something (<see cref="Found"/>, plus an offset).
/// <see cref="WellFormed"/>: written as it must be.
/// </summary>
internal sealed record ArgumentReference(string? From, Guid? Found, int OffsetX, int OffsetY, bool WellFormed);
