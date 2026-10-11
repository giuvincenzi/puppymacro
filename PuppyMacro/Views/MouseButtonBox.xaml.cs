using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>The one field that chooses a mouse button (left, right, middle, back, forward) by its virtual-key code.</summary>
public partial class MouseButtonBox : UserControl
{
    public static readonly DependencyProperty VkProperty = DependencyProperty.Register(
        nameof(Vk), typeof(int), typeof(MouseButtonBox),
        new FrameworkPropertyMetadata(KeyNames.VK_LBUTTON, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((MouseButtonBox)d).Select()));

    public MouseButtonBox()
    {
        InitializeComponent();
        Select();
    }

    public int Vk
    {
        get => (int)GetValue(VkProperty);
        set => SetValue(VkProperty, value);
    }

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
}
