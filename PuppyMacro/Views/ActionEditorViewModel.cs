using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Editable row in the loop editor: one key or one text.</summary>
public sealed class ActionEditorViewModel : INotifyPropertyChanged
{
    private int _keyVk;
    private string _text = "";
    private bool _enterBefore;
    private bool _enterAfter;
    private double? _intervalValue;
    private int _unitIndex;
    private bool _captureOnLoad;
    private bool _isExpanded;
    private bool _holdDown;

    public ActionEditorViewModel(LoopAction action)
    {
        Type = action.Type;
        _keyVk = action.KeyVk;
        _text = action.Text;
        _enterBefore = action.EnterBefore;
        _enterAfter = action.EnterAfter;
        _intervalValue = action.IntervalValue;
        _unitIndex = (int)action.IntervalUnit;
        _holdDown = action.HoldDown;
    }

    /// <summary>Key rows: keep the key held down while the loop runs.</summary>
    public bool HoldDown
    {
        get => _holdDown;
        set
        {
            if (_holdDown == value)
                return;
            _holdDown = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeIndex));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsHoldKey));
            OnPropertyChanged(nameof(ShowInterval));
        }
    }

    /// <summary>The Repeat / Hold down list: 0 Repeat, 1 Hold down.</summary>
    public int ModeIndex
    {
        get => _holdDown ? 1 : 0;
        set => HoldDown = value == 1;
    }

    public bool IsHoldKey => IsKey && _holdDown;

    /// <summary>The interval is not used by held keys.</summary>
    public bool ShowInterval => !IsHoldKey;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActionType Type { get; }
    public bool IsKey => Type == ActionType.Key;
    public bool IsText => Type == ActionType.Text;

    public int KeyVk
    {
        get => _keyVk;
        set
        {
            _keyVk = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Key));
        }
    }

    /// <summary>The key as the row's key field shows and sets it (a single key, no modifiers).</summary>
    public HotkeyBinding? Key
    {
        get => _keyVk == 0 ? null : HotkeyBinding.FromKey(_keyVk);
        set => KeyVk = value?.Vk ?? 0;
    }

    /// <summary>A key row just added: it opens and its key field waits for the key at once.</summary>
    public bool CaptureOnLoad
    {
        get => _captureOnLoad;
        set { _captureOnLoad = value; OnPropertyChanged(); }
    }

    /// <summary>The row is open (a new key row opens to show its waiting key field).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }


    /// <summary>Under the title while the row is closed: how the key is pressed, or the text, and how often.</summary>
    public string Summary
    {
        get
        {
            string every = $"every {IntervalValue ?? 0:0.##} {UnitNames[System.Math.Clamp(UnitIndex, 0, 3)]}";
            if (IsText)
                return string.IsNullOrEmpty(Text) ? $"No text yet, {every}" : $"\"{FirstLine(Text)}\", {every}";
            return _holdDown ? "Held down while the loop runs" : $"Repeat, {every}";
        }
    }

    private static readonly string[] UnitNames = { "ms", "s", "min", "h" };

    private static string FirstLine(string text)
    {
        string line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    public bool EnterBefore
    {
        get => _enterBefore;
        set { _enterBefore = value; OnPropertyChanged(); }
    }

    public bool EnterAfter
    {
        get => _enterAfter;
        set { _enterAfter = value; OnPropertyChanged(); }
    }

    public double? IntervalValue
    {
        get => _intervalValue;
        set { _intervalValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>0 = ms, 1 = s, 2 = min, 3 = h.</summary>
    public int UnitIndex
    {
        get => _unitIndex;
        set { _unitIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); }
    }


    public IntervalUnit Unit => (IntervalUnit)System.Math.Clamp(UnitIndex, 0, 3);

    public double IntervalMs => LoopAction.ToMilliseconds(IntervalValue ?? 0, Unit);

    public LoopAction ToAction() => new()
    {
        Type = Type,
        KeyVk = IsKey ? KeyVk : 0,
        Text = IsText ? Text : "",
        EnterBefore = IsText && EnterBefore,
        EnterAfter = IsText && EnterAfter,
        HoldDown = IsKey && HoldDown,
        IntervalValue = IntervalValue ?? 0,
        IntervalUnit = Unit,
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
