using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace PuppyMacro.Views;

/// <summary>
/// A <see cref="SettingsExpander"/> with a switch in its header that opens only while <see cref="CanExpand"/> is true
/// (the switch is on): otherwise its arrow is hidden and a click or Space/Enter on the header does not open it, while
/// the switch keeps working. Custom on purpose, approved by the user (ui.md). The parts are the library's template's:
/// the header toggle button "ExpanderHeader" and, inside it, the arrow "ExpandCollapseChevronBorder".
/// </summary>
public class SwitchSettingsExpander : SettingsExpander
{
    public static readonly DependencyProperty CanExpandProperty = DependencyProperty.Register(
        nameof(CanExpand), typeof(bool), typeof(SwitchSettingsExpander),
        new PropertyMetadata(true, (d, _) => ((SwitchSettingsExpander)d).Apply()));

    private ToggleButton? _header;

    public SwitchSettingsExpander()
    {
        Loaded += (_, _) => Hook();
    }

    /// <summary>False: the expander stays closed and shows no arrow.</summary>
    public bool CanExpand
    {
        get => (bool)GetValue(CanExpandProperty);
        set => SetValue(CanExpandProperty, value);
    }

    private void Hook()
    {
        if (_header != null)
            return;
        _header = FindHeader(this);
        if (_header == null)
            return;
        _header.PreviewMouseLeftButtonDown += OnHeaderMouseDown;
        _header.PreviewKeyDown += OnHeaderKeyDown;
        _header.ApplyTemplate();
        Apply();
    }

    private void Apply()
    {
        if (!CanExpand)
            IsExpanded = false;
        if (_header?.Template?.FindName("ExpandCollapseChevronBorder", _header) is UIElement arrow)
            arrow.Visibility = CanExpand ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The header button opens the expander on a click; while it cannot, only the controls inside it get the click.
        if (!CanExpand && !IsInsideControl(e.OriginalSource as DependencyObject))
            e.Handled = true;
    }

    private void OnHeaderKeyDown(object sender, KeyEventArgs e)
    {
        if (!CanExpand && e.OriginalSource == _header && e.Key is Key.Space or Key.Enter)
            e.Handled = true;
    }

    /// <summary>A control in the header (the switch), not the header button or the card that shows the header.</summary>
    private bool IsInsideControl(DependencyObject? element)
    {
        for (; element != null && element != _header; element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
        {
            if (element is ToggleSwitch or TextBoxBase or Selector or RangeBase)
                return true;
            if (element is ButtonBase and not SettingsCard)
                return true;
        }
        return false;
    }

    private static ToggleButton? FindHeader(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ToggleButton { Name: "ExpanderHeader" } header)
                return header;
            if (child is not SettingsExpander && FindHeader(child) is ToggleButton found)
                return found;
        }
        return null;
    }
}
