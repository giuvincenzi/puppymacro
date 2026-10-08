using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>A problem in the macro editor's Code view, at a 1-based line and column.</summary>
internal sealed record CodeProblem(int Line, int Column, string Message);

/// <summary>
/// The macro file's JSON: the text <see cref="MacroLibrary"/> saves, and the Code view's
/// strict reading of it. Every value the editor's List view could not produce is a problem
/// here, at its line, instead of being fixed silently as when a file is loaded.
/// </summary>
internal static class MacroJson
{
    /// <summary>The options of the macro files (<c>macros\{id}.json</c>).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Code view: the same format, read strictly. Unknown properties, nulls where a value is
    /// needed and enum values written as numbers are problems.
    /// </summary>
    public static readonly JsonSerializerOptions StrictOptions = new()
    {
        WriteIndented = true,
        Converters = { new ExactEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), // for MacroSchema
    };

    // Limits of the editor's fields (MacroEditorWindow, MacroActionWindow), also used by MacroSchema.
    public const double MaxDelayMs = 86_400_000;
    public static readonly (double Min, double Max) HoldMs = (1, 60_000);
    public static readonly (double Min, double Max) Repeat = (1, 100_000);
    public static readonly (double Min, double Max) RepeatPauseMs = (0, MaxDelayMs);
    public static readonly (double Min, double Max) Coordinate = (-100_000, 100_000);
    public static readonly (double Min, double Max) SmoothSpeed = (1, 5);
    public static readonly (double Min, double Max) ScrollSteps = (1, 1000);
    public static readonly (double Min, double Max) RepeatCount = (1, 100_000);

    public static string Serialize(MacroDefinition macro) => JsonSerializer.Serialize(macro, Options);

    /// <summary>Checks the Code view's text and reads it. Null when there are problems.</summary>
    /// <param name="conflict">The hotkey conflict message for a binding, or null (see <see cref="HotkeyConflicts"/>).</param>
    public static MacroDefinition? Parse(string text, Guid id, Func<HotkeyBinding, string?> conflict, out List<CodeProblem> problems)
    {
        problems = new List<CodeProblem>();
        var locator = new JsonLocator(text);

        // Syntax first: the reader knows the exact line and column.
        try
        {
            using var document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            problems.Add(locator.SyntaxProblem(ex));
            return null;
        }

        MacroDefinition? macro;
        try
        {
            macro = JsonSerializer.Deserialize<MacroDefinition>(text, StrictOptions);
        }
        catch (JsonException ex)
        {
            problems.Add(locator.ProblemFor(ex));
            return null;
        }
        if (macro == null)
        {
            problems.Add(new CodeProblem(1, 1, "The macro must be a JSON object."));
            return null;
        }

        new Checker(macro, id, conflict, locator, problems).Run();
        if (problems.Count > 0)
            return null;
        macro.Name = macro.Name.Trim();
        foreach (var group in macro.Groups)
            group.Name = group.Name.Trim();
        return macro;
    }

    private sealed class Checker
    {
        private readonly MacroDefinition _macro;
        private readonly Guid _id;
        private readonly Func<HotkeyBinding, string?> _conflict;
        private readonly JsonLocator _at;
        private readonly List<CodeProblem> _problems;

        public Checker(MacroDefinition macro, Guid id, Func<HotkeyBinding, string?> conflict, JsonLocator at, List<CodeProblem> problems)
        {
            _macro = macro;
            _id = id;
            _conflict = conflict;
            _at = at;
            _problems = problems;
        }

        private void Add(string path, string message) => _problems.Add(_at.ProblemAt(path, message));

        private void Range(string path, double value, (double Min, double Max) range)
        {
            if (double.IsNaN(value) || value < range.Min || value > range.Max)
                Add(path, $"Must be between {range.Min:0.##} and {range.Max:0.##}.");
        }

