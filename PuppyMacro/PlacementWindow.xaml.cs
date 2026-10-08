using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PuppyMacro.Models;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>A floating button shown in the placement overlay.</summary>
internal sealed record PlacementButton(Guid Id, string Name, string Label, string HotkeyText, FloatingButtonSize Size, Point Position);

/// <summary>
/// Full-screen overlay to choose where the game mode panel and the floating buttons appear.
/// Covers every monitor; each element is dragged on its own, the last one clicked is selected
/// and moves with the arrows. Enter saves, Esc cancels.
/// </summary>
public partial class PlacementWindow
{
    /// <summary>A draggable element: the panel or one floating button.</summary>
    private sealed class Draggable
    {
        public required FrameworkElement Element { get; init; }
        public required Shape Outline { get; init; }
        public required string Name { get; init; }

        /// <summary>Button id; null for the panel.</summary>
        public Guid? Id { get; init; }

        /// <summary>From the element's top-left to the saved position (the circle of a button).</summary>
        public double Offset { get; init; }
    }

    private static readonly DoubleCollection Dashes = new() { 4, 3 };

    private readonly List<Draggable> _ghosts = new();
    private Draggable _selected;
    private Point _dragStart;
    private Point _ghostStart;
    private bool _dragging;

    /// <summary>Chosen panel position (screen coordinates in WPF units) when the dialog result is true.</summary>
    public Point Result { get; private set; }

    /// <summary>Chosen floating button positions (top-left of the circle), by item id.</summary>
    internal Dictionary<Guid, Point> ButtonResults { get; } = new();

    internal PlacementWindow(IList loops, IList macros, Point start, int opacity,
        string exitHotkey, string stopAllHotkey, IReadOnlyList<PlacementButton> buttons, Guid? select = null)
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

        _selected = AddGhost(new Draggable { Element = Ghost, Outline = GhostOutline, Name = "Game mode panel" });
        MoveTo(_selected, start);

        foreach (PlacementButton button in buttons)
        {
            var view = new FloatingButtonView();
            view.Show(button.Label, button.HotkeyText, button.Size);
            view.SetBackgroundOpacity(opacity);
            double diameter = FloatingButton.DiameterOf(button.Size);
            var outline = new Ellipse
            {
                Width = diameter + 4,
                Height = diameter + 4,
                Margin = new Thickness(FloatingButtonView.Inset - 2, FloatingButtonView.Inset - 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0xA2)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            var element = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            element.Children.Add(view);
            element.Children.Add(outline);
            Surface.Children.Insert(Surface.Children.IndexOf(SelectionTag), element);

            Draggable ghost = AddGhost(new Draggable
            {
                Element = element,
                Outline = outline,
                Name = button.Name,
                Id = button.Id,
                Offset = FloatingButtonView.Inset,
            });
            MoveTo(ghost, button.Position);
            if (button.Id == select)
                _selected = ghost;
        }

        if (buttons.Count > 0)
        {
            InstructionsText.Text = "Drag the panel and the buttons where you want them";
            NudgeText.Text = "Nudge the selected one (Shift ×10)";
        }
        Select(_selected);

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

    private Draggable AddGhost(Draggable ghost)
    {
        ghost.Element.MouseLeftButtonDown += OnGhostMouseDown;
        ghost.Element.MouseMove += OnGhostMouseMove;
        ghost.Element.MouseLeftButtonUp += OnGhostMouseUp;
        _ghosts.Add(ghost);
        return ghost;
    }

    /// <summary>The selected element has a solid outline and the name tag.</summary>
    private void Select(Draggable ghost)
    {
        _selected = ghost;
        foreach (Draggable g in _ghosts)
            g.Outline.StrokeDashArray = g == ghost ? new DoubleCollection() : Dashes;
        UpdateSelectionTag();
    }

    private void UpdateSelectionTag()
    {
        Point p = PositionOf(_selected);
        SelectionText.Text = $"{_selected.Name}   X {p.X}   Y {p.Y}";
        Canvas.SetLeft(SelectionTag, Canvas.GetLeft(_selected.Element));
        Canvas.SetTop(SelectionTag, Canvas.GetTop(_selected.Element) - 28);
    }

    private void MoveTo(Draggable ghost, Point screen)
    {
        double x = Math.Round(screen.X);
        double y = Math.Round(screen.Y);
        Canvas.SetLeft(ghost.Element, x - ghost.Offset - Left);
        Canvas.SetTop(ghost.Element, y - ghost.Offset - Top);
        if (ghost == _selected)
            UpdateSelectionTag();
    }

    private Point PositionOf(Draggable ghost) =>
        new(Canvas.GetLeft(ghost.Element) + ghost.Offset + Left, Canvas.GetTop(ghost.Element) + ghost.Offset + Top);

    private Draggable? GhostOf(object sender) => _ghosts.Find(g => g.Element == sender);

    private void OnGhostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GhostOf(sender) is not Draggable ghost)
            return;
        Select(ghost);
        _dragging = true;
        _dragStart = e.GetPosition(Surface);
        _ghostStart = PositionOf(ghost);
        ghost.Element.CaptureMouse();
        e.Handled = true;
    }

    private void OnGhostMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || GhostOf(sender) is not Draggable ghost)
            return;
        Point now = e.GetPosition(Surface);
        MoveTo(ghost, new Point(_ghostStart.X + now.X - _dragStart.X, _ghostStart.Y + now.Y - _dragStart.Y));
    }

    private void OnGhostMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Point p = PositionOf(_selected);
        switch (e.Key)
        {
            case Key.Enter:
                foreach (Draggable ghost in _ghosts)
                {
                    if (ghost.Id is Guid id)
                        ButtonResults[id] = PositionOf(ghost);
                    else
                        Result = PositionOf(ghost);
                }
                DialogResult = true;
                break;
            case Key.Escape:
                DialogResult = false;
                break;
            case Key.Left: MoveTo(_selected, new Point(p.X - step, p.Y)); break;
            case Key.Right: MoveTo(_selected, new Point(p.X + step, p.Y)); break;
            case Key.Up: MoveTo(_selected, new Point(p.X, p.Y - step)); break;
            case Key.Down: MoveTo(_selected, new Point(p.X, p.Y + step)); break;
            default: return;
        }
        e.Handled = true;
    }
}
