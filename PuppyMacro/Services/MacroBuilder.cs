using System;
using System.Collections.Generic;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

public enum RawEventKind
{
    KeyDown,
    KeyUp,
    ButtonDown,
    ButtonUp,
    Move,
    Wheel,
    HWheel,
}

/// <summary>One recorded input event. <see cref="T"/> is ms since the recording started.</summary>
public readonly record struct RawEvent(RawEventKind Kind, int Vk, int X, int Y, int Delta, double T);

/// <summary>Turns recorded raw events into readable macro actions with their original delays.</summary>
internal static class MacroBuilder
{
    private const int ClickTolerancePx = 3;
    private const double DoubleClickMs = 400;
    private const double ScrollGroupMs = 300;
    private const double PathTolerancePx = 1.5;

    public static List<MacroAction> Build(IReadOnlyList<RawEvent> events)
    {
        var actions = new List<MacroAction>();
        double previousEnd = double.NaN;
        int i = 0;

        void Add(MacroAction action, double start, double end)
        {
            action.DelayMs = double.IsNaN(previousEnd) ? 0 : Math.Max(0, Math.Round(start - previousEnd));
            actions.Add(action);
            previousEnd = end;
        }

        while (i < events.Count)
        {
            RawEvent e = events[i];
            switch (e.Kind)
            {
                case RawEventKind.Move:
                {
                    int j = i;
                    while (j + 1 < events.Count && events[j + 1].Kind == RawEventKind.Move)
                        j++;
                    if (j == i)
                    {
                        Add(new MacroAction { Type = MacroActionType.MoveTo, X = e.X, Y = e.Y }, e.T, e.T);
                    }
                    else
                    {
                        var points = new List<PathPoint>();
                        for (int k = i; k <= j; k++)
                            points.Add(new PathPoint { X = events[k].X, Y = events[k].Y, T = Math.Round(events[k].T - e.T, 1) });
                        Add(new MacroAction { Type = MacroActionType.MovePath, Path = Simplify(points) }, e.T, events[j].T);
                    }
                    i = j + 1;
                    break;
                }

                case RawEventKind.KeyDown:
                    if (i + 1 < events.Count && events[i + 1].Kind == RawEventKind.KeyUp && events[i + 1].Vk == e.Vk)
                    {
                        RawEvent up = events[i + 1];
                        Add(new MacroAction { Type = MacroActionType.PressKey, Vk = e.Vk, HoldMs = Math.Round(up.T - e.T) }, e.T, up.T);
                        i += 2;
                    }
                    else
                    {
                        Add(new MacroAction { Type = MacroActionType.KeyDown, Vk = e.Vk }, e.T, e.T);
                        i++;
                    }
                    break;

                case RawEventKind.KeyUp:
                    Add(new MacroAction { Type = MacroActionType.KeyUp, Vk = e.Vk }, e.T, e.T);
                    i++;
                    break;

                case RawEventKind.ButtonDown:
                    if (i + 1 < events.Count && events[i + 1].Kind == RawEventKind.ButtonUp && events[i + 1].Vk == e.Vk
                        && Math.Abs(events[i + 1].X - e.X) <= ClickTolerancePx && Math.Abs(events[i + 1].Y - e.Y) <= ClickTolerancePx)
                    {
                        RawEvent up = events[i + 1];
                        var last = actions.Count > 0 ? actions[^1] : null;
                        if (last is { Type: MacroActionType.Click, ClickCount: 1 } && last.Vk == e.Vk
                            && Math.Abs(last.X - e.X) <= ClickTolerancePx && Math.Abs(last.Y - e.Y) <= ClickTolerancePx
                            && e.T - previousEnd < DoubleClickMs)
                        {
                            last.ClickCount = 2;
                            previousEnd = up.T;
                        }
                        else
                        {
                            Add(new MacroAction { Type = MacroActionType.Click, Vk = e.Vk, X = e.X, Y = e.Y, HoldMs = Math.Round(up.T - e.T) }, e.T, up.T);
                        }
                        i += 2;
                    }
                    else
                    {
                        Add(new MacroAction { Type = MacroActionType.MouseDown, Vk = e.Vk, X = e.X, Y = e.Y }, e.T, e.T);
                        i++;
                    }
                    break;

                case RawEventKind.ButtonUp:
                    Add(new MacroAction { Type = MacroActionType.MouseUp, Vk = e.Vk, X = e.X, Y = e.Y }, e.T, e.T);
                    i++;
                    break;

                case RawEventKind.Wheel:
                case RawEventKind.HWheel:
                {
                    ScrollDirection direction = e.Kind == RawEventKind.Wheel
                        ? (e.Delta > 0 ? ScrollDirection.Up : ScrollDirection.Down)
                        : (e.Delta > 0 ? ScrollDirection.Right : ScrollDirection.Left);
                    int total = Math.Abs(e.Delta);
                    int j = i;
                    while (j + 1 < events.Count && events[j + 1].Kind == e.Kind
                           && Math.Sign(events[j + 1].Delta) == Math.Sign(e.Delta)
                           && events[j + 1].T - events[j].T < ScrollGroupMs)
                    {
                        j++;
                        total += Math.Abs(events[j].Delta);
                    }
                    int steps = Math.Max(1, (int)Math.Round(total / 120.0));
                    Add(new MacroAction { Type = MacroActionType.Scroll, ScrollDirection = direction, ScrollSteps = steps }, e.T, events[j].T);
                    i = j + 1;
                    break;
                }

                default:
                    i++;
                    break;
            }
        }
        return actions;
    }

    /// <summary>Ramer–Douglas–Peucker: keeps only the points needed to follow the same line.</summary>
    private static List<PathPoint> Simplify(List<PathPoint> points)
    {
        if (points.Count < 3)
            return points;

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double maxDistance = 0;
            int index = -1;
            for (int k = first + 1; k < last; k++)
            {
                double d = DistanceToSegment(points[k], points[first], points[last]);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    index = k;
                }
            }
            if (index >= 0 && maxDistance > PathTolerancePx)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        var result = new List<PathPoint>();
        for (int k = 0; k < points.Count; k++)
        {
            if (keep[k])
                result.Add(points[k]);
        }
        return result;
    }

    private static double DistanceToSegment(PathPoint p, PathPoint a, PathPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
            return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double px = a.X + t * dx, py = a.Y + t * dy;
        return Math.Sqrt(Math.Pow(p.X - px, 2) + Math.Pow(p.Y - py, 2));
    }
}
