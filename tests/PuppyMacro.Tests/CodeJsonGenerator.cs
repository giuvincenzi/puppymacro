using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Tests;

/// <summary>
/// Random loops, macros and remaps that the editors could save: every action type, condition kind and field, each value
/// inside the limits of the editors and of the Code views' checks. The same seed gives the same items.
/// </summary>
internal sealed class CodeJsonGenerator
{
    private readonly Random _r;
    private int _names;

    public CodeJsonGenerator(int seed) => _r = new Random(seed);

    private static readonly int[] Keys =
        Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x70, 12))
            .Concat(new[] { 0x08, 0x09, 0x0D, 0x20, 0x25, 0x26, 0x27, 0x28, 0x2E, 0x60, 0x6A }).ToArray();

    private static readonly int[] Mouse = { 1, 2, 4, 5, 6 };
    private static readonly int[] SidedModifiers = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    private T Pick<T>(IReadOnlyList<T> list) => list[_r.Next(list.Count)];
    private bool Coin(int percent = 50) => _r.Next(100) < percent;
    private int Int((double Min, double Max) range) => _r.Next((int)range.Min, (int)range.Max + 1);

    /// <summary>A value of the range: its ends often, a whole number or a number with decimals otherwise.</summary>
    private double Number((double Min, double Max) range, bool whole = false)
    {
        int roll = _r.Next(10);
        if (roll == 0)
            return range.Min;
        if (roll == 1)
            return range.Max;
        double value = range.Min + _r.NextDouble() * (range.Max - range.Min);
        return whole || roll < 6 ? Math.Clamp(Math.Round(value), range.Min, range.Max) : Math.Clamp(Math.Round(value, 3), range.Min, range.Max);
    }

    /// <summary>A text with quotes, backslashes, line breaks, accents, a right-to-left word and an emoji.</summary>
    private string Text()
    {
        string[] parts = { "hello", "\"quoted\"", "back\\slash", "line\nbreak", "tab\there", "città", "مرحبا", "日本語", "😀", "<tag>", "&amp;", "{0}" };
        return string.Join(" ", Enumerable.Range(0, _r.Next(1, 4)).Select(_ => Pick(parts)));
    }

    private string Name() => $"Name {++_names} {Pick(new[] { "Boss", "città", "日本", "😀", "a\"b" })}";

    private ModifierSide Side() => (ModifierSide)_r.Next(3);

    private void Modifiers(KeyModifierFields f)
    {
        f.Ctrl = Coin(30);
        f.Alt = Coin(20);
        f.Shift = Coin(30);
        f.Win = Coin(10);
        if (f.Ctrl) f.CtrlSide = Side();
        if (f.Alt) f.AltSide = Side();
        if (f.Shift) f.ShiftSide = Side();
        if (f.Win) f.WinSide = Side();
    }

    private HotkeyBinding Binding(int vk, bool modifiers)
    {
        var b = new HotkeyBinding { Vk = vk };
        if (!modifiers)
            return b;
        b.Ctrl = Coin(40);
        b.Alt = Coin(30);
        b.Shift = Coin(40);
        b.Win = Coin(10);
        if (b.Ctrl) b.CtrlSide = Side();
        if (b.Alt) b.AltSide = Side();
        if (b.Shift) b.ShiftSide = Side();
        if (b.Win) b.WinSide = Side();
        return b;
    }

    /// <summary>A hotkey on F13 to F24 (no action of the generated items presses them), with modifiers or not.</summary>
    private HotkeyBinding Hotkey() => Binding(_r.Next(0x7C, 0x88), Coin());

    private FloatingButton Button(ActivationMode mode)
    {
        var b = new FloatingButton
        {
            Enabled = mode == ActivationMode.Toggle && Coin(),
            Label = Pick(new[] { "", "A", "AB", "Ω", "日本", "😀" }),
            Size = (FloatingButtonSize)_r.Next(3),
            Opacity = Coin() ? Int((AppSettings.MinOpacity, AppSettings.MaxOpacity)) : null,
        };
        if (Coin())
        {
            b.X = Math.Round(_r.NextDouble() * 3000 - 500, 2);
            b.Y = Math.Round(_r.NextDouble() * 2000 - 500, 2);
        }
        return b;
    }

    // ================= Macros =================

    public MacroDefinition Macro()
    {
        var m = new MacroDefinition
        {
            Name = Name(),
            Repeat = (RepeatMode)_r.Next(3),
            RepeatCount = Int(MacroJson.RepeatCount),
            Speed = Pick(MacroDefinition.SpeedSteps),
            Mode = Coin(20) ? ActivationMode.Hold : ActivationMode.Toggle,
            Enabled = Coin(),
            SoundEnabled = Coin(),
            SoundName = Pick(SoundService.Names),
            ChecksPerSecond = Pick(MacroDefinition.ChecksPerSecondSteps),
            Debug = Coin(),
            AppExe = Coin() ? null : Pick(new[] { "game.exe", "Diablo IV.exe", "città.EXE" }),
            ShowInOverlay = Coin(),
            HideInOverlayWhenDisabled = Coin(),
            OverlayClickPassesThrough = Coin(),
        };
        m.Hotkey = m.Mode == ActivationMode.Hold || Coin() ? Hotkey() : null;
        m.FloatingButton = Button(m.Mode);
        int count = _r.Next(1, 8);
        for (int i = 0; i < count; i++)
            m.Actions.Add(Action(depth: 0, places: new List<Guid>()));

        // Groups: runs of the macro's own actions.
        int at = 0;
        while (at < m.Actions.Count)
        {
            int length = _r.Next(1, 4);
            if (Coin(40))
            {
                var group = new MacroGroup { Name = Name(), Collapsed = Coin() };
                m.Groups.Add(group);
                for (int i = at; i < Math.Min(m.Actions.Count, at + length); i++)
                    m.Actions[i].GroupId = group.Id;
            }
            at += length;
        }
        return m;
    }

    /// <summary>
    /// An action of any type but Component. <paramref name="places"/>: the conditions of the Ifs around it whose Then holds
    /// it and that give a place (where a mouse action can go).
    /// </summary>
    public MacroAction Action(int depth, List<Guid> places)
    {
        var types = Enum.GetValues<MacroActionType>().Where(t => t != MacroActionType.Component && (t != MacroActionType.If || depth < 2)).ToList();
        var a = new MacroAction
        {
            Type = Pick(types),
            DelayMs = Coin(30) ? 0 : Number((0, MacroJson.MaxDelayMs)),
            Name = Coin(30) ? Name() : "",
        };
        switch (a.Type)
        {
            case MacroActionType.PressKey:
                a.Vk = Pick(Keys);
                Modifiers(a);
                Presses(a);
                break;
            case MacroActionType.KeyDown:
            case MacroActionType.KeyUp:
                a.Vk = Coin(70) ? Pick(Keys) : Pick(SidedModifiers);
                break;
            case MacroActionType.Click:
                a.Vk = Pick(Mouse);
                a.ClickCount = _r.Next(1, 3);
                Presses(a);
                Point(a, places);
                break;
            case MacroActionType.MouseDown:
            case MacroActionType.MouseUp:
                a.Vk = Pick(Mouse);
                Point(a, places);
                break;
            case MacroActionType.MoveTo:
                Point(a, places);
                a.Smooth = Coin();
                a.SmoothSpeed = Int(MacroJson.SmoothSpeed);
                break;
            case MacroActionType.MovePath:
                double t = 0;
                for (int i = _r.Next(1, 6); i > 0; i--)
                {
                    a.Path.Add(new PathPoint { X = Int(MacroJson.Coordinate), Y = Int(MacroJson.Coordinate), T = t });
                    t += Math.Round(_r.NextDouble() * 500, 1);
                }
                break;
            case MacroActionType.Scroll:
                a.ScrollDirection = (ScrollDirection)_r.Next(4);
                a.ScrollSteps = Int(MacroJson.ScrollSteps);
                break;
            case MacroActionType.PasteText:
                a.Text = Text();
                a.EnterBefore = Coin();
                a.EnterAfter = Coin();
                break;
            case MacroActionType.If:
                a.If = If(depth, places);
                break;
        }
        return a;
    }

    private void Presses(MacroAction a)
    {
        a.HoldMs = Number(MacroJson.HoldMs);
        a.Repeat = Int(MacroJson.Repeat);
        a.RepeatPauseMs = Number(MacroJson.RepeatPauseMs);
    }

    private void Point(MacroAction a, List<Guid> places)
    {
        a.X = Int(MacroJson.Coordinate);
        a.Y = Int(MacroJson.Coordinate);
        if (places.Count > 0 && Coin())
        {
            a.FoundId = Pick(places);
            a.FoundOffsetX = Int(MacroJson.Coordinate);
            a.FoundOffsetY = Int(MacroJson.Coordinate);
        }
    }

    private MacroIf If(int depth, List<Guid> places)
    {
        var block = new MacroIf
        {
            Match = (ConditionMatch)_r.Next(2),
            Wait = (ConditionWait)_r.Next(3),
            OnFail = (ConditionFail)_r.Next(2),
        };
        if (block.Wait == ConditionWait.UpTo)
            block.WaitSeconds = Number(MacroJson.WaitSeconds);
        for (int i = _r.Next(1, 4); i > 0; i--)
            block.Conditions.Add(Condition());
        var inside = places.Concat(block.Conditions.Where(c => c.GivesPlace).Select(c => c.Id)).ToList();
        for (int i = _r.Next(0, 3); i > 0; i--)
            block.Then.Add(Action(depth + 1, inside));
        for (int i = _r.Next(0, 3); i > 0; i--)
            block.Else.Add(Action(depth + 1, places));
        return block;
    }

    private MacroCondition Condition()
    {
        var c = new MacroCondition { Kind = (ConditionKind)_r.Next(3), Not = Coin(30) };
        switch (c.Kind)
        {
            case ConditionKind.Pixel:
                c.X = Int(MacroJson.Coordinate);
                c.Y = Int(MacroJson.Coordinate);
                c.Color = "#" + _r.Next(0x1000000).ToString(Coin() ? "X6" : "x6", CultureInfo.InvariantCulture);
                c.Tolerance = Int(MacroJson.Tolerance);
                break;
            case ConditionKind.Image:
                c.Name = Name();
                c.Images = Enumerable.Range(0, _r.Next(1, 3)).Select(_ => new ConditionImage
                {
                    Id = string.Concat(Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[_r.Next(16)])),
                    X = Int(MacroJson.Coordinate),
                    Y = Int(MacroJson.Coordinate),
                }).ToList();
                c.NearbySizes = Coin();
                c.Similarity = Int(MacroJson.Similarity);
                Area(c, around: true);
                break;
            case ConditionKind.Text:
                c.Name = Name();
                c.Text = Text().Trim();
                c.TextMatch = (TextMatch)_r.Next(2);
                c.MatchCase = Coin();
                c.Language = Coin() ? null : Pick(new[] { "en-US", "it-IT", "ja" });
                Area(c, around: false);
                break;
        }
        return c;
    }

    private void Area(MacroCondition c, bool around)
    {
        c.Area = around && Coin() ? SearchArea.Around : Coin() ? SearchArea.Drawn : SearchArea.Screen;
        if (c.Area == SearchArea.Around)
            return;
        c.AreaX = Int(MacroJson.Coordinate);
        c.AreaY = Int(MacroJson.Coordinate);
        c.AreaWidth = Int(MacroJson.AreaSize);
        c.AreaHeight = Int(MacroJson.AreaSize);
    }

    // ================= Loops =================

    public LoopDefinition Loop()
    {
        var l = new LoopDefinition
        {
            Name = Name(),
            Mode = Coin(20) ? ActivationMode.Hold : ActivationMode.Toggle,
            Enabled = Coin(),
            SoundEnabled = Coin(),
            SoundName = Pick(SoundService.Names),
            AppExe = Coin() ? null : "game.exe",
            ShowInOverlay = Coin(),
            HideInOverlayWhenDisabled = Coin(),
            OverlayClickPassesThrough = Coin(),
        };
        l.Hotkey = l.Mode == ActivationMode.Hold || Coin() ? Hotkey() : null;
        l.FloatingButton = Button(l.Mode);
        for (int i = _r.Next(1, 5); i > 0; i--)
            l.Actions.Add(NewLoopAction());
        return l;
    }

    private LoopAction NewLoopAction()
    {
        var a = new LoopAction();
        int kind = _r.Next(3);
        if (kind == 2)
        {
            a.Type = ActionType.Text;
            a.Text = Text();
            a.EnterBefore = Coin();
            a.EnterAfter = Coin();
        }
        else
        {
            a.Type = ActionType.Key;
            a.HoldDown = Coin(20);
            if (kind == 1)
            {
                a.KeyVk = Pick(Mouse);
                a.ClickCount = Coin() ? null : _r.Next(1, 3);
                if (Coin())
                {
                    a.X = Int(MacroJson.Coordinate);
                    a.Y = Int(MacroJson.Coordinate);
                }
            }
            else
            {
                a.KeyVk = Pick(Keys);
                if (!a.HoldDown)
                    Modifiers(a);
            }
            a.HoldMs = Coin() ? null : Number(MacroJson.HoldMs);
            a.Repeat = Coin() ? null : Int(MacroJson.Repeat);
            a.RepeatPauseMs = Coin() ? null : Number(MacroJson.RepeatPauseMs);
        }
        a.IntervalUnit = (IntervalUnit)_r.Next(4);
        double perUnit = LoopAction.ToMilliseconds(1, a.IntervalUnit);
        a.IntervalValue = Number((LoopAction.MinIntervalMs / perUnit, LoopAction.MaxIntervalMs / perUnit));
        if (a.IntervalMs < LoopAction.MinIntervalMs || a.IntervalMs > LoopAction.MaxIntervalMs)
            a.IntervalValue = Math.Clamp(a.IntervalValue, Math.Ceiling(LoopAction.MinIntervalMs / perUnit * 1000) / 1000,
                Math.Floor(LoopAction.MaxIntervalMs / perUnit * 1000) / 1000);
        return a;
    }

    // ================= Remaps =================

    public RemapDefinition Remap()
    {
        var r = new RemapDefinition { Note = Coin() ? "" : Name(), Enabled = Coin(), AppExe = Coin() ? null : "game.exe" };
        r.Source = Coin(20) ? new HotkeyBinding { Vk = Pick(SidedModifiers) } : Binding(Pick(Keys.Concat(new[] { 4, 5, 6 }).ToList()), Coin());
        do
            r.Target = Coin(20) ? new HotkeyBinding { Vk = Pick(SidedModifiers) } : Binding(Pick(Keys.Concat(Mouse).ToList()), Coin());
        while (r.Target.SameAs(r.Source));
        return r;
    }
}

