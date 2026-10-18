using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PuppyMacro.Models;

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

    /// <summary>
    /// Sets <paramref name="set"/>'s fields on every action of a copy of <paramref name="document"/> (a loop or macro) whose
    /// fields equal <paramref name="where"/>'s, and that is in the macro group named <paramref name="group"/> when given.
    /// The document is the item's whole JSON (every field there). Throws <see cref="FormatException"/> saying why it can't.
    /// </summary>
    public static (JsonNode Result, int Count) SetWhere(JsonNode document, JsonElement where, JsonElement set, string? group)
    {
        if (set.ValueKind != JsonValueKind.Object || !set.EnumerateObject().Any())
            throw new FormatException("\"set\" must be an object with the fields to change, e.g. {\"DelayMs\": 1200}.");
        if (where.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new FormatException("\"where\" must be an object with the fields to match, e.g. {\"DelayMs\": 800}.");
        JsonNode root = document.DeepClone();
        if (root["Actions"] is not JsonArray actions)
            throw new FormatException("this item has no actions.");

        string? groupId = null;
        if (!string.IsNullOrWhiteSpace(group))
        {
            JsonNode? found = (root["Groups"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(g => string.Equals(g["Name"]?.GetValue<string>(), group.Trim(), StringComparison.OrdinalIgnoreCase));
            groupId = found?["Id"]?.GetValue<string>() ?? throw new FormatException($"no group is named \"{group}\".");
        }

        var match = where.ValueKind == JsonValueKind.Object
            ? where.EnumerateObject().Select(p => (p.Name, Value: JsonNode.Parse(p.Value.GetRawText()))).ToList()
            : new List<(string Name, JsonNode? Value)>();
        var changes = set.EnumerateObject().Select(p => (p.Name, Value: JsonNode.Parse(p.Value.GetRawText()))).ToList();
        int count = 0;
        // The actions as they are before any change: a new value can hold actions (an If's Then), never walked into.
        foreach (JsonObject action in AllActions(actions).ToList())
        {
            if (groupId != null && !string.Equals(action["GroupId"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase))
                continue;
            // A row linked to a Library component has only its link: it is left as it is.
            if (action["Type"]?.GetValue<string>() == nameof(MacroActionType.Component))
                continue;
            if (!match.All(m => action.ContainsKey(m.Name) && JsonNode.DeepEquals(action[m.Name], m.Value)))
                continue;
            foreach ((string name, JsonNode? value) in changes)
            {
                if (!action.ContainsKey(name))
                    throw new FormatException($"an action has no field \"{name}\".");
                action[name] = value?.DeepClone();
            }
            count++;
        }
        if (count == 0)
            throw new FormatException("no action matches. get_item shows the actions.");
        return (root, count);
    }

    /// <summary>Every action of a macro's JSON, also those inside an If's Then and Else.</summary>
    private static IEnumerable<JsonObject> AllActions(JsonArray actions)
    {
        foreach (JsonObject action in actions.OfType<JsonObject>().ToList())
        {
            yield return action;
            if (action["If"] is not JsonObject block)
                continue;
            foreach (string branch in new[] { "Then", "Else" })
                if (block[branch] is JsonArray inner)
                    foreach (JsonObject each in AllActions(inner))
                        yield return each;
        }
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