        public void Run()
        {
            MacroDefinition m = _macro;
            if (m.Id != _id)
                Add("Id", $"The Id cannot be changed: it is the name of the macro's file. Use \"{_id}\".");
            if (string.IsNullOrWhiteSpace(m.Name))
                Add("Name", "Enter a name.");
            if (m.Actions.Count == 0)
                Add("Actions", "Add at least one action.");
            Range("RepeatCount", m.RepeatCount, RepeatCount);
            if (!MacroDefinition.SpeedSteps.Contains(m.Speed))
                Add("Speed", "Must be 0.25, 0.5, 1, 2 or 4.");
            if (!SoundService.Names.Contains(m.SoundName))
                Add("SoundName", $"Unknown sound. Use one of: {string.Join(", ", SoundService.Names.Select(n => $"\"{n}\""))}.");

            CheckHotkey(m);
            CheckFloatingButton(m);
            CheckGroups(m);
            for (int i = 0; i < m.Actions.Count; i++)
                CheckAction(m.Actions[i], $"Actions[{i}]");
        }

        private void CheckHotkey(MacroDefinition m)
        {
            bool hold = m.Mode == ActivationMode.Hold;
            if (m.Hotkey is not { IsSet: true } hotkey)
            {
                if (hold)
                    Add(m.Hotkey == null ? "Mode" : "Hotkey.Vk", "Hold needs a hotkey. Set one or use \"Toggle\".");
                return;
            }
            if (!IsKeyCode(hotkey.Vk) && !KeyNames.IsWheel(hotkey.Vk))
            {
                Add("Hotkey.Vk", "Not a key: use a virtual-key code from 1 to 254, or a scroll wheel code (256 up, 257 down, 258 left, 259 right).");
                return;
            }
            if (KeyNames.IsModifier(hotkey.Vk))
            {
                Add("Hotkey.Vk", "Ctrl, Alt, Shift and Win alone cannot be the key: set them with \"Ctrl\", \"Alt\", \"Shift\" and \"Win\".");
                return;
            }
            if (HotkeyRules.Problem(hotkey, hold) is string problem)
                Add("Hotkey", problem);
            else if (_conflict(hotkey) is string conflict)
                Add("Hotkey", conflict);
        }

        private void CheckFloatingButton(MacroDefinition m)
        {
            FloatingButton b = m.FloatingButton;
            if (b.Label.Trim().Length > FloatingButton.MaxLabelLength)
                Add("FloatingButton.Label", $"At most {FloatingButton.MaxLabelLength} characters.");
            if (b.Opacity is int opacity && (opacity < AppSettings.MinOpacity || opacity > AppSettings.MaxOpacity))
                Add("FloatingButton.Opacity", $"Must be between {AppSettings.MinOpacity} and {AppSettings.MaxOpacity}.");
            if ((b.X == null) != (b.Y == null))
                Add(b.X == null ? "FloatingButton.Y" : "FloatingButton.X", "Set both X and Y, or neither (null: the middle of the main screen).");
            if (b.Enabled && m.Mode == ActivationMode.Hold)
                Add("FloatingButton.Enabled", "Not available with Hold: a click can only start and stop. Use \"Toggle\".");
        }

        private void CheckGroups(MacroDefinition m)
        {
            var index = new Dictionary<Guid, int>();
            for (int j = 0; j < m.Groups.Count; j++)
            {
                MacroGroup group = m.Groups[j];
                if (!index.TryAdd(group.Id, j))
                    Add($"Groups[{j}].Id", "Two groups have this Id.");
                if (string.IsNullOrWhiteSpace(group.Name))
                    Add($"Groups[{j}].Name", "Enter a group name.");
            }

            var finished = new HashSet<Guid>();
            var used = new HashSet<Guid>();
            Guid? previous = null;
            for (int i = 0; i < m.Actions.Count; i++)
            {
                Guid? groupId = m.Actions[i].GroupId;
                if (previous is Guid ended && ended != groupId)
                    finished.Add(ended);
                if (groupId is Guid id)
                {
                    if (!index.ContainsKey(id))
                        Add($"Actions[{i}].GroupId", "No group in \"Groups\" has this Id.");
                    else if (finished.Contains(id))
                        Add($"Actions[{i}].GroupId", "A group's actions must be next to each other.");
                    used.Add(id);
                }
                previous = groupId;
            }
            for (int j = 0; j < m.Groups.Count; j++)
            {
                if (!used.Contains(m.Groups[j].Id))
                    Add($"Groups[{j}]", "This group has no actions: give it some (\"GroupId\") or remove it.");
            }
        }

        private void CheckAction(MacroAction a, string path)
        {
            Range($"{path}.DelayMs", a.DelayMs, (0, MaxDelayMs));
            switch (a.Type)
            {
                case MacroActionType.PressKey:
                case MacroActionType.KeyDown:
                case MacroActionType.KeyUp:
                    if (!IsKeyCode(a.Vk))
                        Add($"{path}.Vk", "Choose a key: a virtual-key code from 1 to 254.");
                    break;
                case MacroActionType.Click:
                case MacroActionType.MouseDown:
                case MacroActionType.MouseUp:
                    if (!KeyNames.IsMouse(a.Vk))
                        Add($"{path}.Vk", "Choose a mouse button: 1 left, 2 right, 4 middle, 5 back (X1), 6 forward (X2).");
                    break;
            }
            if (a.Type is MacroActionType.PressKey or MacroActionType.Click)
            {
                Range($"{path}.HoldMs", a.HoldMs, HoldMs);
                Range($"{path}.Repeat", a.Repeat, Repeat);
                Range($"{path}.RepeatPauseMs", a.RepeatPauseMs, RepeatPauseMs);
            }
            if (a.Type == MacroActionType.Click && a.ClickCount is not (1 or 2))
                Add($"{path}.ClickCount", "Must be 1 (single click) or 2 (double click).");
            if (a.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo)
            {
                Range($"{path}.X", a.X, Coordinate);
                Range($"{path}.Y", a.Y, Coordinate);
            }
            if (a.Type == MacroActionType.MoveTo)
                Range($"{path}.SmoothSpeed", a.SmoothSpeed, SmoothSpeed);
            if (a.Type == MacroActionType.MovePath)
            {
                if (a.Path.Count == 0)
                    Add($"{path}.Path", "A path needs at least one point.");
                for (int p = 0; p < a.Path.Count; p++)
                {
                    if (double.IsNaN(a.Path[p].T) || a.Path[p].T < 0 || (p > 0 && a.Path[p].T < a.Path[p - 1].T))
                        Add($"{path}.Path[{p}].T", "Times must start at 0 or more and never go back.");
                }
            }
            if (a.Type == MacroActionType.Scroll)
                Range($"{path}.ScrollSteps", a.ScrollSteps, ScrollSteps);
            if (a.Type == MacroActionType.PasteText && string.IsNullOrEmpty(a.Text))
                Add($"{path}.Text", "Enter the text to paste.");
        }

        private static bool IsKeyCode(int vk) => vk is >= 1 and <= 254;
    }

    /// <summary>
    /// Enums as their exact names, as the files are written: "PressKey", not "presskey" or 0
    /// (JsonStringEnumConverter reads names in any case and numbers).
    /// </summary>
    private sealed class ExactEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String && reader.GetString() is string name
                    && Enum.GetNames<T>().Contains(name, StringComparer.Ordinal))
                    return Enum.Parse<T>(name);
                throw new JsonException($"Must be one of: {string.Join(", ", Enum.GetNames<T>().Select(n => $"\"{n}\""))}.");
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.ToString());
        }
    }

    /// <summary>Finds the line and column of a value by its path ("Actions[2].Vk") in the text.</summary>
    private sealed class JsonLocator
    {
        private readonly byte[] _utf8;
        private readonly Dictionary<string, int> _offsets = new();

        public JsonLocator(string text)
        {
            _utf8 = Encoding.UTF8.GetBytes(text);
            try
            {
                Map();
            }
            catch (JsonException)
            {
                // Invalid JSON: the paths read so far are kept.
            }
        }

        private void Map()
        {
            var reader = new Utf8JsonReader(_utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            var containers = new Stack<(string Path, bool IsArray, int Index)>();
            string? property = null;

            string NextValuePath(int start)
            {
                if (containers.Count == 0)
                    return "";
                var (path, isArray, index) = containers.Pop();
                if (!isArray)
                {
                    containers.Push((path, false, index));
                    return property ?? path;
                }
                containers.Push((path, true, index + 1));
                string element = $"{path}[{index}]";
                _offsets.TryAdd(element, start);
                return element;
            }

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        string parent = containers.Count > 0 ? containers.Peek().Path : "";
                        string name = reader.GetString() ?? "";
                        property = parent.Length == 0 ? name : $"{parent}.{name}";
                        _offsets.TryAdd(property, (int)reader.TokenStartIndex);
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        string container = NextValuePath((int)reader.TokenStartIndex);
                        containers.Push((container, reader.TokenType == JsonTokenType.StartArray, 0));
                        property = null;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        containers.Pop();
                        property = null;
                        break;
                    default:
                        NextValuePath((int)reader.TokenStartIndex);
                        property = null;
                        break;
                }
            }
        }

        public CodeProblem ProblemAt(string path, string message)
        {
            string? search = path;
            while (search != null)
            {
                if (_offsets.TryGetValue(search, out int offset))
                {
                    var (line, column) = Position(offset);
                    return new CodeProblem(line, column, $"{path}: {message}");
                }
                search = ParentOf(search);
            }
            return new CodeProblem(1, 1, $"{path}: {message}");
        }

        public CodeProblem SyntaxProblem(JsonException ex)
        {
            // "'x' is invalid after a value. Expected ... LineNumber: 3 | BytePositionInLine: 4." without the position.
            string message = ex.Message;
            int position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (position > 0)
                message = message[..position];
            message = $"Invalid JSON: {message.Trim()}";
            if (ex.LineNumber is long line && ex.BytePositionInLine is long bytes)
                return new CodeProblem((int)line + 1, ColumnOf((int)line, (int)bytes), message);
            return new CodeProblem(1, 1, message);
        }

        public CodeProblem ProblemFor(JsonException ex)
        {
            string path = ex.Path is { Length: > 1 } p && p.StartsWith("$.") ? p[2..] : "";
            if (ex.Message.Contains("could not be mapped to any .NET member"))
            {
                string name = UnknownProperty(ex.Message);
                string unknown = path.Length == 0 ? name : path.EndsWith(name) ? path : $"{path}.{name}";
                return ProblemAt(unknown, $"Unknown property \"{name}\".");
            }
            // Messages without .NET's position suffix are this file's own (ExactEnumConverter): shown as they are.
            bool own = !ex.Message.Contains("Path:") && !ex.Message.Contains("LineNumber:");
            string message = own ? ex.Message
                : ex.InnerException is FormatException or InvalidOperationException || ex.Message.Contains("could not be converted")
                    ? "This value has the wrong type or is not one of the allowed values."
                    : ex.Message.Contains("null")
                        ? "A value is needed here, not null."
                        : "Invalid JSON.";

            // The reader's position is where it stopped; for a known path, point at the property.
            if (path.Length > 0 && _offsets.ContainsKey(path))
                return ProblemAt(path, message);
            if (ex.LineNumber is long line && ex.BytePositionInLine is long bytes)
                return new CodeProblem((int)line + 1, ColumnOf((int)line, (int)bytes), path.Length > 0 ? $"{path}: {message}" : message);
            return new CodeProblem(1, 1, message);
        }

        private static string UnknownProperty(string message)
        {
            int start = message.IndexOf('\'');
            int end = start >= 0 ? message.IndexOf('\'', start + 1) : -1;
            return end > start ? message[(start + 1)..end] : "?";
        }

        private static string? ParentOf(string path)
        {
            if (path.Length == 0)
                return null;
            int cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
            return cut <= 0 ? "" : path[..cut];
        }

        private (int Line, int Column) Position(int byteOffset)
        {
            int line = 0, lineStart = 0;
            for (int i = 0; i < byteOffset && i < _utf8.Length; i++)
            {
                if (_utf8[i] == (byte)'\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
            return (line + 1, Encoding.UTF8.GetCharCount(_utf8, lineStart, Math.Max(0, byteOffset - lineStart)) + 1);
        }

        /// <summary>1-based column from a 0-based line and a byte offset in that line.</summary>
        private int ColumnOf(int line, int bytesInLine)
        {
            int start = 0;
            for (int i = 0, seen = 0; i < _utf8.Length && seen < line; i++)
            {
                if (_utf8[i] == (byte)'\n')
                {
                    seen++;
                    start = i + 1;
                }
            }
            int count = Math.Min(bytesInLine, _utf8.Length - start);
            return Encoding.UTF8.GetCharCount(_utf8, start, Math.Max(0, count)) + 1;
        }
    }
}
