using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// Investigation only (branch repro-blank-window): the main window is hidden (not WS_VISIBLE) but the DWM keeps showing its
/// frame (Mica backdrop and caption buttons) where it was, until the window is shown and hidden again. Seen on a user's PC
/// while playing with overlay mode. These tests repeat show and hide sequences and look for that frame on the screen.
/// </summary>
[Trait("Category", "Repro")]
public class GhostFrameRepro
{
    private static void Seed(Seed seed)
    {
        seed.Settings.OverlayPanelX = 600;
        seed.Settings.OverlayPanelY = 500;
        seed.Settings.OpenMainHotkey = HotkeyBinding.FromKey(Vk.F14);
        for (int i = 0; i < 3; i++)
            seed.Settings.Loops.Add(Playback.Loop($"Repro loop {i}", null, Playback.Every(Vk.F17, 300)));
    }

    /// <summary>Open PuppyMacro, then overlay mode on after a short delay: hidden while it may still be animating in.</summary>
    [Fact]
    public void Open_then_overlay_after_a_short_delay()
    {
        using var app = new AppSession(Seed);
        IntPtr main = app.MainWindow.Properties.NativeWindowHandle.Value;
        LogSettings();
        TakeReference(main);
        int ghosts = 0, rounds = 0;
        foreach (int delay in new[] { 0, 20, 50, 80, 120, 160, 220, 300, 500 })
            for (int i = 0; i < 3; i++)
            {
                // Hidden in the tray first: overlay on, then off.
                Key(Vk.F24);
                WaitVisible(main, false);
                Key(Vk.F24);
                Thread.Sleep(400);
                Key(Vk.F14); // Open PuppyMacro: shown
                WaitVisible(main, true, 2000);
                if (delay > 0)
                    Thread.Sleep(delay);
                Key(Vk.F24); // overlay on: hidden
                WaitVisible(main, false);
                Thread.Sleep(1500);
                rounds++;
                if (Ghost(main, $"open-overlay-{delay}ms-{i}"))
                    ghosts++;
                Key(Vk.F24); // overlay off
                Thread.Sleep(300);
                Key(Vk.F14);
                WaitVisible(main, true, 2000);
                Thread.Sleep(600);
            }
        Log($"open then overlay: {ghosts} ghosts in {rounds} rounds");
        Assert.Equal(0, ghosts);
    }

    /// <summary>Overlay mode on and off quickly many times, with Open PuppyMacro in between, as when playing.</summary>
    [Fact]
    public void Overlay_toggled_quickly_with_open_in_between()
    {
        using var app = new AppSession(Seed);
        IntPtr main = app.MainWindow.Properties.NativeWindowHandle.Value;
        LogSettings();
        TakeReference(main);
        var random = new Random(42);
        int ghosts = 0;
        for (int i = 0; i < 40; i++)
        {
            int[] keys = { Vk.F24, Vk.F14, Vk.F24, Vk.F24, Vk.F14, Vk.F24 };
            foreach (int key in keys)
            {
                Key(key);
                Thread.Sleep(random.Next(0, 250));
            }
            // End hidden: overlay on.
            if (IsWindowVisible(main))
                Key(Vk.F24);
            WaitVisible(main, false);
            Thread.Sleep(1500);
            if (Ghost(main, $"quick-{i}"))
                ghosts++;
            Key(Vk.F14);
            WaitVisible(main, true, 2000);
            Thread.Sleep(400);
        }
        Log($"quick toggles: {ghosts} ghosts in 40 rounds");
        Assert.Equal(0, ghosts);
    }

    /// <summary>
    /// As in a game: a borderless window covering the screen is in front while overlay mode is toggled and PuppyMacro opened
    /// and hidden; then the game window is minimized (leaving the game) and the screen is checked.
    /// </summary>
    [Fact]
    public void Toggled_over_a_fullscreen_window_then_leaving_it()
    {
        using var app = new AppSession(Seed);
        IntPtr main = app.MainWindow.Properties.NativeWindowHandle.Value;
        LogSettings();
        TakeReference(main);
        using var game = new GameWindow();
        var random = new Random(7);
        int ghosts = 0;
        for (int i = 0; i < 25; i++)
        {
            game.Restore();
            Thread.Sleep(500);
            foreach (int key in new[] { Vk.F14, Vk.F24, Vk.F24, Vk.F14, Vk.F24 })
            {
                game.Activate();
                Key(key);
                Thread.Sleep(random.Next(0, 300));
            }
            if (IsWindowVisible(main))
                Key(Vk.F24);
            WaitVisible(main, false);
            Thread.Sleep(800);
            game.Minimize();
            Thread.Sleep(1500);
            if (Ghost(main, $"game-{i}"))
                ghosts++;
            Key(Vk.F24); // overlay off
            Thread.Sleep(300);
        }
        Log($"over a fullscreen window: {ghosts} ghosts in 25 rounds");
        Assert.Equal(0, ghosts);
    }

