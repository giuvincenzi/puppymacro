using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services.Components;

namespace PuppyMacro.Views;

/// <summary>
/// A component parameter's value (DataContext: <see cref="ValueEditorViewModel"/>): the screen pickers for a color, images
/// and an area are the conditions' own (<see cref="ScreenPickWindow"/>).
/// </summary>
public partial class ValueEditor : UserControl
{
    public ValueEditor() => InitializeComponent();

    private ValueEditorViewModel? Model => DataContext as ValueEditorViewModel;

    private void OnPickColorClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Window.GetWindow(this) is not { } window)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Pixel, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_PixelTitle);
        if (pick?.PickedPixel is { } pixel)
            model.Color = pixel.Color;
    }

    private void OnTakeImageClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { Store: { } store } model || Window.GetWindow(this) is not { } window)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Image, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_ImageTitle);
        if (pick?.PickedArea is not { Png: { } png } taken)
            return;
        // As a condition's image: in the store at once, held for the editor until it closes.
        string id = store.Add(png);
        store.HoldAlso(window.Owner ?? window, id);
        model.Images = new List<ConditionImage> { new() { Id = id, X = taken.Area.X, Y = taken.Area.Y } };
    }

    private void OnDrawAreaClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Window.GetWindow(this) is not { } window)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Area, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_AreaTitle);
        if (pick?.PickedArea is { } area)
            model.Area = new AreaValue(area.Area.X, area.Area.Y, area.Area.Width, area.Area.Height);
    }

    private void ShowError(string message, string title) =>
        MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, App.MessageBoxOptions);
}
