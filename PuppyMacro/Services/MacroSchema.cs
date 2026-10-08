using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// JSON Schema of a macro file, generated from the models with <see cref="MacroJson.StrictOptions"/>.
/// The Code view's editor uses it for Ctrl+Space suggestions, descriptions and inline errors;
/// <see cref="MacroJson.Parse"/> stays the check that decides.
/// </summary>
internal static class MacroSchema
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

    public static string Build()
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
        JsonNode schema = MacroJson.StrictOptions.GetJsonSchemaAsNode(typeof(MacroDefinition), exporter);
        return schema.ToJsonString();
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        // Enums are read by MacroJson's own converter, so the exporter only says "true" (anything).
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

        // Unknown properties are problems (MacroJson.StrictOptions); the exporter does not say so.
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
