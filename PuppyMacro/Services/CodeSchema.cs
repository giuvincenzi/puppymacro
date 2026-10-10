using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// JSON Schemas of the Code views (a macro, one action, a loop, a remap), generated from the models
/// with <see cref="CodeJson.StrictOptions"/>. The editor uses them for Ctrl+Space suggestions,
/// descriptions and inline errors; <see cref="MacroJson"/>, <see cref="LoopJson"/> and
/// <see cref="RemapJson"/> stay the checks that decide.
/// </summary>
internal static class CodeSchema
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["MacroDefinition.Id"] = "The macro's id: the name of its file. It cannot be changed.",
        ["MacroDefinition.Name"] = "Name shown in the Macros list.",
        ["MacroDefinition.Actions"] = "The steps, in playback order.",
        ["MacroDefinition.Groups"] = "Named groups of consecutive actions (GroupId). Playback ignores them.",
        ["MacroDefinition.Repeat"] = "Once, Loop (until stopped) or Times (RepeatCount times).",
        ["MacroDefinition.RepeatCount"] = "How many times it plays with Repeat \"Times\".",
        ["MacroDefinition.Speed"] = "Playback speed: 0.25, 0.5, 1, 2 or 4.",
        ["MacroDefinition.Mode"] = "Toggle: the hotkey starts and stops it. Hold: it plays only while the hotkey is held.",
        ["MacroDefinition.Hotkey"] = "Hotkey, or null for none.",
        ["MacroDefinition.Enabled"] = "Off: the hotkey and the floating button do nothing.",
        ["MacroDefinition.SoundEnabled"] = "Plays SoundName when the macro starts and stops.",
        ["MacroDefinition.SoundName"] = "One of the built-in sounds.",
        ["MacroDefinition.FloatingButton"] = "Round button shown in overlay mode (Toggle only).",
        ["MacroDefinition.AppExe"] = "File name of the app it works in, like \"notepad.exe\"; null for all apps. It stops when another app comes in front.",
        ["MacroDefinition.ShowInOverlay"] = "Shown in overlay mode: its row in the overlay panel, or its floating button.",
        ["MacroDefinition.HideInOverlayWhenDisabled"] = "Left out of the overlay while it is disabled. Off: shown as disabled.",
        ["MacroDefinition.OverlayClickPassesThrough"] = "A click on it in the overlay also clicks the window under it.",

        ["MacroAction.Type"] = "What the step does.",
        ["MacroAction.DelayMs"] = "Wait before the step, in ms, from the end of the previous step.",
        ["MacroAction.Name"] = "Optional name shown in the editor instead of the description.",
        ["MacroAction.GroupId"] = "Id of one of the macro's Groups, or null.",
        ["MacroAction.Vk"] = "PressKey, KeyDown, KeyUp: virtual-key code of the key (65 = A, 112 = F1). Click, MouseDown, MouseUp: 1 left, 2 right, 4 middle, 5 back, 6 forward.",
        ["MacroAction.HoldMs"] = "PressKey, Click: how long the key or button stays down, in ms.",
        ["MacroAction.ClickCount"] = "Click: 1 single click, 2 double click.",
        ["MacroAction.Repeat"] = "PressKey, Click: how many times to press.",
        ["MacroAction.RepeatPauseMs"] = "PressKey, Click: pause between repeated presses, in ms.",
        ["MacroAction.X"] = "Click, MouseDown, MouseUp, MoveTo: screen position in physical pixels.",
        ["MacroAction.Y"] = "Click, MouseDown, MouseUp, MoveTo: screen position in physical pixels.",
        ["MacroAction.Smooth"] = "MoveTo: travel to the point instead of jumping.",
        ["MacroAction.SmoothSpeed"] = "MoveTo with Smooth: 1 (very slow) to 5 (very fast).",
        ["MacroAction.Path"] = "MovePath: the recorded points, T ms after the path starts.",
        ["MacroAction.ScrollDirection"] = "Scroll: Up, Down, Left or Right.",
        ["MacroAction.ScrollSteps"] = "Scroll: how many wheel notches.",
        ["MacroAction.Text"] = "PasteText: the text to paste.",
        ["MacroAction.EnterBefore"] = "PasteText: press Enter before pasting.",
        ["MacroAction.EnterAfter"] = "PasteText: press Enter after pasting.",

        ["MacroGroup.Id"] = "Referenced by the actions' GroupId.",
        ["MacroGroup.Name"] = "Name shown in the editor.",
        ["MacroGroup.Collapsed"] = "The editor shows only the group's header.",

        ["HotkeyBinding.Vk"] = "Virtual-key code (1 to 254), or the scroll wheel: 256 up, 257 down, 258 left, 259 right. Left and right click, the wheel and Esc (27) need Ctrl, Alt, Shift or Win.",
        ["HotkeyBinding.Ctrl"] = "Ctrl held with the key.",
        ["HotkeyBinding.Alt"] = "Alt held with the key.",
        ["HotkeyBinding.Shift"] = "Shift held with the key.",
        ["HotkeyBinding.Win"] = "Win held with the key.",

        ["FloatingButton.Enabled"] = "Show the floating button in overlay mode (not with Hold).",
        ["FloatingButton.Label"] = "One or two characters shown in the button. Empty: from the name.",
        ["FloatingButton.Size"] = "Small, Medium or Large.",
        ["FloatingButton.X"] = "Position (WPF units), or null with Y for the middle of the main screen.",
        ["FloatingButton.Y"] = "Position (WPF units), or null with X for the middle of the main screen.",
        ["FloatingButton.Opacity"] = "Background opacity, in percent.",

        ["LoopDefinition.Id"] = "The loop's id. It cannot be changed.",
        ["LoopDefinition.Name"] = "Name shown in the Loops list.",
        ["LoopDefinition.Actions"] = "The rows: keys or texts, each on its own interval.",
        ["LoopDefinition.Mode"] = "Toggle: the hotkey starts and stops it. Hold: it runs only while the hotkey is held.",
        ["LoopDefinition.Hotkey"] = "Hotkey, or null for none.",
        ["LoopDefinition.Enabled"] = "Off: the hotkey and the floating button do nothing.",
        ["LoopDefinition.SoundEnabled"] = "Plays SoundName when the loop starts and stops.",
        ["LoopDefinition.SoundName"] = "One of the built-in sounds.",
        ["LoopDefinition.FloatingButton"] = "Round button shown in overlay mode (Toggle only).",
        ["LoopDefinition.AppExe"] = "File name of the app it works in, like \"notepad.exe\"; null for all apps. It stops when another app comes in front.",
        ["LoopDefinition.ShowInOverlay"] = "Shown in overlay mode: its row in the overlay panel, or its floating button.",
        ["LoopDefinition.HideInOverlayWhenDisabled"] = "Left out of the overlay while it is disabled. Off: shown as disabled.",
        ["LoopDefinition.OverlayClickPassesThrough"] = "A click on it in the overlay also clicks the window under it.",

        ["LoopAction.Type"] = "Key: press a key or mouse button. Text: paste a text.",
        ["LoopAction.KeyVk"] = "Key rows: virtual-key code of the key or mouse button (65 = A, 112 = F1, 1 = left button).",
        ["LoopAction.Text"] = "Text rows: the text to paste.",
        ["LoopAction.EnterBefore"] = "Text rows: press Enter before pasting.",
        ["LoopAction.EnterAfter"] = "Text rows: press Enter after pasting.",
        ["LoopAction.HoldDown"] = "Key rows: keep the key held down while the loop runs, instead of repeating it.",
        ["LoopAction.IntervalValue"] = "Repeat every IntervalValue IntervalUnit (10 ms to 24 h).",
        ["LoopAction.IntervalUnit"] = "Milliseconds, Seconds, Minutes or Hours.",

        ["RemapDefinition.Id"] = "The remap's id. It cannot be changed.",
        ["RemapDefinition.SourceVk"] = "Virtual-key code of the key or mouse button you press (65 = A, 112 = F1, 5 = back button).",
        ["RemapDefinition.Target"] = "The key, mouse button or combination sent instead.",
        ["RemapDefinition.AppExe"] = "File name of the app it works in, like \"notepad.exe\"; null for all apps.",
        ["RemapDefinition.Note"] = "Optional note shown in the Remap list.",
        ["RemapDefinition.Enabled"] = "Off: the key works as usual.",

        ["PathPoint.X"] = "Screen position in physical pixels.",
        ["PathPoint.Y"] = "Screen position in physical pixels.",
        ["PathPoint.T"] = "Time from the start of the path, in ms.",
    };

    private static readonly Dictionary<string, (double Min, double Max)> Ranges = new()
    {
        ["MacroDefinition.RepeatCount"] = MacroJson.RepeatCount,
        ["MacroAction.DelayMs"] = (0, MacroJson.MaxDelayMs),
        ["MacroAction.HoldMs"] = MacroJson.HoldMs,
        ["MacroAction.ClickCount"] = (1, 2),
        ["MacroAction.Repeat"] = MacroJson.Repeat,
        ["MacroAction.RepeatPauseMs"] = MacroJson.RepeatPauseMs,
        ["MacroAction.X"] = MacroJson.Coordinate,
        ["MacroAction.Y"] = MacroJson.Coordinate,
        ["MacroAction.SmoothSpeed"] = MacroJson.SmoothSpeed,
        ["MacroAction.ScrollSteps"] = MacroJson.ScrollSteps,
        ["FloatingButton.Opacity"] = (AppSettings.MinOpacity, AppSettings.MaxOpacity),
        ["PathPoint.T"] = (0, double.MaxValue),
        ["LoopAction.KeyVk"] = (0, 254),
        ["LoopAction.IntervalValue"] = (0, double.MaxValue),
        ["RemapDefinition.SourceVk"] = (1, 254),
    };

    private static readonly Dictionary<MacroActionType, string> ActionTypes = new()
    {
        [MacroActionType.PressKey] = "Press and release a key (Vk), held HoldMs; Repeat presses it more times.",
        [MacroActionType.KeyDown] = "Press a key (Vk) and keep it down.",
        [MacroActionType.KeyUp] = "Release a key (Vk).",
        [MacroActionType.Click] = "Click a mouse button (Vk) at X, Y; ClickCount 2 for a double click.",
        [MacroActionType.MouseDown] = "Press a mouse button (Vk) at X, Y and keep it down.",
        [MacroActionType.MouseUp] = "Release a mouse button (Vk) at X, Y.",
        [MacroActionType.MoveTo] = "Move the pointer to X, Y; Smooth travels there.",
        [MacroActionType.MovePath] = "Follow a recorded mouse path (Path).",
        [MacroActionType.Scroll] = "Turn the scroll wheel ScrollSteps notches in ScrollDirection.",
        [MacroActionType.PasteText] = "Paste Text, with Enter before or after if set.",
    };

    /// <summary>A whole macro: its Id is the only value allowed, and suggested.</summary>
    public static string ForMacro(System.Guid id) => Build(typeof(MacroDefinition), schema => FixId(schema, id));

    /// <summary>A loop: its Id is the only value allowed, and suggested. The fields of version 1.0 are left out.</summary>
    public static string ForLoop(System.Guid id) => Build(typeof(LoopDefinition), schema =>
    {
        FixId(schema, id);
        if (schema["properties"] is JsonObject properties)
        {
            properties.Remove("KeyVk");
            properties.Remove("IntervalMs");
            properties.Remove("HotkeyVk");
        }
    });

    /// <summary>A remap: its Id is the only value allowed, and suggested.</summary>
    public static string ForRemap(System.Guid id) => Build(typeof(RemapDefinition), schema => FixId(schema, id));

    /// <summary>
    /// One macro action: its GroupId can be one of <paramref name="allowedGroups"/> (see
    /// <see cref="MacroJson.AllowedGroupIds"/>), suggested with the group names.
    /// </summary>
    public static string ForAction(IReadOnlyList<System.Guid?> allowedGroups, IReadOnlyDictionary<System.Guid, string> groupNames,
        System.Guid? current) => Build(typeof(MacroAction), schema =>
    {
        if (schema["properties"]?["GroupId"] is not JsonObject group)
            return;
        group["enum"] = new JsonArray(allowedGroups.Select(g => (JsonNode?)(g is System.Guid id ? JsonValue.Create(id.ToString()) : null)).ToArray());
        group["enumDescriptions"] = new JsonArray(allowedGroups
            .Select(g => (JsonNode?)JsonValue.Create(g is System.Guid id && groupNames.TryGetValue(id, out string? name) ? $"Group \"{name}\"" : "No group"))
            .ToArray());
        group["default"] = current?.ToString();
    });

    private static string Build(System.Type type, System.Action<JsonObject> customize)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
        JsonObject schema = CodeJson.StrictOptions.GetJsonSchemaAsNode(type, exporter).AsObject();
        customize(schema);
        return schema.ToJsonString();
    }

    /// <summary>A deleted or changed Id comes back with Ctrl+Space.</summary>
    private static void FixId(JsonObject schema, System.Guid id)
    {
        if (schema["properties"]?["Id"] is not JsonObject idSchema)
            return;
        idSchema["default"] = id.ToString();
        idSchema["enum"] = new JsonArray(JsonValue.Create(id.ToString()));
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        // Enums are read by CodeJson's own converter, so the exporter only says "true" (anything).
        if (context.TypeInfo.Type.IsEnum)
        {
            node = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(System.Enum.GetNames(context.TypeInfo.Type).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            };
        }
        if (node is not JsonObject schema)
            return node;

        // Unknown properties are problems (CodeJson.StrictOptions); the exporter does not say so.
        if (schema.ContainsKey("properties"))
            schema["additionalProperties"] = false;

        if (context.PropertyInfo is { } property)
        {
            string key = $"{property.DeclaringType.Name}.{property.Name}";
            if (Descriptions.TryGetValue(key, out string? description))
                schema["description"] = description;
            if (Ranges.TryGetValue(key, out var range))
            {
                schema["minimum"] = range.Min;
                if (range.Max < double.MaxValue)
                    schema["maximum"] = range.Max;
            }
            switch (key)
            {
                case "MacroDefinition.Speed":
                    schema["enum"] = new JsonArray(MacroDefinition.SpeedSteps.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                    break;
                case "MacroDefinition.SoundName":
                case "LoopDefinition.SoundName":
                    schema["enum"] = new JsonArray(SoundService.Names.Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
                    break;
                case "FloatingButton.Label":
                    schema["maxLength"] = FloatingButton.MaxLabelLength;
                    break;
            }
        }

        // Descriptions of the action types in the Ctrl+Space list (a Monaco/VS Code schema extension).
        if (context.TypeInfo.Type == typeof(MacroActionType) && schema["enum"] is JsonArray values)
        {
            schema["enumDescriptions"] = new JsonArray(values
                .Select(v => (JsonNode)JsonValue.Create(
                    System.Enum.TryParse(v?.GetValue<string>(), out MacroActionType type) && ActionTypes.TryGetValue(type, out string? text) ? text : ""))
                .ToArray());
        }
        return schema;
    }
}
