using System;
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

    public PickPointWindow(bool pickApp = false)
    {
        InitializeComponent();
        _pickApp = pickApp;
        if (pickApp)
        {
            Title = "Pick an app";
            InstructionsText.Text = "Click a window of the app";
        }
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
        };
    }

    public int ResultX { get; private set; }
    public int ResultY { get; private set; }

    /// <summary>With <c>pickApp</c>: the .exe name of the app whose window was clicked.</summary>
    public string? ResultApp { get; private set; }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Instructions.IsMouseOver)
            return;
        NativeMethods.GetCursorPos(out var point);
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
        DialogResult = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pickApp)
            UpdateHovered();
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
            AppLabelText.Text = _hoveredApp.Length > 0 ? _hoveredApp : "This app's name cannot be read";
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
