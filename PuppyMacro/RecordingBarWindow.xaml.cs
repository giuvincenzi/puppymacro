using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PuppyMacro.Native;

namespace PuppyMacro;

/// <summary>Small always-on-top bar shown while recording: time, event count, hotkey and Stop.</summary>
public partial class RecordingBarWindow
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _countdown;
    private CountdownWindow? _bigCountdown;

    public RecordingBarWindow(string hotkey)
    {
        InitializeComponent();
        HotkeyText.Text = hotkey;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => TimeText.Text = _clock.Elapsed.ToString(@"mm\:ss");
        _timer.Start();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => CenterOnTop();
        SizeChanged += (_, _) => CenterOnTop();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _countdown?.Stop();
            CloseBigCountdown();
        };
    }

    /// <summary>Raised when the Stop button is clicked.</summary>
    public event Action? StopRequested;

    /// <summary>Raised when the countdown is cancelled.</summary>
    public event Action? CountdownCancelled;

    /// <summary>Shows "Recording starts in N" here and big in the middle of the screen, then calls <paramref name="done"/>.</summary>
    public void StartCountdown(int seconds, Action done)
    {
        CountdownPanel.Visibility = Visibility.Visible;
        RecordingPanel.Visibility = Visibility.Collapsed;
        int left = seconds;
        CountdownText.Text = $"Recording starts in {left}";
        _bigCountdown = new CountdownWindow();
        _bigCountdown.Show();
        _bigCountdown.SetNumber(left);
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            left--;
            if (left > 0)
            {
                CountdownText.Text = $"Recording starts in {left}";
                _bigCountdown?.SetNumber(left);
                return;
            }
            _countdown.Stop();
            ShowRecording();
            done();
        };
        _countdown.Start();
    }

    /// <summary>Switches to the recording view (time, events, Stop) and restarts the clock.</summary>
    public void ShowRecording()
    {
        _countdown?.Stop();
        CloseBigCountdown();
        CountdownPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Visible;
        _clock.Restart();
        Dispatcher.InvokeAsync(CenterOnTop);
    }

    private void OnCancelCountdownClick(object sender, RoutedEventArgs e)
    {
        _countdown?.Stop();
        CloseBigCountdown();
        CountdownCancelled?.Invoke();
    }

    private void CloseBigCountdown()
    {
        _bigCountdown?.Close();
        _bigCountdown = null;
    }

    private void CenterOnTop()
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 18;
    }

    public void SetCount(int events) => CountText.Text = $"{events} {(events == 1 ? "event" : "events")}";

    private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Never take focus from the app being recorded, and stay out of Alt+Tab.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }
}