    private static void Key(int vk)
    {
        Keyboard.Press((VirtualKeyShort)vk);
        Thread.Sleep(30);
        Keyboard.Release((VirtualKeyShort)vk);
    }

    private static void WaitVisible(IntPtr hwnd, bool visible, int ms = 3000) =>
        Retry.WhileFalse(() => IsWindowVisible(hwnd) == visible, TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// The main window is hidden, yet its title bar strip on the screen is not the black desktop the runner has under it:
    /// the DWM still shows its frame. Saves the strip and the whole screen when it happens.
    /// </summary>
    /// <summary>The main window's title strip on the screen right after its first, clean hide: what is under it.</summary>
    private static Bitmap? _reference;

    private static Bitmap Strip(IntPtr main)
    {
        GetWindowRect(main, out RECT r);
        var strip = Rectangle.FromLTRB(r.Left + 8, r.Top + 4, r.Right - 8, r.Top + 36);
        strip.Intersect(new Rectangle(0, 0, 1024, 768));
        var bitmap = new Bitmap(strip.Width, strip.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(strip.Location, Point.Empty, strip.Size);
        return bitmap;
    }

    /// <summary>Hides the main window once (overlay on, then off) and keeps what its title strip shows then.</summary>
    private static void TakeReference(IntPtr main)
    {
        Key(Vk.F24);
        WaitVisible(main, false);
        Thread.Sleep(1500);
        _reference = Strip(main);
        Save(_reference, "reference");
        Key(Vk.F24);
        Thread.Sleep(300);
        Key(Vk.F14);
        WaitVisible(main, true, 2000);
        Thread.Sleep(600);
    }

    /// <summary>
    /// The main window is hidden, yet its title strip on the screen differs from the reference taken after a clean hide: the
    /// DWM still shows its frame. Saves the strip and the whole screen when it happens.
    /// </summary>
    private static bool Ghost(IntPtr main, string when)
    {
        if (IsWindowVisible(main))
        {
            Log($"{when}: main visible, not checked");
            return false;
        }
        using Bitmap now = Strip(main);
        int changed = 0, total = Math.Min(now.Width, _reference!.Width) * Math.Min(now.Height, _reference.Height);
        for (int y = 0; y < Math.Min(now.Height, _reference.Height); y++)
            for (int x = 0; x < Math.Min(now.Width, _reference.Width); x++)
            {
                Color a = now.GetPixel(x, y), b = _reference.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 40)
                    changed++;
            }
        bool ghost = changed > total / 5;
        Log($"{when}: hidden, {changed} of {total} pixels of the title strip differ from the reference{(ghost ? " GHOST" : "")}");
        if (ghost)
        {
            Save(now, $"ghost-strip-{when}");
            using var screen = new Bitmap(1024, 768);
            using (Graphics g = Graphics.FromImage(screen))
                g.CopyFromScreen(Point.Empty, Point.Empty, screen.Size);
            Save(screen, $"ghost-screen-{when}");
        }
        return ghost;
    }

    private static void Save(Bitmap bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is not { Length: > 0 } folder)
            return;
        Directory.CreateDirectory(folder);
        bitmap.Save(Path.Combine(folder, $"{DateTime.UtcNow:HHmmss.fff}-{name}.png"), ImageFormat.Png);
    }

    private static void LogSettings()
    {
        int client = 0;
        SystemParametersInfo(0x1042, 0, ref client, 0);
        Log($"animation effects (SPI_GETCLIENTAREAANIMATION) = {client}");
    }

    private static void Log(string line)
    {
        string folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } f ? f : Path.GetTempPath();
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "repro.log"), $"{DateTime.UtcNow:HH:mm:ss.fff} {TestContext.Current.Test?.TestDisplayName}: {line}\n");
    }

    /// <summary>A borderless black window covering the screen, as a game in fullscreen windowed mode.</summary>
    private sealed class GameWindow : IDisposable
    {
        private readonly System.Windows.Forms.Form _form;
        private readonly Thread _thread;

        public GameWindow()
        {
            System.Windows.Forms.Form? form = null;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                form = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = new Rectangle(0, 0, 1024, 768), BackColor = Color.FromArgb(20, 60, 20), Text = "Repro game",
                };
                form.Shown += (_, _) => ready.Set();
                System.Windows.Forms.Application.Run(form);
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(10000);
            _form = form!;
        }

        public void Activate() => _form.Invoke(() => { SetForegroundWindow(_form.Handle); _form.Activate(); });

        public void Minimize() => _form.Invoke(() => _form.WindowState = System.Windows.Forms.FormWindowState.Minimized);

        public void Restore() => _form.Invoke(() =>
        {
            _form.WindowState = System.Windows.Forms.FormWindowState.Normal;
            _form.Bounds = new Rectangle(0, 0, 1024, 768);
            SetForegroundWindow(_form.Handle);
        });

        public void Dispose()
        {
            _form.Invoke(() => _form.Close());
            _thread.Join(5000);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
