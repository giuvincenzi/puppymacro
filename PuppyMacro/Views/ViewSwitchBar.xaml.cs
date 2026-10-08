using System;
using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;

namespace PuppyMacro.Views;

/// <summary>The editors' Form view / Code view switch, with Format for the Code view (see <see cref="CodeViewSwitch{T}"/>).</summary>
public partial class ViewSwitchBar
{
    private bool _setting;

    public ViewSwitchBar()
    {
        InitializeComponent();
    }

    /// <summary>The user chose a view: true for the Code view.</summary>
    public event Action<bool>? ViewRequested;

    public event Action? FormatRequested;

    /// <summary>Shows <paramref name="code"/> as the current view, without raising <see cref="ViewRequested"/>.</summary>
    public void Show(bool code)
    {
        _setting = true;
        Views.SelectedItem = code ? CodeViewButton : FormViewButton;
        _setting = false;
        FormatButton.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Form view can be chosen: off while the code has problems.</summary>
    public bool FormEnabled
    {
        get => FormViewButton.IsEnabled;
        set => FormViewButton.IsEnabled = value;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_setting && args.SelectedItem != null)
            ViewRequested?.Invoke(args.SelectedItem == CodeViewButton);
    }

    private void OnFormatClick(object sender, RoutedEventArgs e) => FormatRequested?.Invoke();
}
