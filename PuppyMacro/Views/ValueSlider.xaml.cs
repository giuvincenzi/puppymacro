using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using PuppyMacro.Resources;

namespace PuppyMacro.Views;

/// <summary>
/// A slider on one line: its value as text, then a 200 px Slider (opacity of the overlay panel and of a floating
/// button, the sound volume, a macro's speed). By default a percentage in steps of 5; <see cref="Step"/> and
/// <see cref="Format"/> change that. <see cref="ValueChanged"/> follows the slider's.
/// </summary>
public partial class ValueSlider
{
    private Func<double, string> _format = v => string.Format(CultureInfo.CurrentCulture, Strings.Slider_Percent, (int)Math.Round(v));

    public ValueSlider()
    {
        InitializeComponent();
        ShowValue();
    }

    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

    public double Value
    {
        get => Bar.Value;
        set
        {
            Bar.Value = value;
            ShowValue(); // also when the value does not change, so the text is never empty
        }
    }

    public double Minimum
    {
        get => Bar.Minimum;
        set => Bar.Minimum = value;
    }

    public double Maximum
    {
        get => Bar.Maximum;
        set => Bar.Maximum = value;
    }

    /// <summary>The value snaps to multiples of it, and the arrow keys move by it (default 5).</summary>
    public double Step
    {
        get => Bar.TickFrequency;
        set
        {
            Bar.TickFrequency = value;
            Bar.SmallChange = value;
            Bar.LargeChange = value;
        }
    }

    /// <summary>The text shown for a value (default "85%").</summary>
    public Func<double, string> Format
    {
        get => _format;
        set
        {
            _format = value;
            ShowValue();
        }
    }

    /// <summary>The slider's name for UI Automation (screen readers, end-to-end tests).</summary>
    public string Label
    {
        get => AutomationProperties.GetName(Bar);
        set => AutomationProperties.SetName(Bar, value);
    }

    private void OnBarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ValueText == null)
            return; // still building
        ShowValue();
        ValueChanged?.Invoke(this, e);
    }

    private void ShowValue() => ValueText.Text = _format(Bar.Value);
}
