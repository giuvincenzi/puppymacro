using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// What an AI app asks the user (AI page, <c>AiTools</c>): to confirm a change (<see cref="Confirmation"/>) or to press a key
/// or combination (<see cref="KeyRequest"/>). Shown by <c>MainWindow.ShowForAi</c> (in front of the other apps, closed
/// after <see cref="AiTools.AnswerTimeout"/> without use). The answer is in <see cref="Answer"/> (and <see cref="Keys"/>).
/// </summary>
public partial class AiRequestWindow
{
    private AiRequestWindow(string app, string heading, string ok)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = $"AI request - {app}"; // its own name in the taskbar, not the main window's
        AskedByText.Text = $"Asked by {app}";
        HeadingText.Text = heading;
        OkButton.Content = ok;
    }

    public AiAnswer Answer { get; private set; } = AiAnswer.Cancelled;

    /// <summary>Nobody used PuppyMacro for <see cref="AiTools.AnswerTimeout"/>: closes without an answer.</summary>
    internal void TimeOut()
    {
        Answer = AiAnswer.TimedOut;
        DialogResult = false;
    }

    /// <summary>With <see cref="KeyRequest"/>: the keys pressed.</summary>
    internal HotkeyBinding? Keys { get; private set; }

    /// <summary>"Delete this remap?" with what changes, one line each, and the button that does it ("Delete").</summary>
    internal static AiRequestWindow Confirmation(string app, string heading, IReadOnlyList<string> lines, string ok)
    {
        var window = new AiRequestWindow(app, heading, ok);
        window.Lines.ItemsSource = lines;
        window.Lines.Visibility = Visibility.Visible;
        return window;
    }

    /// <summary>Asks for a key or combination, with the AI app's title, description and steps (numbered).</summary>
    internal static AiRequestWindow KeyRequest(string app, AiAsk ask, LoopEngine engine)
    {
        var window = new AiRequestWindow(app, ask.Title, "OK");
        window.ContextText.Text = ask.Description;
        window.ContextText.Visibility = Visibility.Visible;
        window.InstructionsTitle.Visibility = Visibility.Visible;
        window.Lines.ItemsSource = ask.Steps.Select((step, index) => $"{index + 1}. {step}").ToList();
        window.Lines.Visibility = Visibility.Visible;
        KeyCaptureField.SetEngine(window, engine); // the key field waits for keys through it
        window.KeyCard.Visibility = Visibility.Visible;
        window.KeyField.StartOnLoad = true;
        window.OkButton.IsEnabled = false;
        window.Closed += (_, _) => engine.CancelCapture();
        return window;
    }

    private void OnKeyChanged(object? sender, EventArgs e) => OkButton.IsEnabled = KeyField.Value is { IsSet: true };

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (KeyCard.Visibility == Visibility.Visible)
        {
            if (KeyField.Value is not { IsSet: true } keys)
                return;
            Keys = keys.Clone();
        }
        Answer = AiAnswer.Done;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
