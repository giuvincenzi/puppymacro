using System;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>
/// The box of one action (DataContext: <see cref="ActionBoxViewModel"/>), the same in a loop row and in the
/// macro's action window. A loop row's Remove raises <see cref="RemoveRequested"/>; a key the field does not
/// keep (a mouse button for Press key) raises <see cref="Rejected"/> with the reason.
/// </summary>
public partial class ActionBox : UserControl
{
    public ActionBox() => InitializeComponent();

    public ActionBoxViewModel? Action => DataContext as ActionBoxViewModel;

    /// <summary>A loop row's Remove was pressed.</summary>
    public event Action<ActionBox>? RemoveRequested;

    /// <summary>A pressed key was not kept: the reason.</summary>
    public event Action<string>? Rejected;

    /// <summary>Opens or closes the box (the macro's action window shows it open).</summary>
    public bool IsOpen
    {
        get => Expander.IsExpanded;
        set => Expander.IsExpanded = value;
    }

    private void OnKeyFieldLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not KeyCaptureField field || field.Validate != null)
            return;
        // Mouse buttons are Click and Mouse button down / up actions (middle and back / forward reach the field).
        field.Validate = binding => KeyNames.IsMouse(binding.Vk)
            ? (Action?.InLoop == true ? "Use Click for mouse buttons." : "Use a Click or Mouse button action for mouse buttons.")
            : null;
        field.Rejected += reason => Rejected?.Invoke(reason);
    }

    /// <summary>The mouse button field's Detect got a key.</summary>
    private void OnMouseRejected(string reason) => Rejected?.Invoke(reason);

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);
}
