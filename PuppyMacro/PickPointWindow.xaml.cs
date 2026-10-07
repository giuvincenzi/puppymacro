using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuppyMacro.Native;

namespace PuppyMacro;

/// <summary>Full-screen overlay: the user clicks a point; the result is in physical screen pixels.</summary>
public partial class PickPointWindow
{
    public PickPointWindow()
    {
        InitializeComponent();
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
        };
    }

    public int ResultX { get; private set; }
    public int ResultY { get; private set; }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Instructions.IsMouseOver)
            return;
        NativeMethods.GetCursorPos(out var point);
        ResultX = point.X;
        ResultY = point.Y;
        DialogResult = true;
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
