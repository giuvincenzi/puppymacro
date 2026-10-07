using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>One row of the macro editor.</summary>
public sealed class MacroActionRowViewModel : INotifyPropertyChanged
{
    private int _number;
    private bool _dropBefore;
    private bool _dropAfter;

    public MacroActionRowViewModel(MacroAction action) => Action = action;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MacroAction Action { get; private set; }

    public int Number
    {
        get => _number;
        set { if (_number != value) { _number = value; OnPropertyChanged(); } }
    }

    /// <summary>Delay before the action, in ms (edited inline).</summary>
    public double? Delay
    {
        get => Action.DelayMs;
        set
        {
            double v = System.Math.Clamp(value ?? 0, 0, 86_400_000);
            if (Action.DelayMs == v)
                return;
            Action.DelayMs = v;
            OnPropertyChanged();
        }
    }

    public bool DropBefore
    {
        get => _dropBefore;
        set { if (_dropBefore != value) { _dropBefore = value; OnPropertyChanged(); } }
    }

    public bool DropAfter
    {
        get => _dropAfter;
        set { if (_dropAfter != value) { _dropAfter = value; OnPropertyChanged(); } }
    }

    public bool IsKeyIcon => Action.Type is MacroActionType.PressKey or MacroActionType.KeyDown or MacroActionType.KeyUp;
    public bool IsMouseIcon => Action.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp;
    public bool IsMoveIcon => Action.Type is MacroActionType.MoveTo or MacroActionType.MovePath;
    public bool IsScrollIcon => Action.Type == MacroActionType.Scroll;
    public bool IsTextIcon => Action.Type == MacroActionType.PasteText;

    /// <summary>Recorded paths cannot be edited by hand (only their delay).</summary>
    public bool IsEditable => Action.Type != MacroActionType.MovePath;

    public string Title => Action.Type switch
    {
        MacroActionType.PressKey => $"Press {KeyNames.Get(Action.Vk)}{Times}",
        MacroActionType.KeyDown => $"Key down {KeyNames.Get(Action.Vk)}",
        MacroActionType.KeyUp => $"Key up {KeyNames.Get(Action.Vk)}",
        MacroActionType.Click => $"{ButtonName(Action.Vk)} {(Action.ClickCount == 2 ? "double click" : "click")}{Times}",
        MacroActionType.MouseDown => $"{ButtonName(Action.Vk)} button down",
        MacroActionType.MouseUp => $"{ButtonName(Action.Vk)} button up",
        MacroActionType.MoveTo => Action.Smooth ? "Move smoothly" : "Move to",
        MacroActionType.MovePath => "Move path",
        MacroActionType.Scroll => $"Scroll {Action.ScrollDirection.ToString().ToLowerInvariant()}",
        MacroActionType.PasteText => "Paste text",
        _ => Action.Type.ToString(),
    };

    public string Detail => Action.Type switch
    {
        MacroActionType.PressKey => $"held {System.Math.Round(Action.HoldMs)} ms",
        MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp
            or MacroActionType.MoveTo => $"at {Action.X}, {Action.Y}",
        MacroActionType.MovePath => $"{Action.Path.Count} points, {MacroItemViewModel.FormatDuration(Action.DurationMs)}",
        MacroActionType.Scroll => $"{Action.ScrollSteps} {(Action.ScrollSteps == 1 ? "step" : "steps")}",
        MacroActionType.PasteText => "\"" + Shorten(Action.Text) + "\"",
        _ => "",
    };

    private string Times => Action.Repeat > 1 ? $" {Action.Repeat} times" : "";

    public void Replace(MacroAction action)
    {
        Action = action;
        OnPropertyChanged(string.Empty);
    }

    public static string ButtonName(int vk) => vk switch
    {
        KeyNames.VK_RBUTTON => "Right",
        KeyNames.VK_MBUTTON => "Middle",
        KeyNames.VK_XBUTTON1 => "Back (X1)",
        KeyNames.VK_XBUTTON2 => "Forward (X2)",
        _ => "Left",
    };

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 32 ? text[..32] + "…" : text;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
