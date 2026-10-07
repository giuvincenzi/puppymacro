#if DEBUG
using System.Diagnostics;
using System.IO;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// Development build only: fills an empty dev data folder with example loops, macros and
/// remaps, all disabled. `make dev` deletes the folder first, so every run starts from these.
/// </summary>
internal static class DevSampleData
{
    private const int LeftButton = 0x01;
    private const int XButton1 = 0x05;
    private const int Enter = 0x0D;
    private const int CapsLock = 0x14;
    private const int Escape = 0x1B;
    private const int KeyC = 0x43;
    private const int KeyH = 0x48;
    private const int KeyI = 0x49;
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
            Enabled = false,
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
            Enabled = false,
        });

        settings.Remaps.Add(new RemapDefinition
        {
            SourceVk = CapsLock,
            Target = HotkeyBinding.FromKey(Escape),
            Note = "Sample: Caps Lock to Esc",
            Enabled = false,
        });
        settings.Remaps.Add(new RemapDefinition
        {
            SourceVk = XButton1,
            Target = new HotkeyBinding { Vk = KeyC, Ctrl = true },
            Note = "Sample: mouse back button to Ctrl+C",
            Enabled = false,
        });

        var macros = new MacroLibrary(AppPaths.MacrosFolder);
        MacroDefinition[] samples =
        {
            new()
            {
                Name = "Sample: type and confirm",
                Enabled = false,
                Actions =
                {
                    new MacroAction { Type = MacroActionType.PressKey, Vk = KeyH },
                    new MacroAction { Type = MacroActionType.PressKey, Vk = KeyI, DelayMs = 50 },
                    new MacroAction { Type = MacroActionType.PressKey, Vk = Enter, DelayMs = 100 },
                },
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