/// <summary>
/// The parts of JSON Schema that <see cref="CodeSchema"/> writes (type, enum, const, minimum, maximum, maxLength, properties,
/// additionalProperties, items, required, if/then, $ref inside the schema), as a JSON Schema validator reads them.
/// </summary>
internal static class MiniSchema
{
    public static List<string> Validate(string schema, string json)
    {
        var errors = new List<string>();
        JsonObject root = JsonNode.Parse(schema)!.AsObject();
        Check(JsonNode.Parse(json), root, root, "", errors);
        return errors;
    }

    private static void Check(JsonNode? value, JsonNode? schemaNode, JsonObject root, string path, List<string> errors)
    {
        if (schemaNode is not JsonObject schema)
            return; // true: anything
        if (schema["$ref"]?.GetValue<string>() is string reference)
        {
            Check(value, Resolve(root, reference), root, path, errors);
            return;
        }
        if (schema["type"] is JsonNode type)
        {
            var types = type is JsonArray list ? list.Select(t => t!.GetValue<string>()).ToList() : new List<string> { type.GetValue<string>() };
            if (!types.Any(t => IsType(value, t)))
            {
                errors.Add($"{path}: not {string.Join("/", types)}");
                return;
            }
        }
        if (schema["enum"] is JsonArray values && !values.Any(v => Same(v, value)))
            errors.Add($"{path}: not one of the enum");
        if (schema.ContainsKey("const") && !Same(schema["const"], value))
            errors.Add($"{path}: not the const");
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            double n = v.GetValue<double>();
            if (schema["minimum"] is JsonNode min && n < min.GetValue<double>())
                errors.Add($"{path}: {n} below {min}");
            if (schema["maximum"] is JsonNode max && n > max.GetValue<double>())
                errors.Add($"{path}: {n} above {max}");
        }
        if (value is JsonValue s && s.GetValueKind() == JsonValueKind.String && schema["maxLength"] is JsonNode maxLength
            && s.GetValue<string>().EnumerateRunes().Count() > maxLength.GetValue<int>())
            errors.Add($"{path}: longer than {maxLength}");
        if (value is JsonObject obj)
        {
            var properties = schema["properties"] as JsonObject;
            foreach (var (name, child) in obj)
            {
                string at = path.Length == 0 ? name : $"{path}.{name}";
                if (properties?[name] is JsonNode property)
                    Check(child, property, root, at, errors);
                else if (schema["additionalProperties"] is JsonValue no && no.GetValueKind() == JsonValueKind.False)
                    errors.Add($"{at}: not allowed");
                else if (schema["additionalProperties"] is JsonObject extra)
                    Check(child, extra, root, at, errors);
            }
            if (schema["required"] is JsonArray required)
                foreach (string name in required.Select(r => r!.GetValue<string>()).Where(n => !obj.ContainsKey(n)))
                    errors.Add($"{path}: {name} is required");
        }
        if (value is JsonArray array && schema["items"] is JsonNode items)
            for (int i = 0; i < array.Count; i++)
                Check(array[i], items, root, $"{path}[{i}]", errors);
        if (schema["if"] is JsonNode condition && schema["then"] is JsonNode then)
        {
            var none = new List<string>();
            Check(value, condition, root, path, none);
            if (none.Count == 0)
                Check(value, then, root, path, errors);
        }
    }

    private static JsonNode? Resolve(JsonObject root, string reference)
    {
        JsonNode? node = root;
        foreach (string part in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            node = node![part.Replace("~1", "/").Replace("~0", "~")];
        return node;
    }

    private static bool IsType(JsonNode? value, string type) => type switch
    {
        "null" => value == null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && Math.Floor(i.GetValue<double>()) == i.GetValue<double>(),
        _ => true,
    };

    private static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is JsonValue x && b is JsonValue y && x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number)
            return x.GetValue<double>() == y.GetValue<double>();
        return JsonNode.DeepEquals(a, b);
    }
}
