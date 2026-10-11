using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace PuppyMacro.E2E;

public enum TargetEventKind { KeyDown, KeyUp, ButtonDown, ButtonUp, Wheel, HWheel, Move }

/// <summary>
/// Something the target window received. <see cref="Code"/> is the virtual-key code of a key or mouse
/// button (Ctrl arrives as VK_CONTROL), <see cref="Position"/> the cursor in screen pixels,
/// <see cref="Delta"/> a wheel's delta and <see cref="Ms"/> the time since the window was created.
/// </summary>
public readonly record struct TargetEvent(TargetEventKind Kind, int Code, Point Position, int Delta, double Ms);

/// <summary>
/// The window the playback tests send input to: a topmost window of the test process, at a fixed
/// place inside the GitHub runner's 1024x768 screen, that logs every key, mouse button, wheel and move
/// it receives, with the time. The upper part takes clicks; the text box at the bottom has the
/// keyboard focus and receives pasted text.
/// Every input a test sends, and every input a loop, macro or remap of the test sends, goes to this
/// window: before sending, a test checks that it is in front (<see cref="RequireForeground"/>).
/// Coordinates are physical pixels, the unit of PuppyMacro's mouse actions.
/// </summary>
public sealed class TargetWindow : IDisposable
{
    public const string Title = "PuppyMacro E2E target";

    /// <summary>Where the window is, in physical pixels.</summary>
    public static readonly Rectangle Area = new(40, 40, 900, 640);

    private const int TextHeight = 160;

    /// <summary>The part of the window that takes clicks (above the text box), in screen pixels.</summary>
    public static Rectangle ClickArea => new(Area.X, Area.Y, Area.Width, Area.Height - TextHeight);

    /// <summary>
    /// A point of the click area, <paramref name="fx"/> and <paramref name="fy"/> from 0 to 1. The window's
    /// place is fixed, so a test can use its points in the data it seeds before the window opens.
    /// </summary>
    public static Point PointAt(double fx, double fy) =>
        new(ClickArea.X + (int)(ClickArea.Width * fx), ClickArea.Y + (int)(ClickArea.Height * fy));

    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<TargetEvent> _events = new();
    private readonly object _sync = new();
    private Form? _form;
    private TextBox? _text;

