using System;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>
/// The one field that chooses a mouse button (left, right, middle, back, forward) by its virtual-key code: a list,
/// and Detect, which waits for a mouse button through <see cref="LoopEngine.BeginCapture"/> (the engine the window
/// gave the key fields, <see cref="KeyCaptureField.SetEngine"/>) and selects it.
/// </summary>
public partial class MouseButtonBox : UserControl
{
    public static readonly DependencyProperty VkProperty = DependencyProperty.Register(
        nameof(Vk), typeof(int), typeof(MouseButtonBox),
        new FrameworkPropertyMetadata(KeyNames.VK_LBUTTON, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MouseButtonBox)d).Select()));

    private bool _waiting;

    public MouseButtonBox()
    {
        InitializeComponent();
        Select();
        // A field that goes away (a removed row, a closed window) stops waiting.
        Unloaded += (_, _) => Cancel();
    }

    public int Vk
    {
        get => (int)GetValue(VkProperty);
        set => SetValue(VkProperty, value);
    }

    /// <summary>Detect got a key instead of a mouse button: the reason.</summary>
    public event Action<string>? Rejected;

    private void Select()
    {
        foreach (ComboBoxItem item in Box.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == Vk)
            {
                Box.SelectedItem = item;
                return;
            }
        }
        Box.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Box.SelectedItem is ComboBoxItem { Tag: string tag } && int.Parse(tag) != Vk)
            Vk = int.Parse(tag);
    }

    private void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (KeyCaptureField.GetEngine(this) is not { } engine)
            return;
        engine.CancelCapture(); // another field that waits goes back to its value
        ShowWaiting(true);
        // Left and right click are taken too: the user asked for a mouse button.
        engine.BeginCapture(OnCaptured, () => ShowWaiting(false), allowPrimaryMouse: true);
    }

    private void OnCaptured(HotkeyBinding binding)
    {
        ShowWaiting(false);
        if (KeyNames.IsMouse(binding.Vk))
            Vk = binding.Vk;
        else
            Rejected?.Invoke("Use Press key for keys.");
    }

    private void Cancel()
    {
        if (_waiting)
            KeyCaptureField.GetEngine(this)?.CancelCapture();
        ShowWaiting(false);
    }

    private void ShowWaiting(bool waiting)
    {
        _waiting = waiting;
        WaitRing.IsActive = waiting;
        WaitPanel.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        DetectButton.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        Box.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
    }
}
