using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PuppyMacro.Models;
using PuppyMacro.Resources;

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
        List<string> nulls;
        try
        {
            using var document = JsonDocument.Parse(text);
            nulls = MisplacedNulls(document.RootElement, typeof(T));
        }
        catch (JsonException ex)
        {
            problems.Add(locator.SyntaxProblem(ex));
            return null;
        }

        // A null where the model has no null (an entry of a list, a field): a problem at its path, before the checks get it.
        var checker = new Checker(locator, problems);
        foreach (string path in nulls)
            checker.Add(path, Strings.Code_NeedsValue);
        if (problems.Count > 0)
            return null;

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
            problems.Add(new CodeProblem(1, 1, Strings.Code_MustBeObject));
            return null;
        }

        check(value, checker);
        return problems.Count > 0 ? null : value;
    }

    /// <summary>
    /// Reads a data file (a macro file, library.json) as it is loaded: a null where the model has no null is left out, so
    /// the field keeps its default and a list loses that entry (a null holds nothing to keep). The strict reading of the
    /// Code views makes each of them a problem instead (<see cref="Read{T}"/>). Throws <see cref="JsonException"/> when the
    /// text is not JSON or does not fit the model.
    /// </summary>
    public static T? ReadFile<T>(string text) where T : class
    {
        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Deserialize<T>(WithoutMisplacedNulls(document.RootElement, typeof(T)), Options);
    }

    /// <summary>The same JSON without the nulls <see cref="MisplacedNulls"/> finds.</summary>
    public static byte[] WithoutMisplacedNulls(JsonElement root, Type type)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Walk(root, type, null, "", new NullabilityInfoContext(), new List<string>(), writer);
        return stream.ToArray();
    }

    /// <summary>
    /// The paths ("Actions[2].If.Conditions[0]") of every null of <paramref name="root"/> where <paramref name="type"/> (as
    /// the data files and <see cref="StrictOptions"/> read it) has no null: an entry of a list or of a dictionary whose
    /// entries are never null, or a field that is never null. The one place that finds them, for every kind of item: the
    /// strict reading lets a null entry of a list through (RespectNullableAnnotations covers only properties), and the
    /// checks after it, like the code that loads a file, expect none.
    /// </summary>
    public static List<string> MisplacedNulls(JsonElement root, Type type)
    {
        var found = new List<string>();
        Walk(root, type, null, "", new NullabilityInfoContext(), found, null);
        return found;
    }

    /// <summary>Finds the misplaced nulls of <paramref name="element"/> and, with a <paramref name="writer"/>, writes it without them.</summary>
    private static void Walk(JsonElement element, Type type, NullabilityInfo? nullability, string path, NullabilityInfoContext context,
        List<string> found, Utf8JsonWriter? writer)
    {
        JsonTypeInfo info = StrictOptions.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when element.ValueKind == JsonValueKind.Object:
                writer?.WriteStartObject();
                foreach (JsonProperty member in element.EnumerateObject())
                {
                    string at = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                    JsonPropertyInfo? property = info.Properties.FirstOrDefault(p => p.Name == member.Name);
                    if (property == null)
                    {
                        // An unknown property: the reading decides.
                        if (writer != null)
                            member.WriteTo(writer);
                        continue;
                    }
                    if (member.Value.ValueKind == JsonValueKind.Null && !property.IsSetNullable)
                    {
                        found.Add(at);
                        continue;
                    }
                    writer?.WritePropertyName(member.Name);
                    NullabilityInfo? inner = property.AttributeProvider switch
                    {
                        PropertyInfo p => context.Create(p),
                        FieldInfo f => context.Create(f),
                        _ => null,
                    };
                    Walk(member.Value, property.PropertyType, inner, at, context, found, writer);
                }
                writer?.WriteEndObject();
                break;
            case JsonTypeInfoKind.Enumerable when element.ValueKind == JsonValueKind.Array && info.ElementType is Type elementType:
            {
                NullabilityInfo? entry = nullability?.ElementType ?? nullability?.GenericTypeArguments.FirstOrDefault();
                bool mayBeNull = MayBeNull(elementType, entry);
                writer?.WriteStartArray();
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string at = $"{path}[{i++}]";
                    if (item.ValueKind == JsonValueKind.Null && !mayBeNull)
                        found.Add(at);
                    else
                        Walk(item, elementType, entry, at, context, found, writer);
                }
                writer?.WriteEndArray();
                break;
            }
            case JsonTypeInfoKind.Dictionary when element.ValueKind == JsonValueKind.Object && info.ElementType is Type valueType:
            {
                NullabilityInfo? entry = nullability?.GenericTypeArguments is { Length: 2 } arguments ? arguments[1] : null;
                bool mayBeNull = MayBeNull(valueType, entry);
                writer?.WriteStartObject();
                foreach (JsonProperty member in element.EnumerateObject())
                {
                    string at = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                    if (member.Value.ValueKind == JsonValueKind.Null && !mayBeNull)
                    {
                        found.Add(at);
                        continue;
                    }
                    writer?.WritePropertyName(member.Name);
                    Walk(member.Value, valueType, entry, at, context, found, writer);
                }
                writer?.WriteEndObject();
                break;
            }
            default:
                if (writer != null)
                    element.WriteTo(writer);
                break;
        }
    }

    /// <summary>An entry of this type can be null: a Nullable value, or a reference not marked as never null.</summary>
    private static bool MayBeNull(Type type, NullabilityInfo? nullability)
    {
        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) != null;
        if (nullability != null)
            return nullability.ReadState != NullabilityState.NotNull;
        return type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type);
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
                Add(path, string.Format(CultureInfo.CurrentCulture, Strings.Code_Between,
                    range.Min.ToString("0.##", CultureInfo.CurrentCulture), range.Max.ToString("0.##", CultureInfo.CurrentCulture)));
        }

        /// <summary>The Id cannot change: it names the file or the item.</summary>
        public void SameId(Guid id, Guid expected)
        {
            if (id != expected)
                Add("Id", string.Format(CultureInfo.CurrentCulture, Strings.Code_IdCannotChange, expected));
        }

        public void KnownSound(string name)
        {
            if (!SoundService.Names.Contains(name))
                Add("SoundName", string.Format(CultureInfo.CurrentCulture, Strings.Code_UnknownSound,
                    string.Join(", ", SoundService.Names.Select(n => $"\"{n}\""))));
        }

        /// <summary>The checks of the hotkey field of the loop and macro editors.</summary>
        public void Hotkey(HotkeyBinding? hotkey, ActivationMode mode, Func<HotkeyBinding, string?> conflict)
        {
            bool hold = mode == ActivationMode.Hold;
            if (hotkey is not { IsSet: true })
            {
                if (hold)
                    Add(hotkey == null ? "Mode" : "Hotkey.Vk", Strings.Code_HoldNeedsHotkey);
                return;
            }
            if (!IsKeyCode(hotkey.Vk) && !KeyNames.IsWheel(hotkey.Vk))
            {
                Add("Hotkey.Vk", Strings.Code_HotkeyNotAKey);
                return;
            }
            if (KeyNames.IsModifier(hotkey.Vk))
            {
                Add("Hotkey.Vk", Strings.Code_HotkeyModifierAlone);
                return;
            }
            if (!ModifierSides("Hotkey", hotkey))
                return;
            if (HotkeyRules.Problem(hotkey, hold) is string problem)
                Add("Hotkey", problem);
            else if (conflict(hotkey) is string message)
                Add("Hotkey", message);
        }

        /// <summary>
        /// A remap's source or target at <paramref name="path"/>: a key or mouse button with its modifiers, or a modifier
        /// key alone by its side. False when it is not valid.
        /// </summary>
        public bool RemapKey(string path, HotkeyBinding binding)
        {
            if (!IsKeyCode(binding.Vk))
            {
                Add($"{path}.Vk", Strings.Code_RemapChooseKey);
                return false;
            }
            if (KeyNames.IsModifier(binding.Vk))
            {
                if (!KeyNames.IsSidedModifier(binding.Vk))
                {
                    Add($"{path}.Vk", Strings.Code_RemapModifierSide);
                    return false;
                }
                if (binding.HasModifiers)
                {
                    Add(path, Strings.Code_RemapModifierNoOthers);
                    return false;
                }
            }
            return ModifierSides(path, binding);
        }

        /// <summary>A side (CtrlSide, AltSide, ShiftSide, WinSide) only for a modifier the binding holds.</summary>
        public bool ModifierSides(string path, HotkeyBinding binding)
        {
            bool valid = true;
            foreach (var (name, held, side) in new[]
            {
                ("Ctrl", binding.Ctrl, binding.CtrlSide), ("Alt", binding.Alt, binding.AltSide),
                ("Shift", binding.Shift, binding.ShiftSide), ("Win", binding.Win, binding.WinSide),
            })
            {
                if (!held && side != ModifierSide.Any)
                {
                    Add($"{path}.{name}Side", string.Format(CultureInfo.CurrentCulture, Strings.Code_OnlyWithModifier, name));
                    valid = false;
                }
            }
            return valid;
        }

        /// <summary>
        /// The Ctrl, Alt, Shift and Win of a loop action or a macro action (<paramref name="prefix"/>: "" or
        /// "Actions[2]."). Where it can have none, <paramref name="whyNone"/> says why. False when not valid.
        /// </summary>
        public bool KeyModifiers(string prefix, KeyModifierFields f, string? whyNone)
        {
            bool valid = true;
            foreach (var (name, held, side) in new[]
            {
                ("Ctrl", f.Ctrl, f.CtrlSide), ("Alt", f.Alt, f.AltSide), ("Shift", f.Shift, f.ShiftSide), ("Win", f.Win, f.WinSide),
            })
            {
                if (held && whyNone != null)
                {
                    Add($"{prefix}{name}", whyNone);
                    valid = false;
                }
                else if (!held && side != ModifierSide.Any)
                {
                    Add($"{prefix}{name}Side", string.Format(CultureInfo.CurrentCulture, Strings.Code_OnlyWithModifier, name));
                    valid = false;
                }
            }
            return valid;
        }

        /// <summary>The checks of the Specific app card: null (all apps) or an .exe file name.</summary>
        public void AppExe(string? appExe)
        {
            if (appExe == null)
                return;
            if (string.IsNullOrWhiteSpace(appExe))
                Add("AppExe", Strings.Code_AppExeEmpty);
            else if (!appExe.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Add("AppExe", Strings.Code_AppExeNotExe);
        }

        /// <summary>The checks of the Floating button card.</summary>
        public void FloatingButton(FloatingButton b, ActivationMode mode)
        {
            if (b.Label.Trim().Length > Models.FloatingButton.MaxLabelLength)
                Add("FloatingButton.Label", Plurals.Format("Code_AtMostCharacters", Models.FloatingButton.MaxLabelLength));
            if (b.Opacity is int opacity && (opacity < AppSettings.MinOpacity || opacity > AppSettings.MaxOpacity))
                Add("FloatingButton.Opacity", string.Format(CultureInfo.CurrentCulture, Strings.Code_Between, AppSettings.MinOpacity, AppSettings.MaxOpacity));
            if ((b.X == null) != (b.Y == null))
                Add(b.X == null ? "FloatingButton.Y" : "FloatingButton.X", Strings.Code_SetBothXY);
            if (b.Enabled && mode == ActivationMode.Hold)
                Add("FloatingButton.Enabled", Strings.Code_FloatingNeedsToggle);
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
                throw new JsonException(string.Format(CultureInfo.CurrentCulture, Strings.Code_MustBeOneOf,
                    string.Join(", ", Enum.GetNames<T>().Select(n => $"\"{n}\""))));
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
                    return new CodeProblem(line, column, AtPath(path, message));
                }
                search = ParentOf(search);
            }
            return new CodeProblem(1, 1, AtPath(path, message));
        }

        public CodeProblem SyntaxProblem(JsonException ex)
        {
            // "'x' is invalid after a value. Expected ... LineNumber: 3 | BytePositionInLine: 4." without the position.
            string message = ex.Message;
            int position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (position > 0)
                message = message[..position];
            message = string.Format(CultureInfo.CurrentCulture, Strings.Code_InvalidJsonDetail, message.Trim());
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
                return ProblemAt(unknown, string.Format(CultureInfo.CurrentCulture, Strings.Code_UnknownProperty, name));
            }
            // Messages without .NET's position suffix are this file's own (ExactEnumConverter): shown as they are.
            bool own = !ex.Message.Contains("Path:") && !ex.Message.Contains("LineNumber:");
            string message = own ? ex.Message
                : ex.InnerException is FormatException or InvalidOperationException || ex.Message.Contains("could not be converted")
                    ? Strings.Code_WrongType
                    : ex.Message.Contains("null")
                        ? Strings.Code_NeedsValue
                        : Strings.Code_InvalidJson;

            // The reader's position is where it stopped; for a known path, point at the property.
            if (path.Length > 0 && _offsets.ContainsKey(path))
                return ProblemAt(path, message);
            if (ex.LineNumber is long line && ex.BytePositionInLine is long bytes)
                return new CodeProblem((int)line + 1, ColumnOf((int)line, (int)bytes), path.Length > 0 ? AtPath(path, message) : message);
            return new CodeProblem(1, 1, message);
        }

        /// <summary>A message at a path: "Actions[2].Vk: Choose a key…".</summary>
        private static string AtPath(string path, string message) =>
            string.Format(CultureInfo.CurrentCulture, Strings.Code_AtPath, path, message);

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