    public TargetWindow()
    {
        Dpi.UsePhysicalPixels();
        using var ready = new ManualResetEventSlim();
        Exception? failed = null;
        _thread = new Thread(() =>
        {
            try
            {
                Dpi.UsePhysicalPixels();
                _form = new TargetForm
                {
                    Text = Title,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = Area,
                    TopMost = true,
                    ShowInTaskbar = false,
                    BackColor = Color.FromArgb(24, 40, 56),
                };
                _text = new TextBox
                {
                    Multiline = true,
                    AcceptsReturn = true,
                    Dock = DockStyle.Bottom,
                    Height = TextHeight,
                    Font = new Font("Segoe UI", 11),
                };
                _form.Controls.Add(_text);
                Application.AddMessageFilter(new Filter(this));
                _form.Shown += (_, _) => ready.Set();
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                failed = ex;
                ready.Set();
            }
        })
        { IsBackground = true, Name = "E2E target window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(AppSession.Timeout) || failed != null)
            throw new InvalidOperationException("The target window did not open.", failed);

        Handle = OnWindowThread(() => _form!.Handle);
        Rectangle clickArea = OnWindowThread(() => new Rectangle(_form!.PointToScreen(Point.Empty), new Size(_form.ClientSize.Width, _text!.Top)));
        if (clickArea != ClickArea)
            throw new InvalidOperationException($"The target window is at {clickArea}, expected {ClickArea}.");
        Activate();
    }

    public IntPtr Handle { get; }

    /// <summary>The text box's text (pasted text arrives there).</summary>
    public string Text
    {
        get => OnWindowThread(() => _text!.Text);
        set => OnWindowThread(() => _text!.Text = value);
    }

    /// <summary>The clipboard's text, read and written on the window's STA thread.</summary>
    public string ClipboardText
    {
        get => OnWindowThread(() => Clipboard.ContainsText() ? Clipboard.GetText() : "");
        set => OnWindowThread(() => Clipboard.SetText(value));
    }

    public static Point Cursor => Dpi.CursorPosition();

    public IReadOnlyList<TargetEvent> Events
    {
        get
        {
            lock (_sync)
                return _events.ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
            _events.Clear();
    }

    /// <summary>Presses of a key or mouse button (a double-click's second press included).</summary>
    public List<TargetEvent> Downs(int vk) => Events.Where(e => e.Code == vk && e.Kind is TargetEventKind.KeyDown or TargetEventKind.ButtonDown).ToList();

    public List<TargetEvent> Ups(int vk) => Events.Where(e => e.Code == vk && e.Kind is TargetEventKind.KeyUp or TargetEventKind.ButtonUp).ToList();

    public List<TargetEvent> Of(TargetEventKind kind) => Events.Where(e => e.Kind == kind).ToList();

    /// <summary>Brings the window to the front with a click on its corner and gives the text box the focus.</summary>
    public void Activate()
    {
        Clear();
        Mouse.Click(PointAt(0.98, 0.04));
        // Its own click arrived (so it is not left in the events), and the window is in front.
        Assert.True(Retry.WhileFalse(() => Native.GetForegroundWindow() == Handle && Ups(Vk.LButton).Count > 0, AppSession.Timeout).Success,
            $"the target window did not come to the front (\"{ForegroundTitle()}\" is)");
        OnWindowThread(() => _text!.Focus());
        Clear();
    }

    /// <summary>Stops the test unless the target window is in front, so no input reaches another app.</summary>
    public void RequireForeground()
    {
        if (Native.GetForegroundWindow() != Handle)
            throw new InvalidOperationException($"The target window is not in front (\"{ForegroundTitle()}\" is): no input is sent.");
    }

    public void Tap(int vk)
    {
        Press(vk);
        Release(vk);
    }

    public void Press(int vk)
    {
        RequireForeground();
        Keyboard.Press((VirtualKeyShort)vk);
    }

    public void Release(int vk)
    {
        RequireForeground();
        Keyboard.Release((VirtualKeyShort)vk);
    }

    /// <summary>A real click (input from the test, not from PuppyMacro) at a point of the target window.</summary>
    public void Click(Point point, MouseButton button = MouseButton.Left)
    {
        RequireForeground();
        Assert.True(Area.Contains(point), $"{point} is outside the target window");
        Mouse.Click(point, button);
    }

    /// <summary>
    /// Moves the mouse to a point of the target window as a real mouse does, in small steps sent with
    /// SendInput. FlaUI's Mouse.MoveTo sets the cursor position instead, which the low-level mouse hook
    /// (and so recording) never sees.
    /// </summary>
    public void MoveMouse(Point to)
    {
        RequireForeground();
        Assert.True(Area.Contains(to), $"{to} is outside the target window");
        Point from = Cursor;
        const int steps = 20;
        for (int step = 1; step <= steps; step++)
        {
            Native.MoveCursor(from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps);
            Thread.Sleep(10);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds on the events received so far.</summary>
    public void WaitFor(Func<TargetWindow, bool> condition, double seconds, string failure)
    {
        bool met = Retry.WhileFalse(() => condition(this), TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(50)).Success;
        Assert.True(met, $"{failure}. In front: \"{ForegroundTitle()}\". Received: {Describe()}");
    }

    /// <summary>
    /// Waits a fixed time: only to check that something does NOT happen (a stopped loop presses
    /// nothing more), where there is nothing to wait for.
    /// </summary>
    public static void Quiet(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    /// <summary>The title of the window in front, for failure messages.</summary>
    public static string ForegroundTitle()
    {
        var title = new System.Text.StringBuilder(256);
        Native.GetWindowText(Native.GetForegroundWindow(), title, title.Capacity);
        return title.ToString();
    }

    /// <summary>The events, for failure messages (moves left out).</summary>
    public string Describe() => string.Join(", ", Events.Where(e => e.Kind != TargetEventKind.Move).Take(80)
        .Select(e => $"{e.Kind} {e.Code:X2}{(e.Delta != 0 ? " " + e.Delta : "")} @{e.Ms:0}"));

    private T OnWindowThread<T>(Func<T> func) => (T)_form!.Invoke(func)!;

    private void OnWindowThread(Action action) => _form!.Invoke(action);

    private void Add(TargetEventKind kind, int code, Point position, int delta = 0)
    {
        lock (_sync)
            _events.Add(new TargetEvent(kind, code, position, delta, _clock.Elapsed.TotalMilliseconds));
    }

    // Window messages
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_XBUTTONDBLCLK = 0x020D;
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>Sees every input message of the window's thread, whichever control it is for.</summary>
    private void Record(ref Message m)
    {
        long w = m.WParam.ToInt64();
        long l = m.LParam.ToInt64();
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                // Bit 30: the key was already down (keyboard auto-repeat).
                if ((l & 0x40000000) == 0)
                    Add(TargetEventKind.KeyDown, (int)w, Cursor);
                break;
            case WM_KEYUP or WM_SYSKEYUP:
                Add(TargetEventKind.KeyUp, (int)w, Cursor);
                break;
            case WM_MOUSEMOVE:
                Add(TargetEventKind.Move, 0, ToScreen(m.HWnd, l));
                break;
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK:
                Add(TargetEventKind.ButtonDown, Vk.LButton, ToScreen(m.HWnd, l));
                break;
            case WM_LBUTTONUP:
                Add(TargetEventKind.ButtonUp, Vk.LButton, ToScreen(m.HWnd, l));
                break;
            case WM_RBUTTONDOWN or WM_RBUTTONDBLCLK:
                Add(TargetEventKind.ButtonDown, Vk.RButton, ToScreen(m.HWnd, l));
                break;
            case WM_RBUTTONUP:
                Add(TargetEventKind.ButtonUp, Vk.RButton, ToScreen(m.HWnd, l));
                break;
            case WM_MBUTTONDOWN or WM_MBUTTONDBLCLK:
                Add(TargetEventKind.ButtonDown, Vk.MButton, ToScreen(m.HWnd, l));
                break;
            case WM_MBUTTONUP:
                Add(TargetEventKind.ButtonUp, Vk.MButton, ToScreen(m.HWnd, l));
                break;
            case WM_XBUTTONDOWN or WM_XBUTTONDBLCLK:
                Add(TargetEventKind.ButtonDown, XButton(w), ToScreen(m.HWnd, l));
                break;
            case WM_XBUTTONUP:
                Add(TargetEventKind.ButtonUp, XButton(w), ToScreen(m.HWnd, l));
                break;
            case WM_MOUSEWHEEL:
                Add(TargetEventKind.Wheel, 0, ScreenPoint(l), (short)((w >> 16) & 0xFFFF));
                break;
            case WM_MOUSEHWHEEL:
                Add(TargetEventKind.HWheel, 0, ScreenPoint(l), (short)((w >> 16) & 0xFFFF));
                break;
        }
    }

    private static int XButton(long wParam) => ((wParam >> 16) & 0xFFFF) == 1 ? Vk.XButton1 : Vk.XButton2;

    private static Point ScreenPoint(long lParam) => new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    private static Point ToScreen(IntPtr window, long lParam)
    {
        var point = new Native.POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        Native.ClientToScreen(window, ref point);
        return new Point(point.X, point.Y);
    }

    private sealed class TargetForm : Form
    {
        private const int WM_APPCOMMAND = 0x0319;

        protected override void WndProc(ref Message m)
        {
            // The mouse's back and forward buttons become "browser back / forward" commands that
            // would go up to the shell: they stop here.
            if (m.Msg == WM_APPCOMMAND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed class Filter : IMessageFilter
    {
        private readonly TargetWindow _owner;

        public Filter(TargetWindow owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            _owner.Record(ref m);
            return false;
        }
    }

    public void Dispose()
    {
        // Keys a failed test may have left down (only the F keys the tests use).
        for (int vk = Vk.F13; vk <= Vk.F24; vk++)
            Keyboard.Release((VirtualKeyShort)vk);
        if (_form != null && !_form.IsDisposed)
            OnWindowThread(() => _form.Close());
        _thread.Join(AppSession.Timeout);
    }
}

/// <summary>Physical pixels for this test thread and the target window's thread, as PuppyMacro uses.</summary>
internal static class Dpi
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public static void UsePhysicalPixels() => Native.SetThreadDpiAwarenessContext(PerMonitorAwareV2);

    public static Point CursorPosition()
    {
        Native.GetCursorPos(out Native.POINT point);
        return new Point(point.X, point.Y);
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // The union's largest member is MOUSEINPUT; a keyboard input is never sent here.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    /// <summary>A mouse move to a screen point (physical pixels) as real mouse input, as PuppyMacro's InputSender.MoveTo.</summary>
    public static void MoveCursor(int x, int y)
    {
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var input = new INPUT
        {
            type = 0, // INPUT_MOUSE
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}

/// <summary>
/// Virtual-key codes the tests use. Hotkeys and the keys loops, macros and remaps send are F13 to F24:
/// no keyboard has them, so nothing happens if one reaches another window.
/// </summary>
public static class Vk
{
    public const int LButton = 0x01, RButton = 0x02, MButton = 0x04, XButton1 = 0x05, XButton2 = 0x06;
    public const int Return = 0x0D, Control = 0x11, LControl = 0xA2, A = 0x41;
    public const int F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F, F17 = 0x80, F18 = 0x81;
    public const int F19 = 0x82, F20 = 0x83, F21 = 0x84, F22 = 0x85, F23 = 0x86, F24 = 0x87;
}
