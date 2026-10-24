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
        ["MacroAction.If"] = "If: its conditions, how long it waits for them, and the actions of Then and Else. Only on If actions.",
        ["MacroAction.FoundId"] = "Click, MouseDown, MouseUp, MoveTo inside an If's Then: the Id of an Image or Text condition of that If (or of an If around it); the action goes to the center of what it found, moved by FoundOffsetX and FoundOffsetY, instead of X, Y. Null = X, Y.",
        ["MacroAction.FoundOffsetX"] = "With FoundId: pixels to the right of the center of what was found (negative: to the left).",
        ["MacroAction.FoundOffsetY"] = "With FoundId: pixels below the center of what was found (negative: above).",
        ["MacroAction.Cell"] = "Click, MouseDown, MouseUp, MoveTo inside a component, with Bind \"Point\" to a Grid parameter: which cell of the grid, counted row by row from the top left, starting at 1 (Bind \"Cell\" to a Number Count parameter gives it per call). X, Y keep that cell's point in the Example. Every call must reach a cell its grid has. Null: the point is X, Y.",
        ["MacroAction.ComponentId"] = "Component: the Id of the Library component this row calls; it plays the component's actions as they are in the Library, with Arguments. Only on Component rows, which have no DelayMs or Name of their own.",
        ["MacroAction.Arguments"] = "Component rows: the value of each of the component's Parameters, by name: a value of its type (Point {\"X\", \"Y\"} in physical pixels; Number, in its Unit; Text; Key, written as a hotkey; Color \"#RRGGBB\"; Image [{\"Id\", \"X\", \"Y\"}]; Area {\"X\", \"Y\", \"Width\", \"Height\"}; Grid {\"First\": {\"X\", \"Y\"}, \"Last\": {\"X\", \"Y\"}, \"Columns\", \"Rows\"}), " +
            "{\"From\": \"a parameter\"} (inside a component: one of its own parameters of the same type, passed on) or " +
            "{\"Found\": \"a condition's Id\", \"OffsetX\": 0, \"OffsetY\": 0} (a Point: where an Image, Text or Component condition of an If " +
            "around the row found something, this time). A parameter with a Default can be left out.",
        ["MacroAction.Bind"] = "Inside a component only: fields of this action that take a value of one of the component's Parameters, by slot " +
            "(\"Point\": \"Slot\"). Slots: DelayMs (Number, Ms) on every action but a Component row; PressKey: Key, HoldMs (Ms), Repeat " +
            "(Count), RepeatPauseMs (Ms); KeyDown, KeyUp: Key (alone); Click: Point (without FoundId), HoldMs, Repeat, RepeatPauseMs; " +
            "MouseDown, MouseUp, MoveTo: Point (without FoundId); Scroll: ScrollSteps (Count); PasteText: Text; If: WaitSeconds " +
            "(Seconds, with Wait UpTo). The parameter has the slot's type and unit, except Point, which also takes a Grid when the " +
            "action has a Cell; then Cell (Count) can take a Number too. The fields keep the parameter's Example.",

        ["MacroIf.Conditions"] = "What to check on the screen. At least one.",
        ["MacroIf.Match"] = "All: Then runs when every condition is true. Any: when at least one is.",
        ["MacroIf.Wait"] = "Once: checks once, without waiting. UpTo: checks every time the screen changes, up to WaitSeconds. Forever: waits as long as it takes.",
        ["MacroIf.WaitSeconds"] = "With Wait UpTo: the longest wait, in seconds. The macro's Speed does not change it.",
        ["MacroIf.OnFail"] = "When the conditions are not true in time: Else runs the Else actions, then the macro goes on; Stop stops the macro.",
        ["MacroIf.Then"] = "Actions run when the conditions are true, then the macro goes on after the If.",
        ["MacroIf.Else"] = "Actions run when the conditions are not true in time (OnFail Else).",

        ["MacroCondition.Id"] = "Referenced by FoundId of the mouse actions in Then. A new one for a new condition.",
        ["MacroCondition.Kind"] = "Pixel: a pixel has a color. Image: an image is on the screen. Text: a text is on the screen (Windows' text recognition, slower). Component: a Library component that says what it finds (Returns), with Arguments.",
        ["MacroCondition.ComponentId"] = "Component: the Id of a Library component with Returns: true when the condition it returns is, checked with Arguments as the If checks any condition; it gives FoundId the place that condition finds (an Image or Text without Not there).",
        ["MacroCondition.Arguments"] = "Component: the values of the component's Parameters, written as a Component row's Arguments.",
        ["MacroCondition.Bind"] = "Inside a component only: fields of this condition that take a value of one of the component's Parameters, by slot. Pixel: Point (a Point, or a Grid with Cell), Cell (Count), Color, Tolerance (Number, Percent); Image: Images, Similarity (Percent), Area (with Area Drawn or Screen); Text: Text, Area. The fields keep the parameter's Example.",
        ["MacroCondition.Cell"] = "Pixel inside a component, with Bind \"Point\" to a Grid parameter: which cell of the grid, counted row by row from the top left, starting at 1 (Bind \"Cell\" gives it per call). Null: the point is X, Y.",
        ["MacroCondition.Not"] = "True when what it looks for is not there: Pixel, the pixel has another color; Image, none of the images is on the screen; Text, the text is not on the screen. Its If's Up to then waits for it to go away. A condition with Not gives no place to FoundId. False by default.",
        ["MacroCondition.Name"] = "Image, Text, Component: a name of your choice, unique in the macro, shown where the actions use what it found.",

        ["LibraryComponent.Id"] = "The component's id: rows and conditions call it by it. It never changes.",
        ["LibraryComponent.Name"] = "The name people read, in the user's words (\"Remove one affix\").",
        ["LibraryComponent.Purpose"] = "Find (recognizes something), Wait (waits until something shows or goes), Read (reads a text), State (which screen the app is in), Point (where to click or move), Act (a sequence that does an operation).",
        ["LibraryComponent.Concept"] = "The last part of its structured name, in English: lowercase letters, digits and _ (\"remove_one_affix\").",
        ["LibraryComponent.Description"] = "A short English description, for AI apps (optional).",
        ["LibraryComponent.Parameters"] = "Its signature: the values a call gives it, each bound to fields of its actions (Bind). Empty: it takes none.",
        ["LibraryComponent.Returns"] = "What it finds, when it can be used as a Component condition: one of its conditions (Found, its Id). Null: it can't be a condition.",
        ["LibraryComponent.Actions"] = "Its actions, as a macro's, self-contained: a FoundId or a {\"Found\"} argument uses a condition inside it; what comes from outside comes as a parameter. Rows of type Component call other components (no loop, at most 10 levels).",
        ["ComponentParameter.Name"] = "Letters, digits and _, starting with a letter, up to 40; unique in the component. Calls give the value by this name.",
        ["ComponentParameter.Type"] = "Point, Number (with Unit), Text, Key, Color, Image, Area or Grid: a slot takes only its own type. A Grid is cells all alike in rows, as an inventory, {\"First\": {\"X\", \"Y\"}, \"Last\": {\"X\", \"Y\"}, \"Columns\", \"Rows\"} (the same point of the top-left and of the bottom-right cell; 1 to 1000 columns and rows, at most 10000 cells; with one column First.X = Last.X, with one row First.Y = Last.Y); a Point slot takes one of its cells (Cell).",
        ["ComponentParameter.Unit"] = "Number only: Pixels, Ms, Seconds, Count or Percent; a slot takes only its own unit.",
        ["ComponentParameter.Example"] = "A value of its type: what its bound fields hold, the editor shows and a test of the component plays.",
        ["ComponentParameter.Default"] = "The value, of its type, that a call leaving it out gives; left out (null): every call gives one.",
        ["ComponentParameter.Description"] = "A short English description, for AI apps (optional).",
        ["ComponentReturns.Found"] = "The Id of one of its conditions: a Component condition calling it is true when that one is, and finds the place it finds.",
        ["MacroCondition.X"] = "Pixel: the pixel's screen position, in physical pixels.",
        ["MacroCondition.Y"] = "Pixel: the pixel's screen position, in physical pixels.",
        ["MacroCondition.Color"] = "Pixel: the color, as # and six hexadecimal digits (#3BD16F).",
        ["MacroCondition.Tolerance"] = "Pixel: how much red, green and blue may each differ from Color, in percent of 255; 0 = exactly Color. Usually 5.",
        ["MacroCondition.Images"] = "Image: the images to look for, taken by the user in PuppyMacro (only the user can take one). True when one of them is on the screen; the first found is where the actions in Then go.",
        ["MacroCondition.NearbySizes"] = "Image: also look for the images a little bigger or smaller (85% to 115%), for things that change size (zoom, distance). Slower.",
        ["ConditionImage.Id"] = "The image's id (64 hexadecimal digits).",
        ["ConditionImage.X"] = "Where the image was taken (its top-left corner), in physical pixels; Area Around looks near it.",
        ["ConditionImage.Y"] = "Where the image was taken (its top-left corner), in physical pixels.",
        ["MacroDefinition.Debug"] = "Keeps the log of the macro's last run: each action, what each If saw and where it went, why it ended, and pictures of the screen when an If was not true and at the end. In English, in a folder that holds only the last run and is deleted when PuppyMacro closes; get_last_run reads it. Writing it does not slow the macro. A test_macro run is always logged, Debug or not.",
        ["MacroDefinition.ChecksPerSecond"] = "How many times a second its screen conditions look while they wait: 10, 30, 60, or 0 for every new frame. More checks notice changes sooner and use more of the computer.",
        ["MacroCondition.Area"] = "Image, Text: where to look. Around (Image only): a little larger than where each image was taken (it does not follow a window that moves). Drawn: the area AreaX, AreaY, AreaWidth, AreaHeight. Screen: the whole screen with those bounds. A smaller area is faster.",
        ["MacroCondition.AreaX"] = "Area Drawn or Screen: left edge, in physical pixels.",
        ["MacroCondition.AreaY"] = "Area Drawn or Screen: top edge, in physical pixels.",
        ["MacroCondition.AreaWidth"] = "Area Drawn or Screen: width, in physical pixels.",
        ["MacroCondition.AreaHeight"] = "Area Drawn or Screen: height, in physical pixels.",
        ["MacroCondition.Similarity"] = "Image: how closely the screen must look like the image, in percent. Usually 90.",
        ["MacroCondition.Text"] = "Text: the text to look for.",
        ["MacroCondition.TextMatch"] = "Text: Contains (other words may be around it) or Exact (a line with only this text).",
        ["MacroCondition.MatchCase"] = "Text: uppercase and lowercase must match.",
        ["MacroCondition.Language"] = "Text: the language of the text, as a tag like \"en-US\" (one of the languages Windows can read on this PC); null = Windows' language.",

        ["MacroGroup.Id"] = "Referenced by the actions' GroupId.",
        ["MacroGroup.Name"] = "Name shown in the editor.",
        ["MacroGroup.Collapsed"] = "The editor shows only the group's header.",

        ["HotkeyBinding.Vk"] = "Virtual-key code (1 to 254), or the scroll wheel: 256 up, 257 down, 258 left, 259 right. Left and right click, the wheel and Esc (27) need Ctrl, Alt, Shift or Win.",
        ["HotkeyBinding.Ctrl"] = "Ctrl held with the key.",
        ["HotkeyBinding.Alt"] = "Alt held with the key.",
        ["HotkeyBinding.Shift"] = "Shift held with the key.",
        ["HotkeyBinding.Win"] = "Win held with the key.",
        ["HotkeyBinding.CtrlSide"] = "With Ctrl: Left or Right for only that Ctrl key; Any (or left out) for either.",
        ["HotkeyBinding.AltSide"] = "With Alt: Left or Right for only that Alt key; Any (or left out) for either.",
        ["HotkeyBinding.ShiftSide"] = "With Shift: Left or Right for only that Shift key; Any (or left out) for either.",
        ["HotkeyBinding.WinSide"] = "With Win: Left or Right for only that Win key; Any (or left out) for either.",

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

        ["LoopAction.Type"] = "Key: Press key (a key or combination) or Click (when KeyVk is a mouse button). Text: Paste text.",
        ["LoopAction.KeyVk"] = "Key rows: virtual-key code of the key (65 = A, 112 = F1) or of the mouse button (1 left, 2 right, 4 middle, 5 back, 6 forward).",
        ["LoopAction.Text"] = "Text rows: the text to paste.",
        ["LoopAction.EnterBefore"] = "Text rows: press Enter before pasting.",
        ["LoopAction.EnterAfter"] = "Text rows: press Enter after pasting.",
        ["LoopAction.HoldDown"] = "Key rows: keep the key held down while the loop runs, instead of repeating it.",
        ["LoopAction.IntervalValue"] = "Repeat every IntervalValue IntervalUnit (10 ms to 24 h).",
        ["LoopAction.IntervalUnit"] = "Milliseconds, Seconds, Minutes or Hours.",
        ["LoopAction.HoldMs"] = "Key rows: how long each press is held, in ms (at most half the interval); null = 30.",
        ["LoopAction.Repeat"] = "Key rows: how many times to press each time; null = 1.",
        ["LoopAction.RepeatPauseMs"] = "Key rows: pause between those presses, in ms; null = 50.",
        ["LoopAction.ClickCount"] = "Click rows: 2 for a double click; null = single.",
        ["LoopAction.X"] = "Click rows: the fixed point clicked, in physical pixels; null with Y = where the pointer is.",
        ["LoopAction.Y"] = "Click rows: the fixed point clicked, in physical pixels; null with X = where the pointer is.",

        ["KeyModifierFields.Ctrl"] = "Press key (loop key rows, macro PressKey): Ctrl held with the key.",
        ["KeyModifierFields.Alt"] = "Press key: Alt held with the key.",
        ["KeyModifierFields.Shift"] = "Press key: Shift held with the key.",
        ["KeyModifierFields.Win"] = "Press key: Win held with the key.",
        ["KeyModifierFields.CtrlSide"] = "With Ctrl: Left or Right key; Any (or left out) sends the left one.",
        ["KeyModifierFields.AltSide"] = "With Alt: Left or Right key; Any (or left out) sends the left one.",
        ["KeyModifierFields.ShiftSide"] = "With Shift: Left or Right key; Any (or left out) sends the left one.",
        ["KeyModifierFields.WinSide"] = "With Win: Left or Right key; Any (or left out) sends the left one.",

        ["RemapDefinition.Id"] = "The remap's id. It cannot be changed.",
        ["RemapDefinition.Source"] = "The key, mouse button or combination you press (Vk 65 = A, 112 = F1, 5 = back button), or Ctrl, Alt, Shift or Win alone by its key (160, 161 Shift; 162, 163 Ctrl; 164, 165 Alt; 91, 92 Win).",
        ["RemapDefinition.Target"] = "The key, mouse button or combination sent instead, or Ctrl, Alt, Shift or Win alone by its key.",
        ["RemapDefinition.AppExe"] = "File name of the app it works in, like \"notepad.exe\"; null for all apps.",
        ["RemapDefinition.PassExtraModifiers"] = "Only when Source is a key or a button, not Ctrl, Alt, Shift or Win alone. On (the default for a new remap): the Source also works with other Ctrl, Alt, Shift or Win held, and those reach the app with the Target (Source Alt+A, Target Left: Shift+Alt+A sends Shift+Left); an exact match, another remap or a hotkey, always wins. Off: only the exact Source works.",
        ["RemapDefinition.KeepModifiersHeld"] = "Only when both Source and Target have Ctrl, Alt, Shift or Win. On (the default): the Target's stay held while the Source's are, so its key can be pressed again (Ctrl+Tab sending Alt+Tab moves on in the Alt+Tab switcher at each Tab); releasing any of the Source's releases them all, and another key gets the Source's back. Off: the Target's are released with its key.",
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
        ["MacroAction.FoundOffsetX"] = MacroJson.Coordinate,
        ["MacroAction.FoundOffsetY"] = MacroJson.Coordinate,
        ["MacroAction.Cell"] = (1, Components.GridValue.MaxCells),
        ["MacroCondition.Cell"] = (1, Components.GridValue.MaxCells),
        ["MacroCondition.X"] = MacroJson.Coordinate,
        ["MacroCondition.Y"] = MacroJson.Coordinate,
        ["MacroCondition.Tolerance"] = MacroJson.Tolerance,
        ["MacroCondition.Similarity"] = MacroJson.Similarity,
        ["ConditionImage.X"] = MacroJson.Coordinate,
        ["ConditionImage.Y"] = MacroJson.Coordinate,
        ["MacroCondition.AreaX"] = MacroJson.Coordinate,
        ["MacroCondition.AreaY"] = MacroJson.Coordinate,
        ["MacroCondition.AreaWidth"] = MacroJson.AreaSize,
        ["MacroCondition.AreaHeight"] = MacroJson.AreaSize,
        ["FloatingButton.Opacity"] = (AppSettings.MinOpacity, AppSettings.MaxOpacity),
        ["PathPoint.T"] = (0, double.MaxValue),
        ["LoopAction.KeyVk"] = (0, 254),
        ["LoopAction.IntervalValue"] = (0, double.MaxValue),
        ["LoopAction.HoldMs"] = MacroJson.HoldMs,
        ["LoopAction.Repeat"] = MacroJson.Repeat,
        ["LoopAction.RepeatPauseMs"] = MacroJson.RepeatPauseMs,
        ["LoopAction.ClickCount"] = (1, 2),
        ["LoopAction.X"] = MacroJson.Coordinate,
        ["LoopAction.Y"] = MacroJson.Coordinate,
    };

    private static readonly Dictionary<MacroActionType, string> ActionTypes = new()
    {
        [MacroActionType.PressKey] = "Press and release a key or combination (Vk, Ctrl, Alt, Shift, Win), held HoldMs; Repeat presses it more times.",
        [MacroActionType.KeyDown] = "Press a key (Vk) and keep it down.",
        [MacroActionType.KeyUp] = "Release a key (Vk).",
        [MacroActionType.Click] = "Click a mouse button (Vk) at X, Y; ClickCount 2 for a double click.",
        [MacroActionType.MouseDown] = "Press a mouse button (Vk) at X, Y and keep it down.",
        [MacroActionType.MouseUp] = "Release a mouse button (Vk) at X, Y.",
        [MacroActionType.MoveTo] = "Move the pointer to X, Y; Smooth travels there.",
        [MacroActionType.MovePath] = "Follow a recorded mouse path (Path).",
        [MacroActionType.Scroll] = "Turn the scroll wheel ScrollSteps notches in ScrollDirection.",
        [MacroActionType.PasteText] = "Paste Text, with Enter before or after if set.",
        [MacroActionType.If] = "Check the screen (If.Conditions), waiting as If.Wait says, then run If.Then, or If.Else (or stop) when the conditions are not true in time.",
        [MacroActionType.Component] = "Call a Library component (ComponentId) with Arguments: it plays as it is in the Library, changing when it changes.",
    };

    /// <summary>A whole macro: its Id is the only value allowed, and suggested.</summary>
    /// <param name="withDefaults">Each field's default value too (for AI apps, which read items without them: get_item).</param>
    public static string ForMacro(System.Guid id, bool withDefaults = false) =>
        Build(typeof(MacroDefinition), schema => FixId(schema, id), withDefaults);

    /// <summary>A Library component: its Id is the only value allowed, and suggested.</summary>
    public static string ForComponent(System.Guid id, bool withDefaults = false) =>
        Build(typeof(LibraryComponent), schema => FixId(schema, id), withDefaults);

    /// <summary>A loop: its Id is the only value allowed, and suggested. The fields of version 1.0 are left out.</summary>
    public static string ForLoop(System.Guid id, bool withDefaults = false) => Build(typeof(LoopDefinition), schema =>
    {
        FixId(schema, id);
        if (schema["properties"] is JsonObject properties)
        {
            properties.Remove("KeyVk");
            properties.Remove("IntervalMs");
            properties.Remove("HotkeyVk");
        }
    }, withDefaults);

    /// <summary>A remap: its Id is the only value allowed, and suggested. The single key of versions before schema 7 is left out.</summary>
    public static string ForRemap(System.Guid id, bool withDefaults = false) => Build(typeof(RemapDefinition), schema =>
    {
        FixId(schema, id);
        if (schema["properties"] is JsonObject properties)
            properties.Remove("SourceVk");
    }, withDefaults);

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

    private static string Build(System.Type type, System.Action<JsonObject> customize, bool withDefaults = false)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = withDefaults ? (context, node) => WithDefault(context, Transform(context, node)) : Transform,
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

    /// <summary>The field's value in a new object of its type ("default"), except Ids: a new one each time.</summary>
    private static JsonNode WithDefault(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema || context.PropertyInfo is not { Get: { } get } property
            || property.PropertyType == typeof(System.Guid) || property.PropertyType == typeof(System.Guid?)
            || property.DeclaringType.GetConstructor(System.Type.EmptyTypes) == null)
            return node;
        object? value = get(System.Activator.CreateInstance(property.DeclaringType)!);
        schema["default"] = System.Text.Json.JsonSerializer.SerializeToNode(value, property.PropertyType, CodeJson.Options);
        return schema;
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        // Enums are read by CodeJson's own converter, so the exporter only says "true" (anything); a nullable one (a
        // parameter's Unit) can also be left out.
        System.Type? nullableEnum = System.Nullable.GetUnderlyingType(context.TypeInfo.Type) is { IsEnum: true } underlying ? underlying : null;
        if (context.TypeInfo.Type.IsEnum || nullableEnum != null)
        {
            var names = System.Enum.GetNames(nullableEnum ?? context.TypeInfo.Type).Select(n => (JsonNode?)JsonValue.Create(n)).ToList();
            if (nullableEnum != null)
                names.Add(null);
            node = new JsonObject
            {
                ["type"] = nullableEnum != null ? new JsonArray("string", "null") : "string",
                ["enum"] = new JsonArray(names.ToArray()),
            };
        }
        // Any JSON value (a parameter's Example and Default) is "true": an object, so its description fits.
        if (node is JsonValue { } any && any.GetValueKind() == System.Text.Json.JsonValueKind.True && context.PropertyInfo != null)
            node = new JsonObject();
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
                case "MacroDefinition.ChecksPerSecond":
                    schema["enum"] = new JsonArray(MacroDefinition.ChecksPerSecondSteps.Select(c => (JsonNode)JsonValue.Create(c)).ToArray());
                    break;
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

        // WaitSeconds counts only with Wait UpTo (once or as long as it takes leave it 0): its range only then, as MacroJson checks.
        if (context.TypeInfo.Type == typeof(MacroIf) && schema.ContainsKey("properties"))
        {
            schema["if"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["Wait"] = new JsonObject { ["const"] = nameof(ConditionWait.UpTo) } },
                ["required"] = new JsonArray("Wait"),
            };
            schema["then"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["WaitSeconds"] = new JsonObject { ["minimum"] = MacroJson.WaitSeconds.Min, ["maximum"] = MacroJson.WaitSeconds.Max },
                },
            };
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
