using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>
/// The editor's Test: PuppyMacro gets out of the way, the window behind it gets the focus, the
/// action plays once (<see cref="LoopEngine.TestMacroAction"/>) and PuppyMacro comes back.
/// </summary>
internal static class ActionTestSession
{
    /// <summary>Time for the window behind PuppyMacro to take the focus before the action plays.</summary>
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(200);

    /// <param name="from">
    /// The dialog the test starts from (the macro editor, or the action window over it). It and
    /// the dialogs under it are moved off screen, because a modal dialog cannot be hidden safely;
    /// the main window at the bottom of the chain is hidden.
    /// </param>
    public static void Run(LoopEngine engine, MacroAction action, Window from)
    {
        var moved = new List<(Window Window, double Left, double Top)>();
        Window? main = null;
        for (Window? w = from; w != null; w = w.Owner)
        {
            if (w.Owner == null)
                main = w;
            else
                moved.Add((w, w.Left, w.Top));
        }

        foreach (var (window, _, _) in moved)
            window.Left = SystemParameters.VirtualScreenLeft - window.ActualWidth - 200;
        // The main window is hidden too (it already is while an editor is open: then it stays hidden).
        bool mainWasVisible = main?.IsVisible == true;
        main?.Hide();
        ActivateWindowBehind();

        var timer = new DispatcherTimer { Interval = FocusDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            engine.TestMacroAction(action, () =>
            {
                if (mainWasVisible)
                    main?.Show();
                foreach (var (window, left, top) in moved)
                {
                    window.Left = left;
                    window.Top = top;
                }
                from.Activate();
            });
        };
        timer.Start();
    }

    /// <summary>
    /// Gives the focus to the first window, from the top of the z-order, that the user can see
    /// and that is not PuppyMacro's: the one PuppyMacro was covering. Without one, nothing changes.
    /// </summary>
    private static void ActivateWindowBehind()
    {
        IntPtr w = AppWindows.TopAppWindow();
        if (w != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(w);
    }
}
