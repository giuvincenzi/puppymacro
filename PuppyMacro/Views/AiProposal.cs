using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// The windows PuppyMacro opens for an AI app's request (an editor with its proposal, a confirmation, a key to press):
/// the "Proposed by" bar, and in front of the other windows, since the AI app is the window the user was using.
/// </summary>
internal static class AiProposal
{
    /// <summary>Opens an editor's ProposalBar: "Proposed by Claude Desktop."</summary>
    public static void ShowBar(InfoBar bar, string app)
    {
        bar.Title = $"Proposed by {app}.";
        bar.Message = "Check it, change what you want, then Save. Nothing is saved until you do.";
        bar.IsOpen = true;
    }

    /// <summary>
    /// The window opens on top of the other apps' windows (the AI app is in front, and Windows may not let PuppyMacro take
    /// the focus), then stops being topmost once the user has activated it.
    /// </summary>
    public static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Activated += (_, _) => window.Topmost = false;
        window.Loaded += (_, _) => window.Activate();
    }
}
