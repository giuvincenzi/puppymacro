using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
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

    /// <summary>Dragged anywhere on it (Start excepted), so it can be moved off what the user needs to see.</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
