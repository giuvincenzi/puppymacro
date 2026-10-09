using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PuppyMacro.Views;

/// <summary>
/// The sound choices of the loop and macro editors sit in a ScrollViewer of their own, so the long
/// list does not make the whole editor scroll. Each time the list is shown, it scrolls to the chosen
/// sound so the choice is visible. Only that ScrollViewer moves, never the editor around it.
/// </summary>
internal static class SoundChoicesScroll
{
    /// <summary>Call from the editor's constructor; <paramref name="chosen"/> returns the chosen sound's name.</summary>
    public static void Attach(ScrollViewer scroll, Func<string> chosen)
    {
        scroll.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollTo(scroll, chosen()));
        };
    }

    private static void ScrollTo(ScrollViewer scroll, string name)
    {
        RadioButton? button = Find(scroll, name);
        if (button == null || !scroll.IsAncestorOf(button))
            return;
        Point top = button.TransformToAncestor(scroll).Transform(new Point(0, 0));
        bool visible = top.Y >= 0 && top.Y + button.ActualHeight <= scroll.ViewportHeight;
        if (!visible)
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top.Y);
    }

    private static RadioButton? Find(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { Content: string content } button && content == name)
                return button;
            if (Find(child, name) is { } found)
                return found;
        }
        return null;
    }
}
