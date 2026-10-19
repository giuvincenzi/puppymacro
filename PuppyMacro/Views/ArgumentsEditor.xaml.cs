using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PuppyMacro.Views;

/// <summary>A call's arguments, one card per parameter of the called component (<see cref="ArgumentViewModel"/>).</summary>
public partial class ArgumentsEditor : UserControl
{
    public static readonly DependencyProperty ArgumentsProperty = DependencyProperty.Register(
        nameof(Arguments), typeof(IEnumerable), typeof(ArgumentsEditor),
        new PropertyMetadata(null, (d, e) => ((ArgumentsEditor)d).Cards.ItemsSource = e.NewValue as IEnumerable));

    public ArgumentsEditor() => InitializeComponent();

    /// <summary>The <see cref="ArgumentViewModel"/>s, one card each.</summary>
    public IEnumerable? Arguments
    {
        get => (IEnumerable?)GetValue(ArgumentsProperty);
        set => SetValue(ArgumentsProperty, value);
    }

    /// <summary>The first argument that can't be used as it is, or null.</summary>
    internal string? Problem => Arguments?.OfType<ArgumentViewModel>().Select(a => a.Problem).FirstOrDefault(p => p != null);
}
