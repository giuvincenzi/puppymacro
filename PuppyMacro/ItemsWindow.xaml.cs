using System.Windows;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// Import or Export of loops, macros and remaps as .puppyitems files: <see cref="Views.ImportItemsView"/> or
/// <see cref="Views.ExportItemsView"/> in a window of their own, with the main button and Cancel. True when the main button
/// was pressed.
/// </summary>
public partial class ItemsWindow
{
    internal ItemsWindow(string title, FrameworkElement content)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = title;
        Body.Content = content;
    }

    /// <summary>The main button's text ("Import 2 items"), and whether it can be pressed. An empty text hides it.</summary>
    internal void SetPrimary(string text, bool enabled)
    {
        PrimaryButton.Content = text;
        PrimaryButton.IsEnabled = enabled;
        PrimaryButton.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
