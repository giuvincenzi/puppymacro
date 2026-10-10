using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>A problem in a Code view, at a 1-based line and column.</summary>
internal sealed record CodeProblem(int Line, int Column, string Message);

/// <summary>
/// The JSON the Code views show (a macro, one of its actions, a loop, a remap): the same text as in
/// the data files, read back strictly. Every value the Form view could not produce is a problem at
/// its line, instead of being fixed silently as when a file is loaded. The checks of each kind of
/// item are in <see cref="MacroJson"/>, <see cref="LoopJson"/> and <see cref="RemapJson"/>.
/// </summary>
internal static class CodeJson
{
    /// <summary>The options of the data files (<c>macros\{id}.json</c>, settings.json).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The same format, read strictly: unknown properties, nulls where a value is needed and enum
    /// values not written exactly by name are problems.
    /// </summary>
    public static readonly JsonSerializerOptions StrictOptions = new()
    {
        WriteIndented = true,
        Converters = { new ExactEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), // for CodeSchema
    };

    public const double MaxDelayMs = 86_400_000;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Reads <paramref name="text"/> as a <typeparamref name="T"/>, then runs <paramref name="check"/>.
    /// Null when there are problems.
    /// </summary>
    public static T? Read<T>(string text, Action<T, Checker> check, out List<CodeProblem> problems) where T : class
    {
        problems = new List<CodeProblem>();
        var locator = new Locator(text);

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

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, StrictOptions);
        }
        catch (JsonException ex)
        {
            problems.Add(locator.ProblemFor(ex));
            return null;
        }
        if (value == null)
        {
            problems.Add(new CodeProblem(1, 1, "This must be a JSON object."));
            return null;
        }

        check(value, new Checker(locator, problems));
        return problems.Count > 0 ? null : value;
    }

    /// <summary>Adds problems at the line of a path ("Actions[2].Vk").</summary>
    internal sealed class Checker
    {
        private readonly Locator _at;
        private readonly List<CodeProblem> _problems;

        public Checker(Locator at, List<CodeProblem> problems)
        {
            _at = at;
            _problems = problems;
        }

        public void Add(string path, string message) => _problems.Add(_at.ProblemAt(path, message));

        public void Range(string path, double value, (double Min, double Max) range)
        {
            if (double.IsNaN(value) || value < range.Min || value > range.Max)
                Add(path, $"Must be between {range.Min:0.##} and {range.Max:0.##}.");
        }

        /// <summary>The Id cannot change: it names the file or the item.</summary>
        public void SameId(Guid id, Guid expected)
        {
            if (id != expected)
                Add("Id", $"The Id cannot be changed. Use \"{expected}\" (Ctrl+Space suggests it).");
        }

        public void KnownSound(string name)
        {
            if (!SoundService.Names.Contains(name))
                Add("SoundName", $"Unknown sound. Use one of: {string.Join(", ", SoundService.Names.Select(n => $"\"{n}\""))}.");
        }

        /// <summary>The checks of the hotkey field of the loop and macro editors.</summary>
        public void Hotkey(HotkeyBinding? hotkey, ActivationMode mode, Func<HotkeyBinding, string?> conflict)
        {
            bool hold = mode == ActivationMode.Hold;
            if (hotkey is not { IsSet: true })
            {
                if (hold)
                    Add(hotkey == null ? "Mode" : "Hotkey.Vk", "Hold needs a hotkey. Set one or use \"Toggle\".");
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
            else if (conflict(hotkey) is string message)
                Add("Hotkey", message);
        }

        /// <summary>The checks of the Specific app card: null (all apps) or an .exe file name.</summary>
        public void AppExe(string? appExe)
        {
            if (appExe == null)
                return;
            if (string.IsNullOrWhiteSpace(appExe))
                Add("AppExe", "Write the app's file name, like \"notepad.exe\", or null for all apps.");
            else if (!appExe.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Add("AppExe", "Write the app's file name, ending in \".exe\".");
        }

        /// <summary>The checks of the Floating button card.</summary>
        public void FloatingButton(FloatingButton b, ActivationMode mode)
        {
            if (b.Label.Trim().Length > Models.FloatingButton.MaxLabelLength)
                Add("FloatingButton.Label", $"At most {Models.FloatingButton.MaxLabelLength} characters.");
            if (b.Opacity is int opacity && (opacity < AppSettings.MinOpacity || opacity > AppSettings.MaxOpacity))
                Add("FloatingButton.Opacity", $"Must be between {AppSettings.MinOpacity} and {AppSettings.MaxOpacity}.");
            if ((b.X == null) != (b.Y == null))
                Add(b.X == null ? "FloatingButton.Y" : "FloatingButton.X", "Set both X and Y, or neither (null: the middle of the main screen).");
            if (b.Enabled && mode == ActivationMode.Hold)
                Add("FloatingButton.Enabled", "Not available with Hold: a click can only start and stop. Use \"Toggle\".");
        }
    }

    /// <summary>A virtual-key code a keyboard hook can report (1 to 254).</summary>
    public static bool IsKeyCode(int vk) => vk is >= 1 and <= 254;

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
    internal sealed class Locator
    {
        private readonly byte[] _utf8;
        private readonly Dictionary<string, int> _offsets = new();

        public Locator(string text)
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
