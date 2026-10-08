using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// The right side of every card in the Loops, Macros and Remap lists, the same for all: Play / Stop (a loop or
/// macro in Hold mode has none, its place kept empty; a remap has none), Edit, the Enabled switch and More options.
/// Its events come from its buttons, whose Tag is the card's item (the window's handlers read it there).
/// </summary>
public partial class ItemCardActions : UserControl
{
    public static readonly DependencyProperty ShowPlayProperty = Register(nameof(ShowPlay), true);
    public static readonly DependencyProperty IsHoldModeProperty = Register(nameof(IsHoldMode), false);
    public static readonly DependencyProperty IsRunningProperty = Register(nameof(IsRunning), false);
    public static readonly DependencyProperty CanPlayProperty = Register(nameof(CanPlay), true);
    public static readonly DependencyProperty CanEditProperty = Register(nameof(CanEdit), true);
    public static readonly DependencyProperty CanMoreProperty = Register(nameof(CanMore), true);

    public static readonly DependencyProperty EditBlockedTextProperty = DependencyProperty.Register(
        nameof(EditBlockedText), typeof(string), typeof(ItemCardActions),
        new PropertyMetadata("Stop running loops and macros to edit", (d, _) => ((ItemCardActions)d).Update()));

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

    /// <summary>Edit can be used (not while loops or macros run).</summary>
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }

    /// <summary>More options can be used (not while the item runs).</summary>
    public bool CanMore { get => (bool)GetValue(CanMoreProperty); set => SetValue(CanMoreProperty, value); }

    /// <summary>Edit's tooltip while it cannot be used.</summary>
    public string EditBlockedText { get => (string)GetValue(EditBlockedTextProperty); set => SetValue(EditBlockedTextProperty, value); }

    /// <summary>The Enabled switch.</summary>
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    public event RoutedEventHandler? PlayClick;
    public event RoutedEventHandler? EditClick;
    public event RoutedEventHandler? MoreClick;

    private static DependencyProperty Register(string name, bool value) =>
        DependencyProperty.Register(name, typeof(bool), typeof(ItemCardActions),
            new PropertyMetadata(value, (d, _) => ((ItemCardActions)d).Update()));

    private void Update()
    {
        PlayPart.Visibility = ShowPlay ? Visibility.Visible : Visibility.Collapsed;
        // Hold mode runs only while the hotkey is held: no Play, its place kept so Edit lines up on every card.
        PlayButton.Visibility = IsHoldMode ? Visibility.Hidden : Visibility.Visible;
        PlayIcon.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
        StopIcon.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        string play = IsRunning ? "Stop" : "Play";
        PlayButton.ToolTip = play;
        AutomationProperties.SetName(PlayButton, play);
        PlayButton.IsEnabled = CanPlay;
        EditButton.IsEnabled = CanEdit;
        EditButton.ToolTip = CanEdit ? "Edit" : EditBlockedText;
        MoreButton.IsEnabled = CanMore;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayClick?.Invoke(sender, e);

    private void OnEditClick(object sender, RoutedEventArgs e) => EditClick?.Invoke(sender, e);

    private void OnMoreClick(object sender, RoutedEventArgs e) => MoreClick?.Invoke(sender, e);
}
