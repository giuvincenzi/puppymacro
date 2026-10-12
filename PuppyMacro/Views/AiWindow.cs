using System.Globalization;
using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;
using PuppyMacro.Resources;

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

    /// <summary>An editor opened for an AI app (open_item): its notice bar shows why, as the AI wrote it.</summary>
    public static void Notice(InfoBar bar, string app, string title, string description)
    {
        bar.Title = title;
        bar.Message = description.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.AiRequest_NoticeWithDescription, description, app)
            : string.Format(CultureInfo.CurrentCulture, Strings.AiRequest_NoticeAskedBy, app);
        // The InfoBar does not expose its texts to UI Automation: its name carries the title for screen readers.
        System.Windows.Automation.AutomationProperties.SetName(bar, title);
        bar.IsOpen = true;
    }
}
