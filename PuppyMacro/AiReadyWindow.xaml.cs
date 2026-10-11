using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PuppyMacro.Native;
using PuppyMacro.Services.Ai;

namespace PuppyMacro;

/// <summary>
/// "Ready?" before an AI app's pick or placement: its title, description and numbered steps, on top of everything, in the
/// middle of the screen (the user can drag it), without taking the focus from the app the user prepares. Enter and Esc come
/// from any app through the engine (<c>LoopEngine.WatchReadyKeys</c>, set by <c>MainWindow</c>); Start and Esc here work
/// too. <see cref="Answered"/> once.
/// </summary>
public partial class AiReadyWindow
{
    private bool _answered;

    internal AiReadyWindow(string app, AiAsk ask)
    {
        InitializeComponent();
        Title = $"AI request - {app}";
        AskedByText.Text = $"{app} asks";
        TitleText.Text = ask.Title;
        DescriptionText.Text = ask.Description;
        StepsList.ItemsSource = ask.Steps.Select((text, index) => new { Number = $"{index + 1}.", Text = text }).ToList();
        SourceInitialized += OnSourceInitialized;
        // Closed some other way: cancelled, so the request does not wait.
        Closed += (_, _) => Answer(AiAnswer.Cancelled);
    }

    /// <summary>The user is ready (Done), cancelled, or nobody answered (TimedOut).</summary>
    internal event Action<AiAnswer>? Answered;

    /// <summary>Ends the request once: later calls do nothing.</summary>
    internal void Answer(AiAnswer answer)
    {
        if (_answered)
            return;
        _answered = true;
        Answered?.Invoke(answer);
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => Answer(AiAnswer.Done);

    /// <summary>Never takes the focus from the app the user prepares, even when clicked or dragged (as the recording bar).</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_NOACTIVATE));
    }

    // Dragged anywhere on it (Start excepted), so it can be moved off what the user needs to see. Moved by hand with the
    // mouse captured: Window.DragMove runs Windows' move loop, which can activate the window.
    private Point? _grab;

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _grab = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_grab is not Point grab || e.LeftButton != MouseButtonState.Pressed
            || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;
        Point screen = target.TransformFromDevice.Transform(PointToScreen(e.GetPosition(this)));
        Left = screen.X - grab.X;
        Top = screen.Y - grab.Y;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        _grab = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
