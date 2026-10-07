using System;
using System.Collections.Generic;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// The whole recording flow: "Ready to record" prompt (hotkey or Start recording with a
/// 3-second countdown), the recording bar with Stop, then the recorded events turned into
/// macro actions.
/// </summary>
internal static class RecordingSession
{
    private const int CountdownSeconds = 3;

    /// <param name="hide">Hides PuppyMacro's window while recording.</param>
    /// <param name="restore">Brings it back (on cancel and before <paramref name="done"/>).</param>
    /// <param name="done">Called with the recorded actions. Not called on cancel.</param>
    public static void Run(LoopEngine engine, AppSettings settings, Action hide, Action restore,
        Action<List<MacroAction>> done)
    {
        string hotkey = KeyNames.Format(settings.RecordHotkey);
        bool previousSuspended = engine.HotkeysSuspended;
        engine.HotkeysSuspended = true;

        var prompt = new RecordPromptWindow(hotkey, settings.RecordMouseMovement);
        RecordingBarWindow? bar = null;
        Action<int>? progress = null;
        bool stoppedAlready = false;

        void Finish()
        {
            if (progress != null)
                engine.RecordingProgress -= progress;
            bar?.Close();
            engine.HotkeysSuspended = previousSuspended;
            restore();
        }

        engine.ArmRecording(
            () => prompt.RecordMouseMovement,
            started: () =>
            {
                prompt.Started();
                bar?.ShowRecording(); // the hotkey can also start it during the countdown
            },
            stopped: events =>
            {
                stoppedAlready = true;
                Finish();
                done(MacroBuilder.Build(events));
            });

        hide();
        if (prompt.ShowDialog() != true)
        {
            engine.DisarmRecording();
            Finish();
            return;
        }

        settings.RecordMouseMovement = prompt.RecordMouseMovement;
        if (stoppedAlready)
            return; // started and stopped before the prompt closed

        bar = new RecordingBarWindow(hotkey);
        bar.StopRequested += engine.StopRecording;
        bar.CountdownCancelled += () =>
        {
            engine.DisarmRecording();
            Finish();
        };
        progress = bar.SetCount;
        engine.RecordingProgress += progress;

        if (prompt.StartNow && !engine.IsRecording)
            bar.StartCountdown(CountdownSeconds, engine.StartRecordingNow);
        bar.Show();
    }
}
