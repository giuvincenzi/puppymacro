using System.Windows;

namespace PuppyMacro.Views;

/// <summary>The windows PuppyMacro opens for an AI app's request (a confirmation, a key to press).</summary>
internal static class AiWindow
{
    /// <summary>
    /// The window opens on top of the other apps' windows (the AI app is in front, and Windows may not let PuppyMacro take
    /// the focus), then stops being topmost once the user has activated it.
    /// </summary>
    public static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Activated += (_, _) => window.Topmost = false;
        // Not on Loaded: a window sized to its content is loaded before it is shown, and Activate would throw.
        window.ContentRendered += (_, _) => window.Activate();
    }
}
