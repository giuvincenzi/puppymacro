using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Resources;

namespace PuppyMacro.Views;

/// <summary>
/// A grid's value (DataContext: <see cref="ValueEditorViewModel"/> of a Grid): a card that opens on its fields. Show the
/// cells marks every cell's point over a still picture of the screen (<see cref="ScreenPickWindow.ShowCells"/>).
/// </summary>
public partial class GridValueEditor : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(GridValueEditor), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(GridValueEditor), new PropertyMetadata(""));

    public GridValueEditor() => InitializeComponent();

    /// <summary>The card's title ("Example", "Default", a parameter's name).</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>What the value is for, under the title.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private void OnShowCellsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ValueEditorViewModel model || Window.GetWindow(this) is not { } window || model.CellPoints.Count == 0)
            return;
        if (!ScreenPickWindow.ShowCells(window, model.CellPoints, out string? error))
            MessageBox.Show(window, error, Strings.ScreenPick_CellsTitle, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK,
                App.MessageBoxOptions);
    }
}
