using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// Investigation only (branch repro-blank-window): the main window sometimes shows only its frame, with nothing inside, after
/// overlay mode. Checks on the screen's pixels whether the main window drew its content after it is shown again.
/// </summary>
[Trait("Category", "Repro")]
public class BlankWindowRepro
{
    private static void SeedOverlay(Seed seed)
    {
        seed.Settings.OverlayPanelX = 80;
        seed.Settings.OverlayPanelY = 80;
        seed.Settings.OpenMainHotkey = HotkeyBinding.FromKey(Vk.F14);
        // Cards with their switches (ToggleSwitch keeps a BitmapCache, dotnet/wpf#2158).
        for (int i = 0; i < 4; i++)
        {
            seed.Settings.Loops.Add(Playback.Loop($"Repro loop {i}", null, Playback.Every(Vk.F17, 300)));
            seed.Macros.Add(Playback.Macro($"Repro macro {i}", null, Playback.Press(Vk.F16)));
        }
    }

    [Fact]
    public void Overlay_on_and_off_many_times_then_open()
    {
        using var app = new AppSession(SeedOverlay);
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        for (int i = 0; i < 8; i++)
        {
            target.Activate();
            target.Tap(Vk.F24); // overlay on: the main window hides
            WaitHidden(app);
            target.Tap(Vk.F24); // overlay off: it stays in the tray
            TargetWindow.Quiet(0.5);
            target.Tap(Vk.F14); // Open PuppyMacro
            WaitShown(app);
            AssertDrawn(app, $"round {i}");
        }
    }

    [Fact]
    public void Display_change_while_hidden_in_overlay_mode()
    {
        using var app = new AppSession(SeedOverlay);
        using (var first = new TargetWindow())
        {
            AssertDrawn(app, "start");
            first.Activate();
            first.Tap(Vk.F24);
            WaitHidden(app);
            first.Tap(Vk.F24); // overlay off: the main window stays hidden
            TargetWindow.Quiet(1);
        }
        try
        {
            Log($"set 800x600: {Display.Set(800, 600)}");
            Thread.Sleep(3000);
        }
        finally
        {
            Log($"set 1024x768: {Display.Set(1024, 768)}");
            Thread.Sleep(3000);
        }
        // A new target window: the display change moved the old one.
        using var target = new TargetWindow();
        target.Activate();
        target.Tap(Vk.F14);
        WaitShown(app);
        AssertDrawn(app, "after display change");
    }

    [Fact]
    public void Gpu_reset_while_hidden_in_overlay_mode()
    {
        string? dxcap = FindDxcap();
        Assert.SkipWhen(dxcap == null, "dxcap is not installed");
        using var app = new AppSession(SeedOverlay);
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        target.Activate();
        target.Tap(Vk.F24);
        WaitHidden(app);
        RunDxcapTdr(dxcap!);
        Thread.Sleep(5000);
        target.Activate();
        target.Tap(Vk.F14);
        WaitShown(app);
        bool opened = Drawn(app, "after GPU reset, opened");
        Log($"ctl area after: {AreaColors(new Rectangle(950, 100, 70, 300), "ctl area after")} colors");
        target.Activate();
        target.Tap(Vk.F24);
        WaitHidden(app);
        target.Tap(Vk.F14);
        WaitShown(app);
        Drawn(app, "after GPU reset, hidden and shown again");
        Assert.True(opened, "after GPU reset, opened: the main window drew nothing");
    }

    private static void SaveScreen(string when)
    {
        if (Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is not { Length: > 0 } folder)
            return;
        using var bitmap = new Bitmap(1024, 768);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(Point.Empty, Point.Empty, bitmap.Size);
        bitmap.Save(Path.Combine(folder, $"screen-{DateTime.UtcNow:HHmmss.fff}-{when.Replace(' ', '_').Replace(',', '_')}.png"), ImageFormat.Png);
        Log($"{when}: screen saved");
    }

