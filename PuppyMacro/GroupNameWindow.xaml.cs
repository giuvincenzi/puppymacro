using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Asks the name of a new group, or a new name for a group. On OK, <see cref="Result"/> holds it.</summary>
public partial class GroupNameWindow
{
    internal GroupNameWindow(string title, string okText, string name, string info)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        Title = title;
        GroupTitleBar.Title = title;
        OkButton.Content = okText;
        InfoText.Text = info;
        GroupNameBox.Text = name;
        Loaded += (_, _) =>
        {
            GroupNameBox.Focus();
            GroupNameBox.SelectAll();
        };
        UpdateOk();
    }

    public string? Result { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdateOk();

    private void UpdateOk()
    {
        if (OkButton != null)
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(GroupNameBox.Text);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GroupNameBox.Text))
            return;
        Result = GroupNameBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
