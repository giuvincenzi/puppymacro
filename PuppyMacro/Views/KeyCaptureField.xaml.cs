using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SplitButton = iNKORE.UI.WPF.Modern.Controls.SplitButton;
using SplitButtonClickEventArgs = iNKORE.UI.WPF.Modern.Controls.SplitButtonClickEventArgs;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>
/// The one field that asks the user for a key or a hotkey, everywhere, on one line: "Set hotkey" when there
/// is none; while it waits for the key through <see cref="LoopEngine.BeginCapture"/>, a ring and the prompt
/// (Esc cancels); then the keys in the accent color in a SplitButton whose menu has Change, Clear (with
/// <see cref="CanClear"/>) and the side of each Ctrl, Alt, Shift and Win the hotkey holds. It keeps a key when
/// <see cref="Validate"/> accepts it. The window gives the engine once, with
/// <see cref="SetEngine"/> on itself (it is inherited by every field inside, also in templates).
/// </summary>
public partial class KeyCaptureField : UserControl
{
    public static readonly DependencyProperty EngineProperty = DependencyProperty.RegisterAttached(
        "Engine", typeof(LoopEngine), typeof(KeyCaptureField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    internal static void SetEngine(DependencyObject element, LoopEngine? engine) => element.SetValue(EngineProperty, engine);

    internal static LoopEngine? GetEngine(DependencyObject element) => (LoopEngine?)element.GetValue(EngineProperty);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(HotkeyBinding), typeof(KeyCaptureField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((KeyCaptureField)d).ShowValue()));

    public static readonly DependencyProperty PromptProperty = DependencyProperty.Register(
        nameof(Prompt), typeof(string), typeof(KeyCaptureField), new PropertyMetadata("Press the hotkey (Esc cancels)"));

    public static readonly DependencyProperty ItemNameProperty = DependencyProperty.Register(
        nameof(ItemName), typeof(string), typeof(KeyCaptureField), new PropertyMetadata("hotkey", (d, _) => ((KeyCaptureField)d).UpdateNames()));

    public static readonly DependencyProperty CanClearProperty = DependencyProperty.Register(
        nameof(CanClear), typeof(bool), typeof(KeyCaptureField), new PropertyMetadata(true, (d, _) => ((KeyCaptureField)d).ShowValue()));

    public static readonly DependencyProperty KeyOnlyProperty = DependencyProperty.Register(
        nameof(KeyOnly), typeof(bool), typeof(KeyCaptureField), new PropertyMetadata(false, (d, _) => ((KeyCaptureField)d).ShowValue()));

    public static readonly DependencyProperty AllowPrimaryMouseProperty = DependencyProperty.Register(
        nameof(AllowPrimaryMouse), typeof(bool), typeof(KeyCaptureField), new PropertyMetadata(false));

    public static readonly DependencyProperty IsHotkeyProperty = DependencyProperty.Register(
        nameof(IsHotkey), typeof(bool), typeof(KeyCaptureField), new PropertyMetadata(false));

    public static readonly DependencyProperty AllowModifierAloneProperty = DependencyProperty.Register(
        nameof(AllowModifierAlone), typeof(bool), typeof(KeyCaptureField), new PropertyMetadata(false));

    public static readonly DependencyProperty StartOnLoadProperty = DependencyProperty.Register(
        nameof(StartOnLoad), typeof(bool), typeof(KeyCaptureField),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private bool _waiting;
    private readonly List<Control> _sideItems = new();

    public KeyCaptureField()
    {
        InitializeComponent();
        UpdateNames();
        ShowValue();
        Loaded += OnLoaded;
        // A field that goes away (a removed row, a closed window) stops waiting.
        Unloaded += (_, _) => Cancel();
    }

    /// <summary>The key or hotkey; null or not set: none.</summary>
    public HotkeyBinding? Value
    {
        get => (HotkeyBinding?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>What the field says while it waits, e.g. "Press the hotkey (Esc cancels)".</summary>
    public string Prompt
    {
        get => (string)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    /// <summary>What the field asks for, in its buttons: "Set hotkey", "Change key", "Clear hotkey".</summary>
    public string ItemName
    {
        get => (string)GetValue(ItemNameProperty);
        set => SetValue(ItemNameProperty, value);
    }

    /// <summary>Whether the value can be removed (Clear).</summary>
    public bool CanClear
    {
        get => (bool)GetValue(CanClearProperty);
        set => SetValue(CanClearProperty, value);
    }

    /// <summary>A single key or button: the modifiers held while choosing it are not kept.</summary>
    public bool KeyOnly
    {
        get => (bool)GetValue(KeyOnlyProperty);
        set => SetValue(KeyOnlyProperty, value);
    }

    /// <summary>Passed to <see cref="LoopEngine.BeginCapture"/>: left and right click can be chosen alone.</summary>
    public bool AllowPrimaryMouse
    {
        get => (bool)GetValue(AllowPrimaryMouseProperty);
        set => SetValue(AllowPrimaryMouseProperty, value);
    }

    /// <summary>Passed to <see cref="LoopEngine.BeginCapture"/>: the value is a hotkey.</summary>
    public bool IsHotkey
    {
        get => (bool)GetValue(IsHotkeyProperty);
        set => SetValue(IsHotkeyProperty, value);
    }

    /// <summary>Passed to <see cref="LoopEngine.BeginCapture"/>: a Ctrl, Alt, Shift or Win key pressed and released alone is kept, by its side.</summary>
    public bool AllowModifierAlone
    {
        get => (bool)GetValue(AllowModifierAloneProperty);
        set => SetValue(AllowModifierAloneProperty, value);
    }

    /// <summary>Starts waiting as soon as the field is shown (a key row just added); set back to false then.</summary>
    public bool StartOnLoad
    {
        get => (bool)GetValue(StartOnLoadProperty);
        set => SetValue(StartOnLoadProperty, value);
    }

    /// <summary>Why a pressed key cannot be used (shown by the window through <see cref="Rejected"/>), or null to keep it.</summary>
    public Func<HotkeyBinding, string?>? Validate { get; set; }

    /// <summary>The user set or cleared the value.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>A pressed key was not kept: the reason from <see cref="Validate"/>.</summary>
    public event Action<string>? Rejected;

    /// <summary>Set was pressed: the window can hide an old error.</summary>
    public event EventHandler? Started;

    public bool IsWaiting => _waiting;

    /// <summary>Waits for the key, as Set does.</summary>
    public void Start()
    {
        if (GetEngine(this) is not { } engine)
            return;
        engine.CancelCapture(); // another field that waits goes back to its value
        Started?.Invoke(this, EventArgs.Empty);
        ShowWaiting(true);
        engine.BeginCapture(OnCaptured, () => ShowWaiting(false), AllowPrimaryMouse, IsHotkey, AllowModifierAlone);
    }

    /// <summary>Stops waiting, as Esc does.</summary>
    public void Cancel()
    {
        if (_waiting)
            GetEngine(this)?.CancelCapture();
        ShowWaiting(false);
    }

    /// <summary>The keys the field shows for <paramref name="value"/> (none: the field shows Set instead).</summary>
    internal static List<string> DisplayParts(HotkeyBinding? value, bool keyOnly)
    {
        if (value is not { IsSet: true })
            return new List<string>();
        return keyOnly ? new List<string> { KeyNames.Get(value.Vk) } : KeyNames.Parts(value);
    }

    /// <summary>One choice of the menu's side items: <see cref="Modifier"/> ("Ctrl") on <see cref="Side"/>.</summary>
    internal sealed record SideChoice(string Modifier, ModifierSide Side, string Label, bool IsChecked);

    /// <summary>
    /// The side items of the menu: for each Ctrl, Alt, Shift and Win of <paramref name="value"/>, "Left Ctrl",
    /// "Right Ctrl" and "Left or right Ctrl", the current one checked. None for a single key.
    /// </summary>
    internal static List<List<SideChoice>> SideChoices(HotkeyBinding? value, bool keyOnly)
    {
        var groups = new List<List<SideChoice>>();
        if (keyOnly || value is not { IsSet: true })
            return groups;
        foreach (var (name, held, current) in Modifiers(value))
        {
            if (!held)
                continue;
            groups.Add(new List<SideChoice>
            {
                new(name, ModifierSide.Left, KeyNames.Modifier(name, ModifierSide.Left), current == ModifierSide.Left),
                new(name, ModifierSide.Right, KeyNames.Modifier(name, ModifierSide.Right), current == ModifierSide.Right),
                new(name, ModifierSide.Any, $"Left or right {name}", current == ModifierSide.Any),
            });
        }
        return groups;
    }

    /// <summary>A copy of <paramref name="value"/> with <paramref name="modifier"/> ("Ctrl", "Alt", "Shift", "Win") on <paramref name="side"/>.</summary>
    internal static HotkeyBinding WithSide(HotkeyBinding value, string modifier, ModifierSide side)
    {
        var copy = value.Clone();
        switch (modifier)
        {
            case "Ctrl": copy.CtrlSide = side; break;
            case "Alt": copy.AltSide = side; break;
            case "Shift": copy.ShiftSide = side; break;
            case "Win": copy.WinSide = side; break;
        }
        return copy;
    }

    private static (string Name, bool Held, ModifierSide Side)[] Modifiers(HotkeyBinding value) => new[]
    {
        ("Ctrl", value.Ctrl, value.CtrlSide),
        ("Alt", value.Alt, value.AltSide),
        ("Shift", value.Shift, value.ShiftSide),
        ("Win", value.Win, value.WinSide),
    };

    private void OnCaptured(HotkeyBinding binding)
    {
        ShowWaiting(false);
        var value = KeyOnly ? HotkeyBinding.FromKey(binding.Vk) : binding;
        Keep(value);
    }

    /// <summary>Keeps <paramref name="value"/> when <see cref="Validate"/> accepts it; otherwise <see cref="Rejected"/>.</summary>
    private void Keep(HotkeyBinding value)
    {
        if (Validate?.Invoke(value) is { } problem)
        {
            Rejected?.Invoke(problem);
            ShowValue(); // a side item clicked shows the side kept
            return;
        }
        Value = value;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSideClick(SideChoice choice)
    {
        if (Value is not { IsSet: true } value)
            return;
        var changed = WithSide(value, choice.Modifier, choice.Side);
        if (changed.SameAs(value))
            ShowValue(); // clicking the checked item unchecks it: check it again
        else
            Keep(changed);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!StartOnLoad)
            return;
        StartOnLoad = false;
        Start();
    }

    private void OnSetClick(object sender, RoutedEventArgs e) => Start();

    /// <summary>The SplitButton's main part (the keys): change, as Set does.</summary>
    private void OnValueSplitClick(SplitButton sender, SplitButtonClickEventArgs args) => Start();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Cancel();
        Value = null;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowWaiting(bool waiting)
    {
        _waiting = waiting;
        WaitRing.IsActive = waiting;
        PromptText.Text = Prompt;
        ShowState();
    }

    private void ShowValue()
    {
        SplitCaps.ItemsSource = DisplayParts(Value, KeyOnly);
        ClearItem.Visibility = CanClear ? Visibility.Visible : Visibility.Collapsed;
        ShowSideItems();
        ShowState();
    }

    /// <summary>The menu's side items after Change and Clear, a group per modifier, each after a separator.</summary>
    private void ShowSideItems()
    {
        foreach (var item in _sideItems)
            ValueMenu.Items.Remove(item);
        _sideItems.Clear();
        foreach (var group in SideChoices(Value, KeyOnly))
        {
            _sideItems.Add(new Separator());
            foreach (var choice in group)
            {
                var item = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.IsChecked };
                item.Click += (_, _) => OnSideClick(choice);
                _sideItems.Add(item);
            }
        }
        foreach (var item in _sideItems)
            ValueMenu.Items.Add(item);
    }

    /// <summary>One of the three states: nothing set (Set), waiting (ring and prompt), set (keys, Change, Clear).</summary>
    private void ShowState()
    {
        bool set = Value is { IsSet: true };
        SetButton.Visibility = !_waiting && !set ? Visibility.Visible : Visibility.Collapsed;
        WaitPanel.Visibility = _waiting ? Visibility.Visible : Visibility.Collapsed;
        ValuePanel.Visibility = !_waiting && set ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNames()
    {
        SetButton.Content = "Set " + ItemName;
        AutomationProperties.SetName(SetButton, "Set " + ItemName);
        string change = "Change " + ItemName;
        AutomationProperties.SetName(ValueSplit, change);
        ValueSplit.ToolTip = change;
        ChangeItem.Header = "Change";
        ClearItem.Header = "Clear";
        AutomationProperties.SetName(ChangeItem, change);
        AutomationProperties.SetName(ClearItem, "Clear " + ItemName);
    }
}
