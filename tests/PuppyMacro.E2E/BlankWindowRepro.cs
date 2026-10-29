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
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        target.Activate();
        target.Tap(Vk.F24);
        WaitHidden(app);
        try
        {
            Console.WriteLine($"set 800x600: {Display.Set(800, 600)}");
            Thread.Sleep(3000);
        }
        finally
        {
            Console.WriteLine($"set 1024x768: {Display.Set(1024, 768)}");
            Thread.Sleep(3000);
        }
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
        AssertDrawn(app, "after GPU reset");
    }

    [Fact]
    public void Gpu_reset_while_shown()
    {
        string? dxcap = FindDxcap();
        Assert.SkipWhen(dxcap == null, "dxcap is not installed");
        using var app = new AppSession(SeedOverlay);
        using var target = new TargetWindow();
        AssertDrawn(app, "start");
        RunDxcapTdr(dxcap!);
        Thread.Sleep(5000);
        AssertDrawn(app, "after GPU reset");
    }

    private static string? FindDxcap()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dxcap.exe");
        return File.Exists(path) ? path : null;
    }

    private static void RunDxcapTdr(string dxcap)
    {
        using Process p = Process.Start(new ProcessStartInfo(dxcap, "-forcetdr") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Console.WriteLine($"dxcap -forcetdr: exit {p.ExitCode}: {output}");
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
    private static void AssertDrawn(AppSession app, string when)
    {
        int colors = 0;
        bool drawn = Retry.WhileFalse(() =>
        {
            colors = Colors(app, when, save: false);
            return colors > 40;
        }, TimeSpan.FromSeconds(6)).Success;
        Colors(app, when, save: true);
        Console.WriteLine($"{when}: {colors} colors, drawn {drawn}");
        Assert.True(drawn, $"{when}: the main window drew nothing ({colors} colors)");
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
                if (covered.Contains(window.Left + x, window.Top + y))
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