    /// <summary>The issue's steps: after the reset, the window is resized.</summary>
    [Fact]
    public void Gpu_reset_while_hidden_then_open_and_resize()
    {
        string? dxcap = FindDxcap();
        Assert.SkipWhen(dxcap == null, "dxcap is not installed");
        using var app = new AppSession(SeedOverlay);
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        Log($"ctl area start: {AreaColors(new Rectangle(950, 100, 70, 300), "ctl area start")} colors");
        target.Activate();
        target.Tap(Vk.F24);
        WaitHidden(app);
        target.Tap(Vk.F24); // overlay off: the main window stays hidden, no window of PuppyMacro is shown
        TargetWindow.Quiet(1);
        RunDxcapTdr(dxcap!);
        Thread.Sleep(5000);
        target.Activate();
        target.Tap(Vk.F14);
        WaitShown(app);
        bool opened = Drawn(app, "after GPU reset, opened");
        Log($"ctl area after: {AreaColors(new Rectangle(950, 100, 70, 300), "ctl area after")} colors");
        IntPtr hwnd = app.MainWindow.Properties.NativeWindowHandle.Value;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1000, 740, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        Thread.Sleep(1000);
        Drawn(app, "after GPU reset, resized");
        // Does it come back by itself? Overlay on and open again (hidden and shown), then minimized and restored.
        target.Activate();
        target.Tap(Vk.F24);
        WaitHidden(app);
        target.Tap(Vk.F14);
        WaitShown(app);
        Drawn(app, "after GPU reset, hidden and shown again");
        ShowWindow(hwnd, 6); // SW_MINIMIZE
        Thread.Sleep(1500);
        ShowWindow(hwnd, 9); // SW_RESTORE
        Thread.Sleep(1500);
        Drawn(app, "after GPU reset, minimized and restored");
        Assert.True(opened, "after GPU reset, opened: the main window drew nothing");
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [Fact]
    public void Gpu_reset_while_shown()
    {
        string? dxcap = FindDxcap();
        Assert.SkipWhen(dxcap == null, "dxcap is not installed");
        using var app = new AppSession(SeedOverlay);
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        RunDxcapTdr(dxcap!);
        Thread.Sleep(30000); // a capture of the screen in the seconds after the reset hangs the runner
        AssertDrawn(app, "after GPU reset");
    }

    /// <summary>
    /// Control without PuppyMacro: a Windows Forms window and a plain WPF window of the test process, hidden during the
    /// reset and shown after it. If they come back empty too, the forced reset breaks every hidden window on this machine.
    /// </summary>
    [Fact]
    public void Gpu_reset_control_other_windows()
    {
        string? dxcap = FindDxcap();
        Assert.SkipWhen(dxcap == null, "dxcap is not installed");
        var formArea = new Rectangle(60, 60, 400, 300);
        var wpfArea = new Rectangle(520, 60, 400, 300);

        System.Windows.Forms.Form? form = null;
        var formReady = new ManualResetEventSlim();
        var formThread = new Thread(() =>
        {
            form = new System.Windows.Forms.Form
            {
                StartPosition = System.Windows.Forms.FormStartPosition.Manual, Bounds = formArea, TopMost = true,
                Text = "Control Forms", BackColor = Color.DarkGreen,
            };
            for (int i = 0; i < 6; i++)
                form.Controls.Add(new System.Windows.Forms.Button { Text = $"Button {i}", Left = 10, Top = 10 + i * 40, Width = 150 });
            form.Shown += (_, _) => formReady.Set();
            System.Windows.Forms.Application.Run(form);
        });
        formThread.SetApartmentState(ApartmentState.STA);
        formThread.Start();
        Assert.True(formReady.Wait(10000));

        System.Windows.Window? wpf = null;
        var wpfReady = new ManualResetEventSlim();
        var wpfThread = new Thread(() =>
        {
            var panel = new System.Windows.Controls.StackPanel { Background = System.Windows.Media.Brushes.DarkRed };
            for (int i = 0; i < 6; i++)
                panel.Children.Add(new System.Windows.Controls.Button { Content = $"WPF button {i}", Margin = new System.Windows.Thickness(8) });
            wpf = new System.Windows.Window
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = wpfArea.Left, Top = wpfArea.Top,
                Width = wpfArea.Width, Height = wpfArea.Height, Topmost = true, Title = "Control WPF", Content = panel,
            };
            wpf.ContentRendered += (_, _) => wpfReady.Set();
            wpf.Show();
            System.Windows.Threading.Dispatcher.Run();
        });
        wpfThread.SetApartmentState(ApartmentState.STA);
        wpfThread.Start();
        Assert.True(wpfReady.Wait(10000));
        Thread.Sleep(1000);

        int formStart = AreaColors(formArea, "control forms start"), wpfStart = AreaColors(wpfArea, "control wpf start");
        form!.Invoke(() => form.Hide());
        wpf!.Dispatcher.Invoke(() => wpf.Hide());
        Thread.Sleep(1000);
        RunDxcapTdr(dxcap!);
        Thread.Sleep(25000); // a capture of the screen in the seconds after the reset hangs the runner
        form.Invoke(() => form.Show());
        wpf.Dispatcher.Invoke(() => wpf.Show());
        Thread.Sleep(3000);
        int formAfter = AreaColors(formArea, "control forms after reset"), wpfAfter = AreaColors(wpfArea, "control wpf after reset");
        Log($"control: forms {formStart} -> {formAfter} colors, wpf {wpfStart} -> {wpfAfter} colors");

        form.Invoke(() => form.Close());
        wpf.Dispatcher.Invoke(() => { wpf.Close(); wpf.Dispatcher.InvokeShutdown(); });
        Assert.True(formAfter > 8 && wpfAfter > 8, $"forms {formAfter}, wpf {wpfAfter} colors after the reset");
    }

