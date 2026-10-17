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
        // At once, before the capture starts: from another thread it waits for the UI thread (half a second at most).
        ScreenCapture.Shared.ActiveChanged += active =>
        {
            if (dispatcher.CheckAccess())
                ApplyAll(active);
            else
                dispatcher.Invoke(() => ApplyAll(active), DispatcherPriority.Send, System.Threading.CancellationToken.None, TimeSpan.FromMilliseconds(500));
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
