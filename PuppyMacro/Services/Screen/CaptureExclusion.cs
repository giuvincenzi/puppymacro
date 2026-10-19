using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// While a macro's condition looks at the screen, PuppyMacro's windows are left out of every capture of the screen
/// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004 and later), so a condition sees what is under them. Only then: the user's own
/// screenshots and recordings show PuppyMacro the rest of the time.
/// </summary>
internal static class CaptureExclusion
{
    private static bool _active;

    /// <summary>Follows <see cref="ScreenCapture.ActiveChanged"/> on the UI thread, for the windows open and those that open.</summary>
    public static void Start(Dispatcher dispatcher)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (_active && sender is Window w)
                    Apply(w, true);
            }));
        // Each notification applies the capture's state as it is when it runs, not the one it carried: one that comes late or
        // out of order can never leave the windows excluded (or not) against the capture. BeginInvoke is never dropped (an
        // Invoke with a timeout is, when the UI thread is busy). Before a capture starts, the thread that starts it waits for it
        // half a second at most, so the first frames do not show PuppyMacro's windows.
        ScreenCapture.Shared.ActiveChanged += active =>
        {
            if (dispatcher.CheckAccess())
            {
                ApplyAll(ScreenCapture.Shared.Active);
                return;
            }
            var operation = dispatcher.BeginInvoke(() => ApplyAll(ScreenCapture.Shared.Active), DispatcherPriority.Send);
            if (active)
                operation.Wait(TimeSpan.FromMilliseconds(500));
        };
    }

    private static void ApplyAll(bool active)
    {
        _active = active;
        foreach (Window w in Application.Current.Windows)
            Apply(w, active);
    }

    private static void Apply(Window window, bool exclude)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        // Older Windows refuse WDA_EXCLUDEFROMCAPTURE: the window then simply stays in the capture.
        PInvoke.SetWindowDisplayAffinity(new HWND(handle),
            exclude ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE);
    }
}