    private static int AreaColors(Rectangle area, string when)
    {
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        var seen = new HashSet<int>();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                seen.Add(bitmap.GetPixel(x, y).ToArgb() & 0xF8F8F8);
        if (Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } folder)
            bitmap.Save(Path.Combine(folder, $"area-{DateTime.UtcNow:HHmmss.fff}-{when.Replace(' ', '_')}.png"), ImageFormat.Png);
        return seen.Count;
    }

    private static string? FindDxcap()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dxcap.exe");
        return File.Exists(path) ? path : null;
    }

    private static void RunDxcapTdr(string dxcap)
    {
        using Process p = Process.Start(new ProcessStartInfo(dxcap, "-forcetdr") { UseShellExecute = false })!;
        bool exited = p.WaitForExit(20000);
        if (!exited)
            p.Kill();
        Log($"dxcap -forcetdr: {(exited ? $"exit {p.ExitCode}" : "killed after 20 s")}");
    }

    /// <summary>xUnit keeps a passing test's output to itself: the log is a file, printed by the workflow.</summary>
    private static void Log(string line)
    {
        string folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } f ? f : Path.GetTempPath();
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "repro.log"), $"{DateTime.UtcNow:HH:mm:ss.fff} {TestContext.Current.Test?.TestDisplayName}: {line}\n");
    }

    private static void WaitHidden(AppSession app) =>
        Assert.True(Retry.WhileFalse(() => !app.MainWindowShown(), AppSession.Timeout).Success, "the main window did not hide");

    private static void WaitShown(AppSession app) =>
        Assert.True(Retry.WhileFalse(app.MainWindowShown, AppSession.Timeout).Success, "the main window did not show");

    /// <summary>
    /// The main window's pixels outside the target window (topmost, it covers the middle): its title bar above and the page
    /// under it. A window that drew nothing has the backdrop only, a handful of colors; one that drew its content, text,
    /// icons and cards, has many.
    /// </summary>
    private static void AssertDrawn(AppSession app, string when) =>
        Assert.True(Drawn(app, when), $"{when}: the main window drew nothing");

    private static bool Drawn(AppSession app, string when)
    {
        int colors = 0;
        bool drawn = Retry.WhileFalse(() =>
        {
            colors = Colors(app, when, save: false);
            return colors > 8;
        }, TimeSpan.FromSeconds(6)).Success;
        Colors(app, when, save: true);
        Log($"{when}: {colors} colors, drawn {drawn}");
        return drawn;
    }

    private static int Colors(AppSession app, string when, bool save)
    {
        IntPtr hwnd = app.MainWindow.Properties.NativeWindowHandle.Value;
        GetWindowRect(hwnd, out RECT r);
        var window = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        window.Intersect(new Rectangle(0, 0, 1024, 768));
        if (window.Width <= 0 || window.Height <= 0)
            return 0;
        using var bitmap = new Bitmap(window.Width, window.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(window.Location, Point.Empty, window.Size);
        var seen = new HashSet<int>();
        Rectangle covered = TargetWindow.Area;
        covered.Inflate(4, 4);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                // Only the title bar's strip, above the target window: a window drawn empty has one color there.
                if (y < 4 || window.Top + y >= covered.Top || x < 4 || x >= bitmap.Width - 4)
                    continue;
                seen.Add(bitmap.GetPixel(x, y).ToArgb() & 0xF8F8F8);
            }
        if (save && Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            string name = $"repro-{DateTime.UtcNow:HHmmss.fff}-{string.Join("_", when.Split(Path.GetInvalidFileNameChars()))}.png".Replace(' ', '_');
            bitmap.Save(Path.Combine(folder, name), ImageFormat.Png);
        }
        return seen.Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);

        public static int Set(int width, int height)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(null, -1, ref dm))
                return -100;
            dm.dmPelsWidth = width;
            dm.dmPelsHeight = height;
            dm.dmFields = 0x80000 | 0x100000;
            return ChangeDisplaySettings(ref dm, 0);
        }
    }
}
