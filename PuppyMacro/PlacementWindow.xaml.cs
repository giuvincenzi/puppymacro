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
internal sealed record PlacementButton(Guid Id, string Name, string Label, string HotkeyText, FloatingButtonSize Size, int Opacity, Point Position);

/// <summary>
/// Full-screen overlay to choose where the overlay panel and the floating buttons appear.
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
    private Draggable? _selected;
    private Point _dragStart;
    private Point _ghostStart;
    private bool _dragging;

    /// <summary>Chosen panel position (screen coordinates in WPF units) when the dialog result is true.</summary>
    public Point Result { get; private set; }

    /// <summary>Chosen floating button positions (top-left of the circle), by item id.</summary>
    internal Dictionary<Guid, Point> ButtonResults { get; } = new();

    /// <param name="showPanel">The overlay panel is shown in overlay mode; when false it is left out
    /// and <see cref="Result"/> stays <paramref name="start"/>.</param>
    internal PlacementWindow(IList loops, IList macros, bool showPanel, Point start, int opacity,
        string exitHotkey, string stopAllHotkey, IReadOnlyList<PlacementButton> buttons, Guid? select = null)
    {
        InitializeComponent();
        Result = start;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // The whole overlay as in overlay mode: the real panel (same items, opacity and hotkeys), if shown, and every button.
        Draggable? first = null;
        if (showPanel)
        {
            GhostPanel.Bind(loops, macros);
            GhostPanel.SetBackgroundOpacity(opacity);
            GhostPanel.SetHotkeyLabels(exitHotkey, stopAllHotkey);
            first = AddGhost(new Draggable { Element = Ghost, Outline = GhostOutline, Name = "Overlay panel" });
            _selected = first;
            MoveTo(first, start);
        }
        else
        {
            Ghost.Visibility = Visibility.Collapsed;
        }

        foreach (PlacementButton button in buttons)
        {
            var view = new FloatingButtonView();
            view.Show(button.Label, button.HotkeyText, button.Size);
            view.SetBackgroundOpacity(button.Opacity);
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
            first ??= ghost;
            if (button.Id == select || _selected == null)
                _selected = ghost;
            MoveTo(ghost, button.Position);
        }

        if (buttons.Count > 0)
        {
            InstructionsText.Text = showPanel
                ? "Drag the panel and the buttons where you want them"
                : buttons.Count == 1 ? "Drag the button where you want it" : "Drag the buttons where you want them";
            NudgeText.Text = "Nudge the selected one (Shift ×10)";
        }
        Select(_selected ?? first ?? throw new InvalidOperationException("Nothing to position."));

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
        if (_selected is not Draggable selected)
            return;
        Point p = PositionOf(selected);
        SelectionText.Text = $"{selected.Name}   X {p.X}   Y {p.Y}";
        Canvas.SetLeft(SelectionTag, Canvas.GetLeft(selected.Element));
        Canvas.SetTop(SelectionTag, Canvas.GetTop(selected.Element) - 28);
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
        if (_selected is not Draggable selected)
            return;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Point p = PositionOf(selected);
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
            case Key.Left: MoveTo(selected, new Point(p.X - step, p.Y)); break;
            case Key.Right: MoveTo(selected, new Point(p.X + step, p.Y)); break;
            case Key.Up: MoveTo(selected, new Point(p.X, p.Y - step)); break;
            case Key.Down: MoveTo(selected, new Point(p.X, p.Y + step)); break;
            default: return;
        }
        e.Handled = true;
    }
}
