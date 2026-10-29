using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PuppyMacro;

/// <summary>
/// Investigation only (branch repro-blank-window): logs the main window's messages that drive WPF's rendering and the
/// state of its HwndTarget, to find which state stays stuck after a GPU reset while the window is hidden.
/// </summary>
internal static class ReproTrace
{
    private static string? _file;
    private static uint _displayAvailability, _needsRePresent, _updateSettings;

    private static readonly string[] Fields =
    {
        "_displayDevicesAvailable", "_wasWmPaintProcessingDeferred", "_isRenderTargetEnabled", "_needsRePresentOnWake",
        "_hasRePresentedSinceWake", "_isSessionDisconnected", "_isSuspended", "_isMinimized",
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public static void Attach(Window window)
    {
        string folder = Environment.GetEnvironmentVariable("E2E_SCREENSHOTS") is { Length: > 0 } f ? f : Path.GetTempPath();
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "app-trace.log");
        _displayAvailability = RegisterWindowMessage("DisplayDevicesAvailabilityChanged");
        _needsRePresent = RegisterWindowMessage("NeedsRePresentOnWake");
        _updateSettings = RegisterWindowMessage("UpdateWindowSettings");
        Write($"trace on, handle {new WindowInteropHelper(window).Handle}");
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Hook(window);
        else
            window.SourceInitialized += (_, _) => Hook(window);
    }

    private static void Hook(Window window)
    {
        {
            var source = (HwndSource)PresentationSource.FromVisual(window)!;
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                string? name = Name((uint)msg);
                if (name != null)
                    Write($"{name} w={wParam} visible={IsWindowVisible(hwnd)} | {State(source)}");
                return IntPtr.Zero;
            });
            Write($"attached | {State(source)}");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            string last = "";
            timer.Tick += (_, _) =>
            {
                string now = $"visible={IsWindowVisible(source.Handle)} | {State(source)}";
                if (now != last)
                    Write($"state {now}");
                last = now;
            };
            timer.Start();
        }
    }

    private static string? Name(uint msg) => msg switch
    {
        0x000F => "WM_PAINT",
        0x0018 => "WM_SHOWWINDOW",
        0x0047 => "WM_WINDOWPOSCHANGED",
        0x007E => "WM_DISPLAYCHANGE",
        0x031E => "WM_DWMCOMPOSITIONCHANGED",
        0x02B1 => "WM_WTSSESSION_CHANGE",
        0x0218 => "WM_POWERBROADCAST",
        _ when msg == _displayAvailability => "DisplayDevicesAvailabilityChanged",
        _ when msg == _needsRePresent => "NeedsRePresentOnWake",
        _ when msg == _updateSettings => "UpdateWindowSettings",
        _ => null,
    };

    private static string State(HwndSource source)
    {
        if (source.CompositionTarget is not HwndTarget target)
            return "no target";
        Type type = typeof(HwndTarget);
        return string.Join(" ", Fields.Select(name =>
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return $"{name.TrimStart('_')}={(field == null ? "?" : field.GetValue(target))}";
        }));
    }

    private static void Write(string line)
    {
        try
        {
            File.AppendAllText(_file!, $"{DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}\n");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "app-trace-error.log"), ex + "\n"); } catch (Exception) { }
        }
    }
}
