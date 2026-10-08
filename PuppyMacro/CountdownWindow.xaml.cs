using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PuppyMacro.Native;

namespace PuppyMacro;

/// <summary>
/// Big number in a ring in the middle of the main screen before recording starts.
/// Click-through and never activated, so the window to record keeps the focus.
/// </summary>
public partial class CountdownWindow
{
    public CountdownWindow()
    {
        InitializeComponent();
        double length = Math.PI * (Ring.Width - Ring.StrokeThickness) / Ring.StrokeThickness;
        Ring.StrokeDashArray = new DoubleCollection { length, length };
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => CenterOnScreen();
    }

    /// <summary>Shows <paramref name="number"/> and empties the ring in one second.</summary>
    public void SetNumber(int number)
    {
        NumberText.Text = number.ToString();
        var empty = new DoubleAnimation(0, Ring.StrokeDashArray[0], TimeSpan.FromSeconds(1));
        Ring.BeginAnimation(Shape.StrokeDashOffsetProperty, empty);
    }

    private void CenterOnScreen()
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + (work.Height - ActualHeight) / 2;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Click-through + never activated + hidden from Alt+Tab.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }
}
