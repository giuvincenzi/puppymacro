using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PuppyMacro.Views;

/// <summary>How big <see cref="KeyCaps"/> draws its keys.</summary>
public enum KeyCapSize
{
    /// <summary>As every key cap was before the lists' redesign: the key field, Import, Export, dialogs.</summary>
    Default,
    /// <summary>Smaller, so more keys fit: the Loops, Macros and Remap lists (List, Table, Grid).</summary>
    Small,
}

/// <summary>
/// Keys as key caps, the one way the app shows a key or a hotkey (ui.md): <see cref="Keys"/> (key names, KeyNames.Parts),
/// <see cref="Size"/> and <see cref="Accent"/>. Its sizes are static, so the XAML draws with them and <see cref="MeasureWidth"/>
/// measures with them.
/// </summary>
public partial class KeyCaps : UserControl
{
    // The KeyCap style's (App.xaml) border: 1 on the left and on the right.
    private const double BorderWidth = 2;

    public static Thickness DefaultPadding { get; } = new(6, 1, 6, 1);
    public static Thickness DefaultGap { get; } = new(0, 0, 4, 0);
    public static Thickness SmallPadding { get; } = new(4, 0, 4, 0);
    public static Thickness SmallGap { get; } = new(0, 0, 3, 0);
    /// <summary>The library's caption size: Small keys read as the captions around them.</summary>
    public static double SmallFontSize { get; } = 12;

    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(IEnumerable), typeof(KeyCaps), new PropertyMetadata(null));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(KeyCapSize), typeof(KeyCaps), new PropertyMetadata(KeyCapSize.Default));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(bool), typeof(KeyCaps), new PropertyMetadata(false));

    public KeyCaps() => InitializeComponent();

    /// <summary>The key names, left to right (KeyNames.Parts).</summary>
    public IEnumerable? Keys { get => (IEnumerable?)GetValue(KeysProperty); set => SetValue(KeysProperty, value); }

    /// <summary>Default unless set: Small in the lists.</summary>
    public KeyCapSize Size { get => (KeyCapSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>In the accent color: a key the user chooses (the key field, Import, Export).</summary>
    public bool Accent { get => (bool)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>
    /// How wide these keys are drawn in this size: each name as text in <paramref name="font"/> at the size's text size
    /// (<paramref name="defaultFontSize"/> for Default, the library's 14), plus the padding, the border and the gap after it.
    /// </summary>
    public static double MeasureWidth(IEnumerable<string> keys, KeyCapSize size, FontFamily font, double defaultFontSize, double pixelsPerDip)
    {
        bool small = size == KeyCapSize.Small;
        Thickness padding = small ? SmallPadding : DefaultPadding, gap = small ? SmallGap : DefaultGap;
        var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double fontSize = small ? SmallFontSize : defaultFontSize;
        return keys.Sum(key => new FormattedText(key, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize,
                Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace
            + padding.Left + padding.Right + BorderWidth + gap.Right);
    }
}
