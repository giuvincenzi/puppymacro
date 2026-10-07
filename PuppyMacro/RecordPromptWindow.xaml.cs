using System.Windows;

namespace PuppyMacro;

/// <summary>"Press F8 to start recording". Closed with DialogResult true when recording starts.</summary>
public partial class RecordPromptWindow
{
    public RecordPromptWindow(string hotkey, bool recordMouseMovement)
    {
        InitializeComponent();
        HotkeyText.Text = hotkey;
        _recordMouseMovement = recordMouseMovement;
        MouseMovementBox.IsChecked = recordMouseMovement;
        DescriptionText.Text = $"Start recording gives you 3 seconds to switch to the window you want to record. " +
                               $"Everything you press, click and move is recorded until you press {hotkey} or select Stop.";
    }

    /// <summary>True when closed with the Start recording button (countdown), false with the hotkey.</summary>
    public bool StartNow { get; private set; }

    private void OnStartNowClick(object sender, RoutedEventArgs e)
    {
        StartNow = true;
        DialogResult = true;
    }

    private volatile bool _recordMouseMovement;

    /// <summary>Current checkbox value. Safe to read from any thread (the input thread reads it).</summary>
    public bool RecordMouseMovement => _recordMouseMovement;

    private void OnMouseMovementChanged(object sender, RoutedEventArgs e) =>
        _recordMouseMovement = MouseMovementBox.IsChecked == true;

    /// <summary>Called when the record hotkey was pressed.</summary>
    public void Started()
    {
        if (IsVisible)
            DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
