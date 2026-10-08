using System;
using System.Collections.Generic;
using System.Text;
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

    /// <summary>Window classes of the shell (taskbar, desktop): never the target of a test.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
    };

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
        main?.Hide();
        ActivateWindowBehind();

        var timer = new DispatcherTimer { Interval = FocusDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            engine.TestMacroAction(action, () =>
            {
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
        uint ownProcess = (uint)Environment.ProcessId;
        var className = new StringBuilder(64);
        for (IntPtr w = NativeMethods.GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
        {
            if (!NativeMethods.IsWindowVisible(w) || NativeMethods.IsIconic(w))
                continue;
            NativeMethods.GetWindowThreadProcessId(w, out uint process);
            if (process == ownProcess)
                continue;
            long exStyle = NativeMethods.GetWindowLongPtr(w, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
                continue;
            // Cloaked: "visible" but not shown (suspended store apps, windows on other virtual desktops).
            if (NativeMethods.DwmGetWindowAttribute(w, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;
            className.Clear();
            NativeMethods.GetClassName(w, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString()))
                continue;

            NativeMethods.SetForegroundWindow(w);
            return;
        }
    }
}
