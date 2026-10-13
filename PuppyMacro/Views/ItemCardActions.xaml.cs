using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PuppyMacro.Resources;

namespace PuppyMacro.Views;

/// <summary>
/// The right side of every item in the Loops, Macros and Remap lists, the same in each view: Play / Stop (a loop or
/// macro in Hold mode has none, its place kept empty; a remap has none), More options and the Enabled switch. There is
/// no Edit: a click on the item opens its editor. Its events come from its buttons, whose Tag is the card's item (the
/// window's handlers read it there).
/// </summary>
public partial class ItemCardActions : UserControl
{
    public static readonly DependencyProperty ShowPlayProperty = Register(nameof(ShowPlay), true);
    public static readonly DependencyProperty IsHoldModeProperty = Register(nameof(IsHoldMode), false);
    public static readonly DependencyProperty IsRunningProperty = Register(nameof(IsRunning), false);
    public static readonly DependencyProperty CanPlayProperty = Register(nameof(CanPlay), true);
    public static readonly DependencyProperty CanMoreProperty = Register(nameof(CanMore), true);
    public static readonly DependencyProperty CanSwitchProperty = Register(nameof(CanSwitch), true);
    public static readonly DependencyProperty ShowSwitchProperty = Register(nameof(ShowSwitch), true);

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(ItemCardActions),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ItemCardActions()
    {
        InitializeComponent();
        Update();
    }

    /// <summary>Loops and macros have Play / Stop; remaps do not.</summary>
    public bool ShowPlay { get => (bool)GetValue(ShowPlayProperty); set => SetValue(ShowPlayProperty, value); }

    /// <summary>Hold mode: it runs only while its hotkey is held, so no Play (its place stays empty).</summary>
    public bool IsHoldMode { get => (bool)GetValue(IsHoldModeProperty); set => SetValue(IsHoldModeProperty, value); }

    /// <summary>Running: Stop instead of Play.</summary>
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    /// <summary>Play / Stop can be used (a disabled item cannot be started).</summary>
    public bool CanPlay { get => (bool)GetValue(CanPlayProperty); set => SetValue(CanPlayProperty, value); }

    /// <summary>More options can be used (not while the item runs).</summary>
    public bool CanMore { get => (bool)GetValue(CanMoreProperty); set => SetValue(CanMoreProperty, value); }

    /// <summary>The Enabled switch can be used (not on a remap without a hotkey: it can't be turned on).</summary>
    public bool CanSwitch { get => (bool)GetValue(CanSwitchProperty); set => SetValue(CanSwitchProperty, value); }

    /// <summary>The Enabled switch is here (a grid tile has it at its top right instead).</summary>
    public bool ShowSwitch { get => (bool)GetValue(ShowSwitchProperty); set => SetValue(ShowSwitchProperty, value); }

    /// <summary>The Enabled switch.</summary>
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    public event RoutedEventHandler? PlayClick;
    public event RoutedEventHandler? MoreClick;

    private static DependencyProperty Register(string name, bool value) =>
        DependencyProperty.Register(name, typeof(bool), typeof(ItemCardActions),
            new PropertyMetadata(value, (d, _) => ((ItemCardActions)d).Update()));

    private void Update()
    {
        PlayPart.Visibility = ShowPlay ? Visibility.Visible : Visibility.Collapsed;
        // Hold mode runs only while the hotkey is held: no Play, its place kept so More options lines up on every card.
        PlayButton.Visibility = IsHoldMode ? Visibility.Hidden : Visibility.Visible;
        PlayIcon.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
        StopIcon.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        string play = IsRunning ? Strings.Card_Stop : Strings.Card_Play;
        PlayButton.ToolTip = play;
        AutomationProperties.SetName(PlayButton, play);
        PlayButton.IsEnabled = CanPlay;
        MoreButton.IsEnabled = CanMore;
        EnabledSwitch.IsEnabled = CanSwitch;
        EnabledSwitch.Visibility = ShowSwitch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayClick?.Invoke(sender, e);

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreClick?.Invoke(sender, e);
}
