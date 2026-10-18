using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PuppyMacro.Resources;
using PuppyMacro.Services.Screen;
using Mat = OpenCvSharp.Mat;
using CvRect = OpenCvSharp.Rect;

namespace PuppyMacro;

/// <summary>What <see cref="ScreenPickWindow"/> asks the user for, or (<see cref="Cells"/>) only shows.</summary>
internal enum ScreenPickMode { Pixel, Image, Area, Cells }

/// <summary>
/// Full-screen pick over a still picture of the screen as the macros' conditions see it: a pixel and its color, an image
/// to look for, or a search area. Results are in desktop coordinates, physical pixels. <see cref="ShowCells"/> shows a
/// grid's cells there instead, each point with its number, until a click or Esc.
/// </summary>
internal partial class ScreenPickWindow
{
    /// <summary>The smallest image or area, on each side (a condition's image needs some detail).</summary>
    public const int MinSize = 8;

    private const int LensPixels = 11;

    private readonly ScreenPickMode _mode;
    private readonly ScreenSnapshot _snapshot;
    private readonly List<(CvRect Bounds, BitmapSource Picture)> _pictures = new();
    private Point? _dragFrom;
    private CvRect? _drawn;

    private ScreenPickWindow(ScreenPickMode mode, ScreenSnapshot snapshot, string? askedBy, string? reason)
    {
        InitializeComponent();
        _mode = mode;
        _snapshot = snapshot;
        (Title, InstructionsText.Text) = mode switch
        {
            ScreenPickMode.Image => (Strings.ScreenPick_ImageTitle, Strings.ScreenPick_Image),
            ScreenPickMode.Area => (Strings.ScreenPick_AreaTitle, Strings.ScreenPick_Area),
            ScreenPickMode.Cells => (Strings.ScreenPick_CellsTitle, Strings.ScreenPick_Cells),
            _ => (Strings.ScreenPick_PixelTitle, Strings.ScreenPick_Pixel),
        };
        if (mode == ScreenPickMode.Cells)
        {
            Cursor = Cursors.Arrow;
            EscText.Text = Strings.Editor_Close;
        }
        if (askedBy != null)
            InstructionsText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Pick_AskedBy, askedBy, reason);
        Closed += (_, _) => _snapshot.Dispose();
        UseButton.Content = mode == ScreenPickMode.Image ? Strings.ScreenPick_TakeImage : Strings.ScreenPick_UseArea;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) =>
        {
            ShowPictures();
            ShowCellMarks();
            PlaceInstructions();
            Activate();
            Focus();
            UpdateLens();
        };
    }

    /// <summary>Pixel: the point and its color ("#RRGGBB").</summary>
    public (int X, int Y, string Color)? PickedPixel { get; private set; }

    /// <summary>Image: where it was taken and the image as PNG. Area: where it is (PNG null).</summary>
    public (CvRect Area, byte[]? Png)? PickedArea { get; private set; }

    /// <summary>
    /// Takes the screen's picture, then asks. <paramref name="owner"/> moves off the screen meanwhile, so neither the
    /// picture nor the pick shows it. Null when the user cancels or the screen cannot be seen (<paramref name="error"/>).
    /// </summary>
    public static ScreenPickWindow? Ask(Window owner, ScreenPickMode mode, out string? error) => Ask(owner, mode, null, out error);

    /// <summary>
    /// Shows a grid's cells (<paramref name="points"/>, cell 1 first) over a still picture of the screen, each with its
    /// number, until a click or Esc: whether they are where the grid's cells are. False, with why, when the screen can't be seen.
    /// </summary>
    public static bool ShowCells(Window owner, IReadOnlyList<(int X, int Y)> points, out string? error)
    {
        Ask(owner, ScreenPickMode.Cells, points, out error);
        return error == null;
    }

    private static ScreenPickWindow? Ask(Window owner, ScreenPickMode mode, IReadOnlyList<(int X, int Y)>? cells, out string? error)
    {
        error = null;
        // The window and the windows under it (the macro editor) leave the screen.
        var moved = new List<(Window Window, double Left)>();
        for (Window? w = owner; w != null; w = w.Owner)
        {
            if (!w.IsVisible)
                continue;
            moved.Add((w, w.Left));
            w.Left = SystemParameters.VirtualScreenLeft - w.ActualWidth - 200;
        }
        try
        {
            // Let the window move before the picture is taken.
            owner.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var window = Create(mode, out error);
            if (window != null)
                window._cells = cells;
            return window?.ShowDialog() == true ? window : null;
        }
        finally
        {
            foreach (var (window, place) in moved)
                window.Left = place;
            owner.Activate();
        }
    }

    /// <summary>
    /// The window over a picture of the screen taken now, not shown yet (an AI app's request shows it its own way); null when
    /// the screen cannot be seen (<paramref name="error"/>). <paramref name="askedBy"/>: the AI app that asks, with its reason.
    /// </summary>
    public static ScreenPickWindow? Create(ScreenPickMode mode, out string? error, string? askedBy = null, string? reason = null)
    {
        error = null;
        var snapshot = ScreenSnapshot.Take();
        if (snapshot.Monitors.Count > 0)
            return new ScreenPickWindow(mode, snapshot, askedBy, reason);
        snapshot.Dispose();
        error = Strings.ScreenPick_NoScreen;
        return null;
    }

    /// <summary>The instructions centered at the top of the work area, after their text changes too.</summary>
    private void PlaceInstructions()
    {
        Instructions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Rect work = SystemParameters.WorkArea;
        Canvas.SetLeft(Instructions, work.Left - Left + (work.Width - Instructions.DesiredSize.Width) / 2);
        Canvas.SetTop(Instructions, work.Top - Top + 18);
    }

    /// <summary>Each monitor's picture where the monitor is.</summary>
    private void ShowPictures()
    {
        int at = 0;
        foreach (var (bounds, picture) in _snapshot.Monitors)
        {
            var source = ToBitmap(picture);
            _pictures.Add((bounds, source));
            Point topLeft = PointFromScreen(new Point(bounds.X, bounds.Y));
            Point bottomRight = PointFromScreen(new Point(bounds.X + bounds.Width, bounds.Y + bounds.Height));
            var image = new Image { Source = source, Stretch = Stretch.Fill, Width = bottomRight.X - topLeft.X, Height = bottomRight.Y - topLeft.Y };
            Canvas.SetLeft(image, topLeft.X);
            Canvas.SetTop(image, topLeft.Y);
            Surface.Children.Insert(at++, image);
        }
    }

    /// <summary>The grid's cells to show (<see cref="ScreenPickMode.Cells"/>), physical pixels; null otherwise.</summary>
    private IReadOnlyList<(int X, int Y)>? _cells;

    /// <summary>Each cell's point, a ring where the pixel is, with its number beside it.</summary>
    private void ShowCellMarks()
    {
        if (_mode != ScreenPickMode.Cells || _cells == null)
            return;
        var accent = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        accent.Freeze();
        int at = Surface.Children.IndexOf(Instructions);
        for (int i = 0; i < _cells.Count; i++)
        {
            Point center = PointFromScreen(new Point(_cells[i].X + 0.5, _cells[i].Y + 0.5));
            var ring = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Stroke = accent, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(ring, center.X - 6);
            Canvas.SetTop(ring, center.Y - 6);
            var number = new Border
            {
                Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4), Background = accent, IsHitTestVisible = false,
                Child = new TextBlock { FontSize = 11, Foreground = Brushes.Black, Text = (i + 1).ToString(CultureInfo.CurrentCulture) },
            };
            Canvas.SetLeft(number, center.X + 8);
            Canvas.SetTop(number, center.Y - 18);
            Surface.Children.Insert(at++, ring);
            Surface.Children.Insert(at++, number);
        }
    }

    private static BitmapSource ToBitmap(Mat bgr)
    {
        var bitmap = BitmapSource.Create(bgr.Width, bgr.Height, 96, 96, PixelFormats.Bgr24, null,
            bgr.Data, (int)(bgr.Step() * bgr.Height), (int)bgr.Step());
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The screen pixel under a point of this window.</summary>
    private (int X, int Y) ScreenPixel(Point at)
    {
        Point screen = PointToScreen(at);
        return ((int)Math.Floor(screen.X), (int)Math.Floor(screen.Y));
    }

    // ================= Pixel =================

    private void UpdateLens()
    {
        if (_mode != ScreenPickMode.Pixel || !IsLoaded)
            return;
        Point at = Mouse.GetPosition(this);
        var (x, y) = ScreenPixel(at);
        if (_snapshot.Pixel(x, y) is not { } color)
        {
            Lens.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var (bounds, picture) in _pictures)
        {
            if (!bounds.Contains(x, y))
                continue;
            int half = LensPixels / 2;
            int left = Math.Clamp(x - bounds.X - half, 0, bounds.Width - LensPixels);
            int top = Math.Clamp(y - bounds.Y - half, 0, bounds.Height - LensPixels);
            LensImage.Source = new CroppedBitmap(picture, new Int32Rect(left, top, LensPixels, LensPixels));
        }
        LensSwatch.Background = new SolidColorBrush(Color.FromRgb(color.Item2, color.Item1, color.Item0));
        LensText.Text = string.Format(CultureInfo.CurrentCulture, Strings.ScreenPick_PixelInfo, ScreenSnapshot.Hex(color), x, y);
        Lens.Visibility = Visibility.Visible;
        Lens.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = Lens.DesiredSize;
        const double gap = 24;
        Canvas.SetLeft(Lens, at.X + gap + size.Width <= ActualWidth ? at.X + gap : at.X - gap - size.Width);
        Canvas.SetTop(Lens, at.Y + gap + size.Height <= ActualHeight ? at.Y + gap : at.Y - gap - size.Height);
    }

    // ================= Image and area =================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode == ScreenPickMode.Cells)
        {
            DialogResult = false;
            return;
        }
        if (Instructions.IsMouseOver || SelectionButtons.IsMouseOver)
            return;
        Point at = e.GetPosition(this);
        if (_mode == ScreenPickMode.Pixel)
        {
            var (x, y) = ScreenPixel(at);
            if (_snapshot.Pixel(x, y) is { } color)
            {
                PickedPixel = (x, y, ScreenSnapshot.Hex(color));
                DialogResult = true;
            }
            return;
        }
        _dragFrom = at;
        _drawn = null;
        SelectionButtons.Visibility = Visibility.Collapsed;
        Surface.CaptureMouse();
        DrawSelection(at, at);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_mode == ScreenPickMode.Pixel)
            UpdateLens();
        else if (_dragFrom is { } from)
            DrawSelection(from, e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        _dragFrom = null;
        Surface.ReleaseMouseCapture();
        var area = DrawSelection(from, e.GetPosition(this));
        if (area.Width < MinSize || area.Height < MinSize)
        {
            Selection.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _drawn = area;
        // The buttons under the rectangle, or above it near the screen's bottom.
        SelectionButtons.Visibility = Visibility.Visible;
        SelectionButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double right = Canvas.GetLeft(Selection) + Selection.Width;
        double bottom = Canvas.GetTop(Selection) + Selection.Height;
        Size size = SelectionButtons.DesiredSize;
        Canvas.SetLeft(SelectionButtons, Math.Max(0, right - size.Width));
        Canvas.SetTop(SelectionButtons, bottom + 8 + size.Height <= ActualHeight ? bottom + 8 : Canvas.GetTop(Selection) - 8 - size.Height);
    }

    /// <summary>Draws the rectangle between two points of this window and gives it in screen pixels.</summary>
    private CvRect DrawSelection(Point a, Point b)
    {
        var (x1, y1) = ScreenPixel(a);
        var (x2, y2) = ScreenPixel(b);
        var area = new CvRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
        Point topLeft = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        Canvas.SetLeft(Selection, topLeft.X);
        Canvas.SetTop(Selection, topLeft.Y);
        Selection.Width = Math.Abs(b.X - a.X);
        Selection.Height = Math.Abs(b.Y - a.Y);
        Selection.Visibility = Visibility.Visible;
        SizeText.Text = string.Format(CultureInfo.CurrentCulture, Strings.ScreenPick_Size, area.Width, area.Height);
        SizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeLabel, topLeft.X);
        Canvas.SetTop(SizeLabel, Math.Max(0, topLeft.Y - 26));
        return area;
    }

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (_drawn is not { } area)
            return;
        if (_mode == ScreenPickMode.Area)
        {
            PickedArea = (area, null);
            DialogResult = true;
            return;
        }
        if (_snapshot.Part(area) is not { } part)
            return;
        using (part.Picture)
        {
            // An image of one color has no shape to find, and the search never finds it: refused here, as the AI tools
            // refuse it, and the user draws again on the same picture.
            if (Flatness.IsFlat(part.Picture))
            {
                OnRetryClick(this, e);
                InstructionsText.Text = Strings.ScreenPick_OneColor;
                PlaceInstructions();
                return;
            }
            OpenCvSharp.Cv2.ImEncode(".png", part.Picture, out byte[] png);
            PickedArea = (part.Area, png);
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Retry: the rectangle goes, the same picture stays, and the user drags again.</summary>
    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _drawn = null;
        Selection.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        SelectionButtons.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _drawn != null)
        {
            OnUseClick(this, e);
            e.Handled = true;
        }
    }
}
