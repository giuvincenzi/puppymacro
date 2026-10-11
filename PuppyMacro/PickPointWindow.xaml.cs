using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuppyMacro.Native;
using PuppyMacro.Services;

namespace PuppyMacro;

/// <summary>
/// Full-screen overlay: the user clicks a point (the result is in physical screen pixels) or, with
/// <c>pickApp</c>, a window of another app (the result is its .exe name; the window under the cursor is outlined).
/// </summary>
public partial class PickPointWindow
{
    private readonly bool _pickApp;
    private IntPtr _hoveredWindow;
    private string _hoveredApp = "";

    private readonly int _count;
    private readonly string _instructions;
    private readonly IReadOnlyList<string>? _labels;
    private readonly List<(int X, int Y)> _points = new();

    /// <param name="askedBy">An AI app that asks for the points or app (pick_point(s), pick_app), shown with its <paramref name="reason"/>.</param>
    /// <param name="count">Points to click in a row (pick_points): "2 / 3" before the text, each one numbered where it was clicked.</param>
    /// <param name="labels">What each point is (an AI app's pick, one per point): shown next to the cursor, the current one.</param>
    public PickPointWindow(bool pickApp = false, string? askedBy = null, string? reason = null, int count = 1,
        IReadOnlyList<string>? labels = null)
    {
        InitializeComponent();
        _pickApp = pickApp;
        _count = Math.Max(1, labels?.Count ?? count);
        _labels = labels is { Count: > 0 } ? labels : null;
        if (pickApp)
        {
            Title = "Pick an app";
            InstructionsText.Text = "Click a window of the app";
        }
        if (askedBy != null)
            InstructionsText.Text = $"{askedBy}: {reason}";
        _instructions = InstructionsText.Text;
        UpdateCounter();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) =>
        {
            Instructions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Rect work = SystemParameters.WorkArea;
            Canvas.SetLeft(Instructions, work.Left - Left + (work.Width - Instructions.DesiredSize.Width) / 2);
            Canvas.SetTop(Instructions, work.Top - Top + 18);
            Activate();
            Focus();
            UpdateHovered();
            UpdatePointLabel();
        };
    }

    public int ResultX { get; private set; }
    public int ResultY { get; private set; }

    /// <summary>The points clicked, in order (physical pixels): one, or the count asked.</summary>
    public IReadOnlyList<(int X, int Y)> Points => _points;

    private void UpdateCounter()
    {
        if (_count > 1)
            InstructionsText.Text = $"{Math.Min(_points.Count + 1, _count)} / {_count}   {_instructions}";
        if (_labels != null)
            PointLabelText.Text = _labels[Math.Min(_points.Count, _labels.Count - 1)];
    }

    /// <summary>The current point's label just below and right of the cursor, on the other side near the screen's edges.</summary>
    private void UpdatePointLabel()
    {
        if (_labels == null || !IsLoaded)
            return;
        PointLabel.Visibility = Visibility.Visible;
        NativeMethods.GetCursorPos(out var cursor);
        Point at = PointFromScreen(new Point(cursor.X, cursor.Y));
        PointLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = PointLabel.DesiredSize;
        const double gap = 20;
        double x = at.X + gap + size.Width <= ActualWidth ? at.X + gap : at.X - gap - size.Width;
        double y = at.Y + gap + size.Height <= ActualHeight ? at.Y + gap : at.Y - gap - size.Height;
        Canvas.SetLeft(PointLabel, Math.Max(0, x));
        Canvas.SetTop(PointLabel, Math.Max(0, y));
    }

    /// <summary>A number where a point was clicked, so the user sees which ones are done.</summary>
    private void AddMarker(int x, int y, int number)
    {
        Point at = PointFromScreen(new Point(x, y));
        var marker = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF)),
            BorderBrush = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(marker, at.X - 12);
        Canvas.SetTop(marker, at.Y - 12);
        ((Canvas)Content).Children.Add(marker);
    }

    /// <summary>With <c>pickApp</c>: the .exe name of the app whose window was clicked.</summary>
    public string? ResultApp { get; private set; }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Instructions.IsMouseOver)
            return;
        // Where this click was (screen pixels), not where the pointer is now: with quick clicks the pointer is already
        // at the next point when this one is handled.
        Point screen = PointToScreen(e.GetPosition(this));
        var point = new NativeMethods.POINT { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        if (_pickApp)
        {
            UpdateHovered();
            if (_hoveredApp.Length == 0)
                return; // no app window there, or its name cannot be read: keep waiting
            ResultApp = _hoveredApp;
            DialogResult = true;
            return;
        }
        ResultX = point.X;
        ResultY = point.Y;
        _points.Add((point.X, point.Y));
        if (_points.Count < _count)
        {
            AddMarker(point.X, point.Y, _points.Count);
            UpdateCounter();
            return;
        }
        DialogResult = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pickApp)
            UpdateHovered();
        UpdatePointLabel();
    }

    /// <summary>Outlines the app window under the cursor (this overlay and PuppyMacro's windows are skipped) and shows its .exe name.</summary>
    private void UpdateHovered()
    {
        if (!_pickApp || !IsLoaded)
            return;
        NativeMethods.GetCursorPos(out var point);
        AppWindow? found = AppWindows.At(point.X, point.Y);
        if (found is not { } window)
        {
            _hoveredWindow = IntPtr.Zero;
            _hoveredApp = "";
            Highlight.Visibility = Visibility.Collapsed;
            AppLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (window.Handle != _hoveredWindow)
        {
            _hoveredWindow = window.Handle;
            _hoveredApp = AppWindows.ExeName(window.ProcessId);
            AppLabelText.Text = _hoveredApp.Length > 0 ? _hoveredApp : "Can't read this app's name";
        }

        // Physical pixels to this window's units.
        Point topLeft = PointFromScreen(new Point(window.Bounds.Left, window.Bounds.Top));
        Point bottomRight = PointFromScreen(new Point(window.Bounds.Right, window.Bounds.Bottom));
        Canvas.SetLeft(Highlight, topLeft.X);
        Canvas.SetTop(Highlight, topLeft.Y);
        Highlight.Width = Math.Max(0, bottomRight.X - topLeft.X);
        Highlight.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
        Canvas.SetLeft(AppLabel, topLeft.X + 8);
        Canvas.SetTop(AppLabel, topLeft.Y + 8);
        Highlight.Visibility = Visibility.Visible;
        AppLabel.Visibility = Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
