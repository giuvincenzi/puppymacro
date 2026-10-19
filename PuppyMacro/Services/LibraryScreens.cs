using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Native;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Services;

/// <summary>
/// The screen a Library collection is made on (<see cref="LibraryScreen"/>): the monitor its points are on, its place on the
/// desktop and Windows' scale there, so another screen configuration can be told (a warning, never a conversion).
/// </summary>
internal static class LibraryScreens
{
    /// <summary>The first point the actions use (a click, a move, a condition's pixel or image), or null.</summary>
    public static (int X, int Y)? FirstPoint(IEnumerable<MacroAction> actions)
    {
        foreach (var action in MacroDefinition.Walk(actions))
        {
            if (action.Type is MacroActionType.Click or MacroActionType.MouseDown or MacroActionType.MouseUp or MacroActionType.MoveTo
                && action.FoundId == null)
                return (action.X, action.Y);
            foreach (var condition in action.If?.Conditions ?? new List<MacroCondition>())
            {
                if (condition.Kind == ConditionKind.Pixel)
                    return (condition.X, condition.Y);
                if (condition.Images?.FirstOrDefault() is { } image)
                    return (image.X, image.Y);
            }
        }
        return null;
    }

    /// <summary>The monitor holding the actions' first point (else the main one), with its place and Windows' scale.</summary>
    public static LibraryScreen? Of(IEnumerable<MacroAction> actions)
    {
        var monitors = ScreenCapture.MonitorBounds();
        if (monitors.Count == 0)
            return null;
        var point = FirstPoint(actions) ?? (0, 0);
        var monitor = monitors.FirstOrDefault(m => point.X >= m.X && point.X < m.X + m.Width && point.Y >= m.Y && point.Y < m.Y + m.Height);
        if (monitor.Width == 0)
            monitor = monitors.FirstOrDefault(m => m.X == 0 && m.Y == 0) is { Width: > 0 } main ? main : monitors[0];
        return new LibraryScreen
        {
            X = monitor.X,
            Y = monitor.Y,
            Width = monitor.Width,
            Height = monitor.Height,
            ScalePercent = ScaleAt(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2),
        };
    }

    /// <summary>Windows' scale of the monitor at a point, in percent (100 when Windows does not say).</summary>
    private static int ScaleAt(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetDpiForMonitor(handle, 0, out uint dpi, out _) == 0 && dpi > 0
            ? (int)System.Math.Round(dpi * 100.0 / 96)
            : 100;
    }

    /// <summary>Whether this PC has the collection's screen: a monitor with the same place and size, at the same scale.</summary>
    public static bool HasHere(LibraryScreen screen) =>
        ScreenCapture.MonitorBounds().Any(m => m.X == screen.X && m.Y == screen.Y && m.Width == screen.Width && m.Height == screen.Height)
        && ScaleAt(screen.X + screen.Width / 2, screen.Y + screen.Height / 2) == screen.ScalePercent;
}
