using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PuppyMacro.Native;
using PuppyMacro.Resources;
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
        Title = string.Format(CultureInfo.CurrentCulture, Strings.AiRequest_TitleWithApp, app);
        AskedByText.Text = string.Format(CultureInfo.CurrentCulture, Strings.AiReady_AppAsks, app);
        TitleText.Text = ask.Title;
        DescriptionText.Text = ask.Description;
        StepsList.ItemsSource = ask.Steps
            .Select((text, index) => new { Number = string.Format(CultureInfo.CurrentCulture, Strings.AiReady_StepNumber, index + 1), Text = text })
            .ToList();
        BuildHint();
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

    private static readonly Brush HintBrush = MakeHintBrush();

    private static Brush MakeHintBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// "Press [Enter] when you're ready · [Esc] cancels" in the language shown: the text of <c>AiReady_Hint</c> with the Enter
    /// keycap at {0} and the Esc keycap at {1}, in the order the language puts them.
    /// </summary>
    private void BuildHint()
    {
        HintPanel.Children.Clear();
        foreach (string part in Regex.Split(Strings.AiReady_Hint, @"(\{[01]\})"))
        {
            if (part == "{0}")
                HintPanel.Children.Add(EnterCap);
            else if (part == "{1}")
                HintPanel.Children.Add(EscCap);
            else if (part.Trim().Length > 0)
                HintPanel.Children.Add(new TextBlock { FontSize = 13, Foreground = HintBrush, Text = part.Trim(), VerticalAlignment = VerticalAlignment.Center });
        }
    }

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
