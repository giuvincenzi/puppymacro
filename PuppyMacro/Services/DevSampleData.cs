#if DEBUG
using System.Diagnostics;
using System.IO;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// Development build only: fills an empty dev data folder with example loops, macros and
/// remaps, all disabled (in screenshots the first loop is enabled, so the lists do not look all off).
/// `make dev` deletes the folder first, so every run starts from these.
/// </summary>
internal static class DevSampleData
{
    private const int LeftButton = 0x01;
    private const int XButton1 = 0x05;
    private const int Enter = 0x0D;
    private const int CapsLock = 0x14;
    private const int Escape = 0x1B;
    private const int Left = 0x25;
    private const int KeyC = 0x43;
    private const int KeyH = 0x48;
    private const int KeyI = 0x49;
    private const int KeyJ = 0x4A;
    private const int KeyS = 0x53;
    private const int KeyW = 0x57;
    private const int F6 = 0x75;
    private const int F7 = 0x76;

    public static void WriteIfEmpty()
    {
        if (File.Exists(AppPaths.SettingsFile))
            return;

        AppSettings settings = AppSettings.CreateDefault();
        settings.Loops.Clear();
        settings.Loops.Add(new LoopDefinition
        {
            Name = "Sample: click every second",
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = LeftButton, IntervalValue = 1, IntervalUnit = IntervalUnit.Seconds } },
            Hotkey = HotkeyBinding.FromKey(F6),
            Enabled = App.IsScreenshotMode,
        });
        settings.Loops.Add(new LoopDefinition
        {
            Name = "Sample: hold W",
            Actions = { new LoopAction { Type = ActionType.Key, KeyVk = KeyW, HoldDown = true } },
            Mode = ActivationMode.Hold,
            Hotkey = HotkeyBinding.FromKey(F7),
            Enabled = false,
        });
        settings.Loops.Add(new LoopDefinition
        {
            Name = "Sample: chat message",
            Actions = { new LoopAction { Type = ActionType.Text, Text = "Hello", EnterAfter = true, IntervalValue = 30, IntervalUnit = IntervalUnit.Seconds } },
            AppExe = "notepad.exe",
            Enabled = false,
        });

        settings.Remaps.Add(new RemapDefinition
        {
            Source = HotkeyBinding.FromKey(CapsLock),
            Target = HotkeyBinding.FromKey(Escape),
            Note = "Sample: Caps Lock to Esc",
            Enabled = false,
        });
        settings.Remaps.Add(new RemapDefinition
        {
            Source = new HotkeyBinding { Vk = KeyJ, Alt = true, AltSide = ModifierSide.Left },
            Target = HotkeyBinding.FromKey(Left),
            Note = "Sample: Left Alt + J to the left arrow",
            Enabled = false,
        });
        settings.Remaps.Add(new RemapDefinition
        {
            Source = HotkeyBinding.FromKey(XButton1),
            Target = new HotkeyBinding { Vk = KeyC, Ctrl = true },
            Note = "Sample: mouse back button to Ctrl+C",
            Enabled = false,
        });

        var macros = new MacroLibrary(AppPaths.MacrosFolder);
        // A Library collection with a component, and a macro that links it (the Library's samples).
        var saveFile = new LibraryComponent
        {
            Name = "Save the file",
            Purpose = ComponentPurpose.Act,
            Concept = "save_file",
            Description = "Presses Ctrl+S.",
            Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = KeyS, Ctrl = true } },
        };
        macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Sample: Notepad",
            Domain = LibraryDomain.Office,
            AppExe = "notepad.exe",
            Components = { saveFile },
        });
        // A component with a parameter (a function): it pastes the word each call gives it.
        var typeWord = new LibraryComponent
        {
            Name = "Type a word",
            Purpose = ComponentPurpose.Act,
            Concept = "type_word",
            Description = "Pastes Word.",
            Parameters =
            {
                new ComponentParameter { Name = "Word", Type = ParameterType.Text, Example = System.Text.Json.Nodes.JsonValue.Create("hello"), Description = "The word to paste" },
            },
            Actions =
            {
                new MacroAction { Type = MacroActionType.PasteText, Text = "hello", Bind = new() { ["Text"] = "Word" } },
            },
        };
        macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Sample: typing",
            Domain = LibraryDomain.Other,
            Components = { typeWord },
        });
        // A component with a Grid parameter: it clicks the cell each call gives (Slot), of 3 × 2 cells.
        var clickCell = new LibraryComponent
        {
            Name = "Click a cell",
            Purpose = ComponentPurpose.Act,
            Concept = "click_cell",
            Description = "Clicks cell Slot of Cells.",
            Parameters =
            {
                new ComponentParameter
                {
                    Name = "Cells", Type = ParameterType.Grid, Description = "The cells, 3 × 2",
                    Example = System.Text.Json.Nodes.JsonNode.Parse("""{"First":{"X":100,"Y":100},"Last":{"X":300,"Y":200},"Columns":3,"Rows":2}"""),
                    Default = System.Text.Json.Nodes.JsonNode.Parse("""{"First":{"X":100,"Y":100},"Last":{"X":300,"Y":200},"Columns":3,"Rows":2}"""),
                },
                new ComponentParameter
                {
                    Name = "Slot", Type = ParameterType.Number, Unit = NumberUnit.Count, Description = "Which cell, 1 to 6",
                    Example = System.Text.Json.Nodes.JsonValue.Create(1),
                },
            },
            Actions =
            {
                new MacroAction
                {
                    Type = MacroActionType.Click, Vk = LeftButton, X = 100, Y = 100, Cell = 1,
                    Bind = new() { ["Point"] = "Cells", ["Cell"] = "Slot" },
                },
            },
        };
        macros.Library.Collections.Add(new LibraryCollection
        {
            Name = "Sample: grid",
            Domain = LibraryDomain.Other,
            Components = { clickCell },
        });
        if (!macros.Library.TrySave(out string? libraryError))
            Debug.WriteLine($"Sample Library not saved: {libraryError}");
        var typeHi = new MacroGroup { Name = "Type hi" };
        MacroDefinition[] samples =
        {
            new()
            {
                Name = "Sample: type and confirm",
                Enabled = false,
                Actions =
                {
                    new MacroAction { Type = MacroActionType.PressKey, Vk = KeyH, GroupId = typeHi.Id },
                    new MacroAction { Type = MacroActionType.PressKey, Vk = KeyI, DelayMs = 50, GroupId = typeHi.Id },
                    new MacroAction { Type = MacroActionType.PressKey, Vk = Enter, DelayMs = 100, Name = "Send" },
                },
                Groups = { typeHi },
            },
            new()
            {
                Name = "Sample: click and scroll",
                Enabled = false,
                Actions =
                {
                    new MacroAction { Type = MacroActionType.Click, Vk = LeftButton },
                    new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = ScrollDirection.Down, ScrollSteps = 3, DelayMs = 300 },
                },
            },
            new()
            {
                // A row linked to a Library component.
                Name = "Sample: save with a component",
                Enabled = false,
                AppExe = "notepad.exe",
                Actions = { new MacroAction { Type = MacroActionType.Component, ComponentId = saveFile.Id } },
            },
            new()
            {
                // A call that gives a component's parameter its value.
                Name = "Sample: type with a parameter",
                Enabled = false,
                Actions =
                {
                    new MacroAction
                    {
                        Type = MacroActionType.Component,
                        ComponentId = typeWord.Id,
                        Arguments = new() { ["Word"] = System.Text.Json.Nodes.JsonValue.Create("hello") },
                    },
                },
            },
            new()
            {
                // A call that gives only a cell's number: the grid is the component's Default.
                Name = "Sample: click a cell",
                Enabled = false,
                Actions =
                {
                    new MacroAction
                    {
                        Type = MacroActionType.Component,
                        ComponentId = clickCell.Id,
                        Arguments = new() { ["Slot"] = System.Text.Json.Nodes.JsonValue.Create(5) },
                    },
                },
            },
            new()
            {
                // An If on the screen (the guide's condition-editor.png): a green pixel, then Enter, otherwise Esc.
                Name = "Sample: accept when ready",
                Enabled = false,
                Actions =
                {
                    new MacroAction
                    {
                        Type = MacroActionType.If,
                        Name = "Match ready",
                        If = new MacroIf
                        {
                            Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, X = 960, Y = 1010, Color = "#3BD16F", Tolerance = 5 } },
                            Wait = ConditionWait.UpTo,
                            WaitSeconds = 30,
                            Then = { new MacroAction { Type = MacroActionType.PressKey, Vk = Enter, DelayMs = 100 } },
                            Else = { new MacroAction { Type = MacroActionType.PressKey, Vk = Escape } },
                        },
                    },
                },
            },
        };
        foreach (MacroDefinition macro in samples)
        {
            if (macros.TrySave(macro, out string? error))
                settings.MacroOrder.Add(macro.Id);
            else
                Debug.WriteLine($"Sample macro not saved: {error}");
        }

        if (!new SettingsStore(AppPaths.SettingsFile).TrySave(settings, out string? settingsError))
            Debug.WriteLine($"Sample settings not saved: {settingsError}");
    }
}
#endif
