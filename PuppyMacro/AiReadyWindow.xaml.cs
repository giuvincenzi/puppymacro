using System;
using System.Windows;
using System.Windows.Input;
using PuppyMacro.Services.Ai;

namespace PuppyMacro;

/// <summary>
/// "Ready?" before an AI app's pick or key request: its message and context, on top of everything, at the top right of the
/// screen, without taking the focus from the app the user prepares. Enter and Esc come from any app through the engine
/// (<c>LoopEngine.WatchReadyKeys</c>, set by <c>MainWindow</c>); Start and Esc here work too. <see cref="Answered"/> once.
/// </summary>
public partial class AiReadyWindow
{
    private bool _answered;

    internal AiReadyWindow(string app, string message, string context)
    {
        InitializeComponent();
        Title = $"AI request - {app}";
        AskedByText.Text = $"{app} asks";
        MessageText.Text = message;
        ContextText.Text = context;
        Loaded += (_, _) =>
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 32;
            Top = work.Top + 32;
        };
        // Closed from the taskbar or with Alt+F4: cancelled (the request does not wait, Enter is not taken any more).
        Closed += (_, _) => Answer(AiAnswer.Cancelled);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Answer(AiAnswer.Cancelled);
            else if (e.Key == Key.Enter)
                Answer(AiAnswer.Done);
        };
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
}
