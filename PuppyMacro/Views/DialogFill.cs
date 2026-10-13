using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;

namespace PuppyMacro.Views;

/// <summary>
/// A bigger <see cref="ContentDialog"/> whose content fills it (Import and Export of loops, macros and remaps, approved: the
/// Design canvas "PuppyMacro Import, remap without key, title bar"): 800 x 900 px, or the window's size less a margin when it is
/// smaller, set through the library's own ContentDialogMin/MaxWidth and Height resources. The dialog shows its content in a
/// ScrollViewer, where a * row cannot stretch: the content gets the height the dialog shows it, so its list takes the free
/// space and what is under the list stays at the bottom.
/// </summary>
internal static class DialogFill
{
    private const double Width = 800;
    private const double Height = 900;

    /// <summary>The space left around the dialog inside the window.</summary>
    private const double Margin = 48;

    /// <summary>Call before the dialog is shown.</summary>
    public static void Apply(ContentDialog dialog, Window owner, FrameworkElement content)
    {
        double width = Math.Max(320, Math.Min(Width, owner.ActualWidth - Margin));
        double height = Math.Max(320, Math.Min(Height, owner.ActualHeight - Margin));
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinHeight"] = height;
        dialog.Resources["ContentDialogMaxHeight"] = height;

        content.Loaded += (_, _) =>
        {
            if (Ancestor<ScrollViewer>(content) is not { } scroll)
                return;
            void Fill()
            {
                // What is above the content in the scrolled area (the title) stays above it.
                double top = content.TranslatePoint(new Point(0, 0), scroll).Y + scroll.VerticalOffset;
                content.Height = Math.Max(content.MinHeight, scroll.ViewportHeight - top);
            }
            scroll.SizeChanged += (_, _) => Fill();
            Fill();
        };
    }

    private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T found)
                return found;
        return null;
    }
}
