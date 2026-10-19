using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Views;

/// <summary>
/// What an If's conditions can use besides themselves: the Library (a Component condition calls one of its components), the
/// parameters of the component being edited (a field can take one's value, an argument can pass one on), and the places
/// the Ifs around this one find (a point argument can take one). <see cref="None"/>: a macro with no Library at hand.
/// </summary>
public sealed class ConditionContext
{
    public static ConditionContext None { get; } = new();

    internal MacroLibrary? Macros { get; init; }

    internal IReadOnlyList<ComponentParameter>? Parameters { get; init; }

    internal IReadOnlyDictionary<Guid, string> Found { get; init; } = new Dictionary<Guid, string>();

    internal ComponentSignatures Signatures => Macros?.Library.Signatures() ?? ComponentSignatures.Empty;
}

/// <summary>Something the condition editor shows that changes.</summary>
public abstract class ConditionEditorItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Changed(params string[] names)
    {
        foreach (string name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected static string Fill(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}

/// <summary>One image of an Image condition: the picture and where it was taken.</summary>
public sealed class ConditionImageViewModel : ConditionEditorItem
{
    private bool _canRemove;

    internal ConditionImageViewModel(ConditionImage image, ImageStore? store)
    {
        Image = image.Clone();
        if (store?.Read(image.Id) is { } png)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(png);
                bitmap.EndInit();
                bitmap.Freeze();
                Preview = bitmap;
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    public ConditionImage Image { get; }

    /// <summary>The image, at its size in pixels; null when PuppyMacro does not have it.</summary>
    public ImageSource? Preview { get; }

    public double PreviewWidth => Preview is BitmapSource b ? b.PixelWidth : 0;
    public double PreviewHeight => Preview is BitmapSource b ? b.PixelHeight : 0;

    /// <summary>"184 × 52 pixels, taken at 1812, 1210", or that the image is missing.</summary>
    public string Description => Preview is BitmapSource b
        ? Fill(Strings.Condition_ImageInfo, b.PixelWidth, b.PixelHeight, Image.X, Image.Y)
        : Strings.Condition_ImageMissing;

    /// <summary>More than one image: each one can be removed.</summary>
    public bool CanRemove
    {
        get => _canRemove;
        set { if (_canRemove != value) { _canRemove = value; Changed(); } }
    }
}

/// <summary>One condition of an If, as the condition editor shows it.</summary>
public sealed class ConditionViewModel : ConditionEditorItem
{
    private readonly MacroCondition _condition;
    private readonly ImageStore? _store;
    private string _name;
    private double _x, _y, _tolerance, _similarity;
    private string _color;
    private bool _not, _nearbySizes, _matchCase, _isExpanded = true;
    private int _areaX, _areaY, _areaWidth, _areaHeight;
    private SearchArea _area;
    private string _text;
    private TextMatch _textMatch;
    private string? _language;
    private string? _result;
    private bool _resultMet;
    private readonly ConditionContext _context;
    private Guid? _componentId;
    private List<ArgumentViewModel> _arguments = new();

    internal ConditionViewModel(MacroCondition condition, ImageStore? store, ConditionContext? context = null)
    {
        _condition = condition.Clone();
        _store = store;
        _context = context ?? ConditionContext.None;
        Slots = new SlotBindings(ComponentSlots.Of(condition), _context.Parameters, condition.Bind);
        foreach (var choice in Slots.All)
            choice.PropertyChanged += (_, _) => Changed(nameof(Slots));
        Slots.Area.PropertyChanged += (_, _) => Changed(nameof(ShowDraw));
        Slots.Point.PropertyChanged += (_, _) => Changed(nameof(ShowCell));
        _cell = condition.Cell ?? 1;
        _componentId = condition.ComponentId;
        if (Kind == ConditionKind.Component)
            LoadArguments(condition.Arguments);
        _name = condition.Name ?? "";
        _not = condition.Not;
        _x = condition.X;
        _y = condition.Y;
        _color = condition.Color ?? "";
        _tolerance = condition.Tolerance;
        _similarity = condition.Similarity;
        _nearbySizes = condition.NearbySizes;
        _area = condition.Area;
        _areaX = condition.AreaX;
        _areaY = condition.AreaY;
        _areaWidth = condition.AreaWidth;
        _areaHeight = condition.AreaHeight;
        _text = condition.Text ?? "";
        _textMatch = condition.TextMatch;
        _matchCase = condition.MatchCase;
        _language = condition.Language;
        foreach (var image in condition.Images ?? new List<ConditionImage>())
            Images.Add(new ConditionImageViewModel(image, store));
        Images.CollectionChanged += (_, _) => ImagesChanged();
        ImagesChanged();
        // Whether the card shows follows what the condition has (a color, an image, a text with its area).
        PropertyChanged += (_, e) =>
        {
            if (!ShownOnly.Contains(e.PropertyName ?? ""))
                Changed(nameof(ShowNow));
        };
        // The whole screen of a new condition: the monitor around it.
        if (_area == SearchArea.Screen && _areaWidth <= 0)
            SetArea(ScreenAround());
    }

    /// <summary>A new condition of a kind, with the usual values.</summary>
    internal static MacroCondition New(ConditionKind kind) => new()
    {
        Kind = kind,
        Tolerance = 5,
        Similarity = 90,
        Area = kind == ConditionKind.Text ? SearchArea.Screen : SearchArea.Around,
    };

    public ConditionKind Kind => _condition.Kind;

    /// <summary>The condition's Id, which "Where … was found" names.</summary>
    public Guid Id => _condition.Id;
    public bool IsPixel => Kind == ConditionKind.Pixel;
    public bool IsImage => Kind == ConditionKind.Image;
    public bool IsText => Kind == ConditionKind.Text;
    public bool IsComponent => Kind == ConditionKind.Component;
    public bool Finds => !IsPixel;

    /// <summary>Image and Text: where they look (a Component condition looks where its component's condition does).</summary>
    public bool ShowsArea => IsImage || IsText;

    // ---- Inside a component: fields from its parameters ----

    /// <summary>Where each field's value comes from, inside a component (<see cref="SlotBindings"/>).</summary>
    public SlotBindings Slots { get; }

    private double _cell = 1;

    /// <summary>A pixel at a cell of a Grid parameter: the Cell card shows.</summary>
    public bool ShowCell => IsPixel && Slots.Point.IsGrid;

    /// <summary>With <see cref="ShowCell"/>: the cell, when it isn't a parameter's.</summary>
    public double Cell
    {
        get => _cell;
        set { _cell = value; Changed(); }
    }

    // ---- Component ----

    private LibraryComponent? Callee => _componentId is Guid id ? _context.Macros?.Library.Find(id)?.Component : null;

    /// <summary>The component it calls, by name; or that none is chosen, or that the Library doesn't have it.</summary>
    public string ComponentName => Callee?.Name ?? (_componentId == null ? Strings.Condition_NoComponent : Strings.Code_ComponentMissing);

    /// <summary>The called component's arguments, one card each.</summary>
    public List<ArgumentViewModel> Arguments
    {
        get => _arguments;
        private set { _arguments = value; Changed(nameof(Arguments), nameof(HasArguments)); }
    }

    public bool HasArguments => _arguments.Count > 0;

    /// <summary>Calls another component (the Choose button): its arguments start from its examples.</summary>
    internal void ChooseComponent(LibraryComponent component)
    {
        _componentId = component.Id;
        LoadArguments(ArgumentViewModel.Starting(component));
        Changed(nameof(ComponentName), nameof(Summary));
    }

    private void LoadArguments(Dictionary<string, System.Text.Json.Nodes.JsonNode?>? arguments)
    {
        var list = Callee is { } callee
            ? ArgumentViewModel.For(callee, arguments, _context.Signatures, _context.Parameters, _context.Found, _store, asCondition: true)
            : new List<ArgumentViewModel>();
        foreach (var argument in list)
        {
            argument.PropertyChanged += (_, _) => Changed(nameof(Summary));
            argument.Value.PropertyChanged += (_, _) => Changed(nameof(Summary));
        }
        Arguments = list;
    }

    public string Title => KindName(Kind);

    /// <summary>The word a new condition's name starts with ("Image 1").</summary>
    public static string KindShortName(ConditionKind kind) => kind == ConditionKind.Text ? Strings.Condition_TextShort : Strings.Condition_ImageShort;

    /// <summary>The kind's name in the Add condition menus: Text on screen says it reads with OCR.</summary>
    public static string MenuName(ConditionKind kind) => kind switch
    {
        ConditionKind.Text => Strings.Condition_TextMenu,
        ConditionKind.Component => Strings.Condition_ComponentMenu,
        _ => KindName(kind),
    };

    public static string KindName(ConditionKind kind) => kind switch
    {
        ConditionKind.Image => Strings.Condition_Image,
        ConditionKind.Text => Strings.Condition_Text,
        ConditionKind.Component => Strings.Condition_Component,
        _ => Strings.Condition_Pixel,
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Changed(); }
    }

    // ---- True when (it is there, or it is not) ----

    /// <summary>The True when list: what it looks for is there, or it is not.</summary>
    public IReadOnlyList<string> NotChoices => Kind switch
    {
        ConditionKind.Pixel => new[] { Strings.Condition_PixelHas, Strings.Condition_PixelHasNot },
        ConditionKind.Image => new[] { Strings.Condition_ImageIs, Strings.Condition_ImageIsNot },
        ConditionKind.Component => new[] { Strings.Condition_ComponentFinds, Strings.Condition_ComponentFindsNot },
        _ => new[] { Strings.Condition_TextIs, Strings.Condition_TextIsNot },
    };

    /// <summary>True when what it looks for is not there; it then gives no place to the actions in Then.</summary>
    public bool Not
    {
        get => _not;
        set
        {
            if (_not == value)
                return;
            _not = value;
            Changed(nameof(Not), nameof(NotIndex), nameof(Summary));
        }
    }

    public int NotIndex
    {
        get => _not ? 1 : 0;
        set => Not = value == 1;
    }

    // ---- Name (Image and Text) ----

    public string Name
    {
        get => _name;
        set { _name = value ?? ""; Changed(nameof(Name), nameof(Summary)); }
    }

    // ---- Pixel ----

    public double X
    {
        get => _x;
        set { if (_x.Equals(value)) return; _x = value; Changed(nameof(X), nameof(Summary)); }
    }

    public double Y
    {
        get => _y;
        set { if (_y.Equals(value)) return; _y = value; Changed(nameof(Y), nameof(Summary)); }
    }

    public string Color
    {
        get => _color;
        set { _color = value ?? ""; Changed(nameof(Color), nameof(Swatch), nameof(Summary)); }
    }

    /// <summary>The color as a brush, transparent while the text is not a color.</summary>
    public Brush Swatch => MacroJson.IsColor(_color.Trim().ToUpperInvariant()) && (ColorConverter.ConvertFromString(_color.Trim()) is Color c)
        ? new SolidColorBrush(c)
        : Brushes.Transparent;

    public double Tolerance
    {
        get => _tolerance;
        set
        {
            double v = Math.Clamp(Math.Round(value), 0, 100);
            if (v == _tolerance)
                return; // the slider showing the value is no change
            _tolerance = v;
            Changed(nameof(Tolerance), nameof(Summary));
        }
    }

    // ---- Image ----

    public ObservableCollection<ConditionImageViewModel> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    private void ImagesChanged()
    {
        foreach (var image in Images)
            image.CanRemove = Images.Count > 1;
        Changed(nameof(HasImages), nameof(TakeText), nameof(Summary), nameof(AreaText));
    }

    /// <summary>The image button: Take on screen, then Take another image (the condition is true with any of them).</summary>
    public string TakeText => HasImages ? Strings.Condition_TakeAnother : Strings.Condition_TakeOnScreen;

    public bool NearbySizes
    {
        get => _nearbySizes;
        set { _nearbySizes = value; Changed(); }
    }

    public double Similarity
    {
        get => _similarity;
        set
        {
            double v = Math.Clamp(Math.Round(value), 50, 100);
            if (v == _similarity)
                return;
            _similarity = v;
            Changed(nameof(Similarity), nameof(Summary));
        }
    }

    // ---- Search area (Image and Text) ----

    /// <summary>The Search area list: Image: Around where it was taken, The whole screen, An area I draw; Text: the last two.</summary>
    public IReadOnlyList<string> AreaChoices => IsImage
        ? new[] { Strings.Condition_AreaAround, Strings.Condition_AreaScreen, Strings.Condition_AreaDrawn }
        : new[] { Strings.Condition_AreaScreen, Strings.Condition_AreaDrawn };

    public int AreaIndex
    {
        get => Array.IndexOf(AreaOrder, _area);
        set
        {
            var order = AreaOrder;
            var area = order[Math.Clamp(value, 0, order.Length - 1)];
            if (area == _area)
                return;
            _area = area;
            if (area == SearchArea.Screen)
                SetArea(ScreenAround());
            Changed(nameof(AreaIndex), nameof(IsDrawn), nameof(ShowDraw), nameof(ShowAreaChoice), nameof(AreaText), nameof(Summary));
        }
    }

    /// <summary>The areas in the list's order.</summary>
    private SearchArea[] AreaOrder => IsImage
        ? new[] { SearchArea.Around, SearchArea.Screen, SearchArea.Drawn }
        : new[] { SearchArea.Screen, SearchArea.Drawn };

    public bool IsDrawn => _area == SearchArea.Drawn;

    /// <summary>Draw on screen: a drawn area of its own (not a parameter's).</summary>
    public bool ShowDraw => IsDrawn && Slots.Area.ShowValue;

    /// <summary>Inside a component: a drawn or whole-screen area can take an Area parameter.</summary>
    public bool ShowAreaChoice => Slots.Area.Show && _area != SearchArea.Around;

    /// <summary>The monitor where the image was taken (or the first one), for The whole screen.</summary>
    private OpenCvSharp.Rect ScreenAround()
    {
        var monitors = ScreenCapture.MonitorBounds();
        var point = Images.Count > 0 ? new OpenCvSharp.Point(Images[0].Image.X, Images[0].Image.Y)
            : _areaWidth > 0 ? new OpenCvSharp.Point(_areaX + _areaWidth / 2, _areaY + _areaHeight / 2)
            : new OpenCvSharp.Point(0, 0);
        return monitors.FirstOrDefault(m => m.Contains(point), monitors.FirstOrDefault());
    }

    internal void SetArea(OpenCvSharp.Rect area)
    {
        (_areaX, _areaY, _areaWidth, _areaHeight) = (area.X, area.Y, area.Width, area.Height);
        Changed(nameof(AreaText), nameof(Summary));
    }

    /// <summary>Under Search area: where it looks.</summary>
    public string AreaText => _area switch
    {
        SearchArea.Around => Strings.Condition_AreaAroundDescription,
        _ when _areaWidth <= 0 => Strings.Condition_AreaNotDrawn,
        _ => Fill(Strings.Condition_AreaInfo, _areaX, _areaY, _areaWidth, _areaHeight),
    };

    // ---- Text ----

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Changed(nameof(Text), nameof(Summary)); }
    }

    /// <summary>0 Contains the text, 1 Exactly these words.</summary>
    public int TextMatchIndex
    {
        get => (int)_textMatch;
        set { _textMatch = value == 1 ? TextMatch.Exact : TextMatch.Contains; Changed(nameof(TextMatchIndex), nameof(Summary)); }
    }

    public bool MatchCase
    {
        get => _matchCase;
        set { _matchCase = value; Changed(); }
    }

    /// <summary>The Language list: Windows' language first, then the languages Windows can read on this PC.</summary>
    public IReadOnlyList<string> LanguageChoices
    {
        get
        {
            var list = new List<string>
            {
                TextReader.WindowsLanguage is { } windows
                    ? Fill(Strings.Condition_WindowsLanguageNamed, TextReader.NativeName(windows))
                    : Strings.Condition_WindowsLanguage,
            };
            list.AddRange(LanguageTags.Select(TextReader.NativeName));
            return list;
        }
    }

    /// <summary>The languages Windows can read, and the condition's own when it is not one of them (another PC's).</summary>
    private List<string> LanguageTags
    {
        get
        {
            var tags = TextReader.Languages.ToList();
            if (_language != null && !tags.Contains(_language, StringComparer.OrdinalIgnoreCase))
                tags.Add(_language);
            return tags;
        }
    }

    public int LanguageIndex
    {
        get => _language == null ? 0 : LanguageTags.FindIndex(t => string.Equals(t, _language, StringComparison.OrdinalIgnoreCase)) + 1;
        set
        {
            var tags = LanguageTags;
            _language = value > 0 && value <= tags.Count ? tags[value - 1] : null;
            Changed();
        }
    }

    // ---- The header's summary ----

    /// <summary>Under the condition's title: what it checks, in one line.</summary>
    public string Summary => Kind switch
    {
        ConditionKind.Component => Callee is { } callee
            ? Fill(_not ? Strings.Condition_ComponentSummaryNot : Strings.Condition_ComponentSummary, callee.Name)
            : ComponentName,
        ConditionKind.Pixel => MacroJson.IsColor(_color.Trim().ToUpperInvariant())
            ? Fill(_not ? Strings.Condition_PixelSummaryNot : Strings.Condition_PixelSummary, _x, _y, _color.Trim().ToUpperInvariant(), _tolerance)
            : Strings.Condition_PixelNotPicked,
        ConditionKind.Image => !HasImages
            ? Strings.Condition_ImageNotTaken
            : Fill(_not ? Strings.Condition_ImageSummaryNot : Strings.Condition_ImageSummary, NameOrKind, AreaName, _similarity),
        _ => string.IsNullOrWhiteSpace(_text)
            ? Strings.Condition_TextNotWritten
            : Fill((_textMatch == TextMatch.Exact, _not) switch
            {
                (true, false) => Strings.Condition_TextSummaryExact,
                (true, true) => Strings.Condition_TextSummaryExactNot,
                (false, false) => Strings.Condition_TextSummaryContains,
                _ => Strings.Condition_TextSummaryContainsNot,
            }, _text.Trim(), AreaName),
    };

    private string NameOrKind => string.IsNullOrWhiteSpace(_name) ? Title : _name.Trim();

    private string AreaName => _area switch
    {
        SearchArea.Around => Strings.Condition_AreaAroundShort,
        SearchArea.Screen => Strings.Condition_AreaScreenShort,
        _ => Strings.Condition_AreaDrawnShort,
    };

    // ---- The live check ----

    /// <summary>What the last check saw, or null before one.</summary>
    public string? Result
    {
        get => _result;
        private set { _result = value; Changed(nameof(Result), nameof(HasResult), nameof(NotChecked), nameof(NowText), nameof(ShowNow)); }
    }

    public bool HasResult => _result != null;

    /// <summary>
    /// The card shows once there is something to check, also before the first check (opening a condition does not check
    /// it: the user does with Check again, or by changing it).
    /// </summary>
    public bool ShowNow => HasResult || CanCheck;

    public bool NotChecked => !HasResult;

    /// <summary>What the last check saw, or that there was none yet.</summary>
    public string NowText => _result ?? Strings.Condition_NotChecked;

    /// <summary>The last check found the condition true.</summary>
    public bool ResultMet
    {
        get => _resultMet;
        private set { _resultMet = value; Changed(nameof(ResultMet), nameof(ResultNotMet)); }
    }

    public bool ResultNotMet => HasResult && !_resultMet;

    /// <summary>The properties that only show the last check or the expander's state, not what the condition checks.</summary>
    internal static readonly HashSet<string> ShownOnly = new()
    {
        nameof(Result), nameof(HasResult), nameof(ResultMet), nameof(ResultNotMet), nameof(IsExpanded),
        nameof(ShowNow), nameof(NotChecked), nameof(NowText),
    };

    /// <param name="not">A Component condition: the Not of what it checked in the end (its own and its component's); the condition's own otherwise.</param>
    internal void ShowReport(ConditionReport report, bool? not = null)
    {
        ResultMet = report.TrueFor(new MacroCondition { Not = not ?? _not });
        Result = report switch
        {
            { NoLanguage: true } => Fill(Strings.Condition_NoLanguage, TextReader.NativeName(_language ?? "")),
            { NoMonitor: true } => Strings.Condition_NoMonitor,
            { Met: false, Black: true } => Strings.Condition_Black,
            _ when IsComponent => ResultMet && report.At is { } place && !_not ? Fill(Strings.Condition_ComponentFoundNow, place.X, place.Y)
                : ResultMet ? Strings.Condition_ComponentTrueNow : Strings.Condition_ComponentNotNow,
            _ => Kind switch
            {
                ConditionKind.Pixel when report.Met => Fill(Strings.Condition_PixelMatches, report.Color ?? ""),
                ConditionKind.Pixel => Fill(Strings.Condition_PixelDiffers, report.Color ?? "", Difference(report.Color), _tolerance),
                ConditionKind.Image when report.Met && report.At is { } at => Fill(Strings.Condition_ImageFoundNow, at.X, at.Y, Percent(report.Score)),
                ConditionKind.Image => Fill(Strings.Condition_ImageNotNow, Percent(report.Score), _similarity),
                ConditionKind.Text when report.Met && report.At is { } at => Fill(Strings.Condition_TextFoundNow, at.X, at.Y),
                ConditionKind.Text when string.IsNullOrWhiteSpace(report.Read) => Fill(Strings.Condition_TextNotNowNothing, _text.Trim()),
                _ => Fill(Strings.Condition_TextNotNow, _text.Trim(), Shorten(report.Read ?? "")),
            },
        };
    }

    internal void ClearReport()
    {
        ResultMet = false;
        Result = null;
    }

    private static double Percent(double score) => ImageMatch.Percent(score);

    /// <summary>How far the color seen is from the condition's, in percent (the largest of red, green and blue).</summary>
    private double Difference(string? seen)
    {
        string mine = _color.Trim().ToUpperInvariant();
        if (!MacroJson.IsColor(seen) || !MacroJson.IsColor(mine))
            return 100;
        int a = int.Parse(seen!.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(mine.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int most = new[] { 16, 8, 0 }.Max(shift => Math.Abs(((a >> shift) & 0xFF) - ((b >> shift) & 0xFF)));
        return Math.Round(most * 100 / 255.0);
    }

    private static string Shorten(string text) => TextCut.Shorten(text, 60);

    // ---- Back to the model ----

    /// <summary>It has what it looks for (the name does not matter for a check).</summary>
    public bool CanCheck => Kind switch
    {
        ConditionKind.Component => Callee is { Returns: not null } && _arguments.All(a => a.Problem == null),
        ConditionKind.Pixel => MacroJson.IsColor(_color.Trim().ToUpperInvariant()),
        ConditionKind.Image => HasImages && (_area == SearchArea.Around || _areaWidth >= MacroJson.AreaSize.Min),
        _ => !string.IsNullOrWhiteSpace(_text) && _areaWidth >= MacroJson.AreaSize.Min,
    };

    /// <summary>Why the condition cannot be saved as it is, or null.</summary>
    public string? Problem => Kind switch
    {
        _ when Finds && string.IsNullOrWhiteSpace(_name) => Strings.Code_EnterConditionName,
        ConditionKind.Component when _componentId == null => Strings.Code_ChooseComponent,
        ConditionKind.Component when Callee == null => Strings.Code_ComponentMissing,
        ConditionKind.Component when Callee!.Returns == null => Fill(Strings.Code_ComponentNoReturns, Callee.Name),
        ConditionKind.Component => _arguments.Select(a => a.Problem).FirstOrDefault(p => p != null),
        // A field a parameter gives takes its Example when the component is saved: it needs no value here.
        ConditionKind.Pixel when !Slots.Color.IsBound && !MacroJson.IsColor(_color.Trim().ToUpperInvariant()) => Strings.Code_ConditionColor,
        ConditionKind.Image when !Slots.Images.IsBound && !HasImages => Strings.Code_TakeImage,
        ConditionKind.Image when !Slots.Images.IsBound && Images.Any(i => i.Preview == null) => Strings.Code_ImageMissing,
        ConditionKind.Text when !Slots.Text.IsBound && string.IsNullOrWhiteSpace(_text) => Strings.Code_EnterConditionText,
        _ when ShowsArea && !Slots.Area.IsBound && _area != SearchArea.Around && (_areaWidth < MacroJson.AreaSize.Min || _areaHeight < MacroJson.AreaSize.Min) => Strings.Condition_DrawArea,
        _ => null,
    };

    internal MacroCondition ToModel()
    {
        var c = _condition.Clone();
        c.Name = Finds ? _name.Trim() : null;
        c.Not = _not;
        c.X = c.Y = 0;
        c.Color = null;
        c.Tolerance = 0;
        c.Images = null;
        c.NearbySizes = false;
        c.Similarity = 0;
        c.Area = SearchArea.Around;
        c.AreaX = c.AreaY = c.AreaWidth = c.AreaHeight = 0;
        c.Text = null;
        c.TextMatch = TextMatch.Contains;
        c.MatchCase = false;
        c.Language = null;
        c.ComponentId = null;
        c.Arguments = null;
        c.Cell = ShowCell ? (int)Math.Max(1, Math.Round(double.IsNaN(_cell) ? 1 : _cell)) : null;
        switch (Kind)
        {
            case ConditionKind.Component:
                c.ComponentId = _componentId;
                c.Arguments = ArgumentViewModel.ToArguments(_arguments);
                break;
            case ConditionKind.Pixel:
                c.X = (int)Math.Round(double.IsNaN(_x) ? 0 : _x);
                c.Y = (int)Math.Round(double.IsNaN(_y) ? 0 : _y);
                c.Color = _color.Trim().ToUpperInvariant();
                c.Tolerance = (int)_tolerance;
                break;
            case ConditionKind.Image:
                c.Images = Images.Select(i => i.Image.Clone()).ToList();
                c.NearbySizes = _nearbySizes;
                c.Similarity = (int)_similarity;
                SetAreaOn(c);
                break;
            case ConditionKind.Text:
                c.Text = _text.Trim();
                c.TextMatch = _textMatch;
                c.MatchCase = _matchCase;
                c.Language = _language;
                SetAreaOn(c);
                break;
        }
        c.Bind = Slots.ToBind(ComponentSlots.Of(c));
        return c;
    }

    private void SetAreaOn(MacroCondition c)
    {
        c.Area = _area;
        if (_area == SearchArea.Around)
            return;
        (c.AreaX, c.AreaY, c.AreaWidth, c.AreaHeight) = (_areaX, _areaY, _areaWidth, _areaHeight);
    }
}

/// <summary>An If's conditions and when its Then runs, as the condition editor shows them.</summary>
public sealed class IfViewModel : ConditionEditorItem
{
    private int _matchIndex, _waitIndex, _onFailIndex;
    private double _waitSeconds;

    internal IfViewModel(MacroIf block, ImageStore? store, ConditionContext? context = null, Dictionary<string, string>? actionBind = null)
    {
        Store = store;
        Context = context ?? ConditionContext.None;
        Slots = new SlotBindings(new[] { new ComponentSlot(ComponentSlots.WaitSeconds, ParameterType.Number, NumberUnit.Seconds, ValueRule.Any, true) },
            Context.Parameters, actionBind);
        Slots.WaitSeconds.PropertyChanged += (_, _) => Changed(nameof(ShowSecondsValue), nameof(Problem));
        foreach (var condition in block.Conditions)
            Add(new ConditionViewModel(condition, store, Context) { IsExpanded = false });
        foreach (var action in MacroDefinition.Walk(block.Then))
            if (action.FoundId is Guid used)
                UsedByThen.Add(used);
        _matchIndex = (int)block.Match;
        _waitIndex = (int)block.Wait;
        _waitSeconds = block.WaitSeconds > 0 ? block.WaitSeconds : 5;
        _onFailIndex = (int)block.OnFail;
        Conditions.CollectionChanged += (_, _) => Changed(nameof(ShowMatch), nameof(OnFailHeader), nameof(HasConditions), nameof(HasNoConditions), nameof(HasText));
        if (Conditions.Count == 1)
            Conditions[0].IsExpanded = true;
    }

    internal ImageStore? Store { get; }

    /// <summary>The Library, the parameters and the places around the If, for its conditions.</summary>
    internal ConditionContext Context { get; }

    /// <summary>Inside a component: the If's own field that can take a parameter's value, WaitSeconds.</summary>
    public SlotBindings Slots { get; }

    /// <summary>Up to with its own number of seconds (not a parameter's).</summary>
    public bool ShowSecondsValue => ShowSeconds && Slots.WaitSeconds.ShowValue;

    /// <summary>Up to inside a component with a parameter in seconds: the list shows.</summary>
    public bool ShowSecondsChoice => ShowSeconds && Slots.WaitSeconds.Show;

    /// <summary>The conditions an action in this If's Then goes to ("Where … was found"): they can't be removed.</summary>
    internal HashSet<Guid> UsedByThen { get; } = new();

    /// <summary>The names of the macro's other conditions, so a new one gets a name of its own.</summary>
    internal IReadOnlyCollection<string> OtherNames { get; set; } = Array.Empty<string>();

    public ObservableCollection<ConditionViewModel> Conditions { get; } = new();

    public bool HasConditions => Conditions.Count > 0;
    public bool HasNoConditions => Conditions.Count == 0;

    /// <summary>Raised when anything of a condition changes (the window checks it again).</summary>
    public event Action<ConditionViewModel>? ConditionChanged;

    internal void Add(ConditionViewModel condition)
    {
        condition.PropertyChanged += (_, e) =>
        {
            // What the check showed, or opening the condition, is no change of the condition (it would check again, forever).
            if (!ConditionViewModel.ShownOnly.Contains(e.PropertyName ?? ""))
                ConditionChanged?.Invoke(condition);
        };
        Conditions.Add(condition);
    }

    /// <summary>A Text condition is there: the window says text recognition is slower.</summary>
    public bool HasText => Conditions.Any(c => c.IsText);

    /// <summary>All / Any: shown with more than one condition.</summary>
    public bool ShowMatch => Conditions.Count > 1;

    public int MatchIndex
    {
        get => _matchIndex;
        set { _matchIndex = value; Changed(); }
    }

    /// <summary>0 Check once, 1 Up to, 2 As long as it takes.</summary>
    public int WaitIndex
    {
        get => _waitIndex;
        set { _waitIndex = value; Changed(nameof(WaitIndex), nameof(ShowSeconds), nameof(ShowSecondsValue), nameof(ShowSecondsChoice), nameof(OnFailDescription)); }
    }

    public bool ShowSeconds => _waitIndex == (int)ConditionWait.UpTo;

    public double WaitSeconds
    {
        get => _waitSeconds;
        set { _waitSeconds = value; Changed(nameof(WaitSeconds), nameof(OnFailDescription)); }
    }

    /// <summary>0 Run the Else actions, 1 Stop the macro.</summary>
    public int OnFailIndex
    {
        get => _onFailIndex;
        set { _onFailIndex = value; Changed(); }
    }

    public string OnFailHeader => Strings.Condition_IfNotTrue;

    public string OnFailDescription => (ConditionWait)_waitIndex switch
    {
        ConditionWait.Once => Strings.Condition_IfNotTrueOnce,
        // Whole seconds in words ("30 seconds"); tenths with the unit's symbol, which needs no plural form in any language.
        ConditionWait.UpTo => double.IsNaN(_waitSeconds) || _waitSeconds == Math.Floor(_waitSeconds)
            ? Plurals.Format("Condition_IfNotTrueAfter", (long)(double.IsNaN(_waitSeconds) ? 0 : _waitSeconds))
            : string.Format(CultureInfo.CurrentCulture, Strings.Condition_IfNotTrueAfterSeconds, _waitSeconds.ToString("0.#", CultureInfo.CurrentCulture)),
        _ => Strings.Condition_IfNotTrueForever,
    };

    /// <summary>Why the If cannot be saved as it is, or null.</summary>
    public string? Problem
    {
        get
        {
            if (Conditions.Count == 0)
                return Strings.Code_AddCondition;
            if (Conditions.Select(c => c.Problem).FirstOrDefault(p => p != null) is { } problem)
                return problem;
            var names = Conditions.Where(c => c.Finds).Select(c => c.Name.Trim()).ToList();
            if (names.Count != names.Distinct(StringComparer.CurrentCultureIgnoreCase).Count())
                return Strings.Code_ConditionNameTwice;
            if (ShowSecondsValue && (double.IsNaN(_waitSeconds) || _waitSeconds < MacroJson.WaitSeconds.Min || _waitSeconds > MacroJson.WaitSeconds.Max))
                return Strings.Condition_WaitSeconds;
            return null;
        }
    }

    /// <summary>The If action's Bind: WaitSeconds from a parameter (with Up to), or null.</summary>
    internal Dictionary<string, string>? WaitBind() =>
        Slots.ToBind(new[] { new ComponentSlot(ComponentSlots.WaitSeconds, ParameterType.Number, NumberUnit.Seconds, ValueRule.Any, ShowSeconds) });

    /// <summary><paramref name="block"/>'s Then and Else with the editor's conditions and choices.</summary>
    internal MacroIf ToModel(MacroIf block)
    {
        var result = block.Clone();
        result.Conditions = Conditions.Select(c => c.ToModel()).ToList();
        result.Match = (ConditionMatch)Math.Clamp(_matchIndex, 0, 1);
        result.Wait = (ConditionWait)Math.Clamp(_waitIndex, 0, 2);
        result.WaitSeconds = result.Wait == ConditionWait.UpTo ? Math.Round(_waitSeconds, 1) : 0;
        result.OnFail = (ConditionFail)Math.Clamp(_onFailIndex, 0, 1);
        return result;
    }
}
