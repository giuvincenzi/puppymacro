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
    private bool _isCapturing;
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

    /// <summary>Unique radio group name for this row's Repeat / Hold down choice.</summary>
    public string RowGroup { get; } = "Row" + System.Guid.NewGuid().ToString("N");

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
            OnPropertyChanged(nameof(IsRepeat));
            OnPropertyChanged(nameof(IsHoldKey));
            OnPropertyChanged(nameof(ShowInterval));
        }
    }

    public bool IsRepeat
    {
        get => !_holdDown;
        set => HoldDown = !value;
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
        set { _keyVk = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyLabel)); }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set { _isCapturing = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyLabel)); }
    }

    public string KeyLabel => IsCapturing
        ? "Press a key or mouse button (Esc cancels)"
        : (KeyVk == 0 ? "Choose key" : KeyNames.Get(KeyVk));

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; OnPropertyChanged(); }
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
        set { _intervalValue = value; OnPropertyChanged(); }
    }

    /// <summary>0 = ms, 1 = s, 2 = min, 3 = h.</summary>
    public int UnitIndex
    {
        get => _unitIndex;
        set { _unitIndex = value; OnPropertyChanged(); }
    }

    /// <summary>Changes the interval by one step (10 for ms, 1 for s/min/h).</summary>
    public void Step(int direction)
    {
        double step = Unit == IntervalUnit.Milliseconds ? 10 : 1;
        double next = (IntervalValue ?? 0) + direction * step;
        IntervalValue = System.Math.Max(0, System.Math.Round(next, 2));
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
