using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// Full-screen overlay to choose where the game mode panel appears.
/// Covers every monitor; Enter saves, Esc cancels, arrows nudge.
/// </summary>
public partial class PlacementWindow
{
    private Point _dragStart;
    private Point _ghostStart;
    private bool _dragging;

    /// <summary>Chosen position (screen coordinates in WPF units) when the dialog result is true.</summary>
    public Point Result { get; private set; }

    internal PlacementWindow(IList loops, IList macros, Point start, int opacity,
        string exitHotkey, string stopAllHotkey)
    {
        InitializeComponent();
        // The real panel, exactly as in game mode (same items, opacity and hotkeys).
        GhostPanel.Bind(loops, macros);
        GhostPanel.SetBackgroundOpacity(opacity);
        GhostPanel.SetHotkeyLabels(exitHotkey, stopAllHotkey);

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        MoveGhostTo(start);
        Loaded += (_, _) =>
        {
            // Instructions at the top center of the main screen.
            Instructions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Rect work = SystemParameters.WorkArea;
            Canvas.SetLeft(Instructions, work.Left - Left + (work.Width - Instructions.DesiredSize.Width) / 2);
            Canvas.SetTop(Instructions, work.Top - Top + 18);
            Activate();
            Focus();
        };
    }

    private void MoveGhostTo(Point screen)
    {
        double x = Math.Round(screen.X);
        double y = Math.Round(screen.Y);
        Canvas.SetLeft(Ghost, x - Left);
        Canvas.SetTop(Ghost, y - Top);
        CoordinatesText.Text = $"X {x}   Y {y}";
    }

    private Point GhostScreenPosition() =>
        new(Canvas.GetLeft(Ghost) + Left, Canvas.GetTop(Ghost) + Top);

    private void OnGhostMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(Surface);
        _ghostStart = GhostScreenPosition();
        Ghost.CaptureMouse();
        e.Handled = true;
    }

    private void OnGhostMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        Point now = e.GetPosition(Surface);
        MoveGhostTo(new Point(_ghostStart.X + now.X - _dragStart.X, _ghostStart.Y + now.Y - _dragStart.Y));
    }

    private void OnGhostMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Ghost.ReleaseMouseCapture();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Point p = GhostScreenPosition();
        switch (e.Key)
        {
            case Key.Enter:
                Result = p;
                DialogResult = true;
                break;
            case Key.Escape:
                DialogResult = false;
                break;
            case Key.Left: MoveGhostTo(new Point(p.X - step, p.Y)); break;
            case Key.Right: MoveGhostTo(new Point(p.X + step, p.Y)); break;
            case Key.Up: MoveGhostTo(new Point(p.X, p.Y - step)); break;
            case Key.Down: MoveGhostTo(new Point(p.X, p.Y + step)); break;
            default: return;
        }
        e.Handled = true;
    }
}
