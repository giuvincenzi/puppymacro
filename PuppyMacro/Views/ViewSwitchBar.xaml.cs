using System;
using System.Windows;
using iNKORE.UI.WPF.Modern.Controls;

namespace PuppyMacro.Views;

/// <summary>The views of an editor: its fields, its JSON, its saved versions.</summary>
public enum EditorView
{
    Form,
    Code,
    History,
}

/// <summary>
/// The editors' Form view / Code view / History switch, with Format for the Code view (see <see cref="CodeViewSwitch{T}"/>).
/// History shows only where it is turned on (<see cref="HistoryAvailable"/>: an item that was saved before).
/// </summary>
public partial class ViewSwitchBar
{
    private bool _setting;

    public ViewSwitchBar()
    {
        InitializeComponent();
    }

    /// <summary>The user chose a view.</summary>
    public event Action<EditorView>? ViewRequested;

    public event Action? FormatRequested;

    /// <summary>Shows <paramref name="view"/> as the current view, without raising <see cref="ViewRequested"/>.</summary>
    public void Show(EditorView view)
    {
        _setting = true;
        Views.SelectedItem = view switch
        {
            EditorView.Code => CodeViewButton,
            EditorView.History => HistoryViewButton,
            _ => FormViewButton,
        };
        _setting = false;
        FormatButton.Visibility = view == EditorView.Code ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Form view can be chosen: off while the code has problems.</summary>
    public bool FormEnabled
    {
        get => FormViewButton.IsEnabled;
        set => FormViewButton.IsEnabled = HistoryViewButton.IsEnabled = value;
    }

    /// <summary>The History view is offered (an item that was saved before).</summary>
    public bool HistoryAvailable
    {
        get => HistoryViewButton.Visibility == Visibility.Visible;
        set => HistoryViewButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_setting || args.SelectedItem == null)
            return;
        ViewRequested?.Invoke(args.SelectedItem == CodeViewButton ? EditorView.Code
            : args.SelectedItem == HistoryViewButton ? EditorView.History
            : EditorView.Form);
    }

    private void OnFormatClick(object sender, RoutedEventArgs e) => FormatRequested?.Invoke();
}
