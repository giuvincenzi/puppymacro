using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The items as AI apps read and change them, kept short (an AI app writes and reads every character, and a big macro takes
/// minutes to rewrite): <see cref="Compact"/> for get_item, <see cref="Patch"/> (JSON Patch, RFC 6902) for patch_item.
/// </summary>
internal static class AiJson
{
    /// <summary>The data files' format on one line, with "Ctrl + X → A" written as it is.</summary>
    private static readonly JsonSerializerOptions OneLine = new(CodeJson.Options)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly ConcurrentDictionary<Type, JsonObject> Defaults = new();

    /// <summary>
    /// The item's JSON on one line, without the fields that have their default value (in objects at any depth: actions,
    /// groups, the floating button, hotkeys). Read back, it is the same item: a field left out takes its default.
    /// </summary>
    public static string Compact(object item)
    {
        JsonNode node = JsonSerializer.SerializeToNode(item, item.GetType(), CodeJson.Options)!;
        if (node is JsonObject obj)
            Strip(obj, item.GetType());
        return node.ToJsonString(OneLine);
    }

    /// <summary>A new object of <paramref name="type"/> as JSON: its defaults. Null for a type without a parameterless constructor.</summary>
    private static JsonObject? DefaultOf(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) == null)
            return null;
        return Defaults.GetOrAdd(type, t => JsonSerializer.SerializeToNode(Activator.CreateInstance(t), t, CodeJson.Options)!.AsObject());
    }

    private static void Strip(JsonObject obj, Type type)
    {
        JsonObject? defaults = DefaultOf(type);
        JsonTypeInfo info = CodeJson.Options.GetTypeInfo(type);
        foreach (string name in obj.Select(p => p.Key).ToList())
        {
            JsonNode? value = obj[name];
            JsonNode? standard = defaults?[name];
            Type? propertyType = info.Properties.FirstOrDefault(p => p.Name == name)?.PropertyType;
            Type? valueType = propertyType == null ? null : Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (value is JsonObject child && valueType != null && DefaultOf(valueType) != null)
                Strip(child, valueType);
            else if (value is JsonArray array && valueType?.IsGenericType == true && valueType.GetGenericArguments()[0] is { } element
                     && DefaultOf(element) != null)
                foreach (JsonObject item in array.OfType<JsonObject>())
                    Strip(item, element);

            // A field equal to its default (a nested object whose fields all were: its own default) is left out.
            bool isDefault = defaults != null && defaults.ContainsKey(name) && (JsonNode.DeepEquals(value, standard)
                || (value is JsonObject { Count: 0 } && standard is JsonObject));
            if (isDefault && name != "Id")
                obj.Remove(name);
        }
    }

    /// <summary>
    /// Applies a JSON Patch (RFC 6902: add, remove, replace, move, copy, test) to a copy of <paramref name="document"/>,
    /// all or nothing. Paths are JSON Pointers (/Actions/4/DelayMs). Throws <see cref="FormatException"/> saying which
    /// operation is wrong and why.
    /// </summary>
    public static JsonNode Patch(JsonNode document, JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
            throw new FormatException("\"patch\" must be a list of operations, e.g. [{\"op\": \"replace\", \"path\": \"/Actions/4/DelayMs\", \"value\": 250}].");
        JsonNode root = document.DeepClone();
        int index = 0;
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            index++;
            string op = Text(operation, "op") ?? "";
            string path = Text(operation, "path") ?? "";
            try
            {
                Apply(root, op, path, operation);
            }
            catch (FormatException e)
            {
                throw new FormatException($"Operation {index} ({op} {path}): {e.Message} Nothing was changed.");
            }
        }
        return root;
    }

    private static void Apply(JsonNode root, string op, string path, JsonElement operation)
    {
        JsonNode? Value() => operation.TryGetProperty("value", out JsonElement value)
            ? JsonNode.Parse(value.GetRawText())
            : throw new FormatException("it needs a \"value\".");
        string From() => Text(operation, "from") ?? throw new FormatException("it needs a \"from\" path.");

        switch (op)
        {
            case "add":
                Add(root, path, Value());
                break;
            case "remove":
                Remove(root, path);
                break;
            case "replace":
                Get(root, path); // must exist
                Remove(root, path);
                Add(root, path, Value());
                break;
            case "move":
                string from = From();
                if (path.StartsWith(from + "/", StringComparison.Ordinal))
                    throw new FormatException("a value can't be moved inside itself.");
                JsonNode? moved = Get(root, from)?.DeepClone();
                Remove(root, from);
                Add(root, path, moved);
                break;
            case "copy":
                Add(root, path, Get(root, From())?.DeepClone());
                break;
            case "test":
                if (!JsonNode.DeepEquals(Get(root, path), Value()))
                    throw new FormatException("the value there is not the one given.");
                break;
            default:
                throw new FormatException("\"op\" must be add, remove, replace, move, copy or test.");
        }
    }

    /// <summary>The parent of the path's last token, and that token.</summary>
    private static (JsonNode Parent, string Token) Parent(JsonNode root, string path)
    {
        if (path.Length == 0 || path[0] != '/')
            throw new FormatException("\"path\" must be a JSON Pointer starting with /, e.g. /Actions/0/DelayMs.");
        string[] tokens = path[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode current = root;
        foreach (string token in tokens[..^1])
            current = Child(current, token) ?? throw new FormatException($"there is nothing at \"{token}\".");
        return (current, tokens[^1]);
    }

    private static JsonNode? Child(JsonNode node, string token) => node switch
    {
        JsonObject obj when obj.TryGetPropertyValue(token, out JsonNode? value) => value,
        JsonArray array when int.TryParse(token, out int i) && i >= 0 && i < array.Count => array[i],
        JsonObject => throw new FormatException($"there is no field \"{token}\"."),
        JsonArray array => throw new FormatException($"\"{token}\" is not an index of the list (0 to {array.Count - 1})."),
        _ => throw new FormatException($"\"{token}\" is inside a value that is not an object or a list."),
    };

    private static JsonNode? Get(JsonNode root, string path)
    {
        (JsonNode parent, string token) = Parent(root, path);
        return Child(parent, token);
    }

    private static void Add(JsonNode root, string path, JsonNode? value)
    {
        (JsonNode parent, string token) = Parent(root, path);
        switch (parent)
        {
            case JsonObject obj:
                obj[token] = value;
                break;
            case JsonArray array when token == "-":
                array.Add(value);
                break;
            case JsonArray array when int.TryParse(token, out int i) && i >= 0 && i <= array.Count:
                array.Insert(i, value);
                break;
            case JsonArray array:
                throw new FormatException($"\"{token}\" is not a place in the list (0 to {array.Count}, or - for the end).");
            default:
                throw new FormatException("the parent is not an object or a list.");
        }
    }

    private static void Remove(JsonNode root, string path)
    {
        (JsonNode parent, string token) = Parent(root, path);
        Child(parent, token); // must exist
        if (parent is JsonObject obj)
            obj.Remove(token);
        else if (parent is JsonArray array)
            array.RemoveAt(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
