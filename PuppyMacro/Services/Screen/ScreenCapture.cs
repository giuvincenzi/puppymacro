using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace PuppyMacro.Services.Screen;

/// <summary>The screen as the conditions see it: <see cref="ScreenCapture"/>, or a picture in the unit tests.</summary>
internal interface IScreen
{
    /// <summary>Starts looking at the monitor that holds the area's center; null when no monitor holds it.</summary>
    IScreenWatch? Start(Rect area);

    /// <summary>The waiter is set at every new frame until it is removed.</summary>
    void Listen(AutoResetEvent waiter);

    void Unlisten(AutoResetEvent waiter);
}

/// <summary>One condition looking at one monitor, until disposed. Areas are desktop coordinates (physical pixels).</summary>
internal interface IScreenWatch : IDisposable
{
    Rect Bounds { get; }

    /// <summary>Changes at every new image of the monitor; 0 until the first one.</summary>
    long Version { get; }

    /// <summary>The graphics card the monitor is connected to; null when there is none to use.</summary>
    GpuMatcher? Gpu { get; }

    /// <summary>The card is the software renderer: the graphics card would be no faster than the processor.</summary>
    bool Software { get; }

    /// <summary>A part of the last image on the card, in SDR colors; null before the first image. Disposed by the caller.</summary>
    GpuMatcher.GpuBuffer? Picture(Rect area);

    /// <summary>A part of the last image as a BGR picture, in SDR colors; null before the first image.</summary>
    Mat? Copy(Rect area);
}

/// <summary>
/// What the screen shows, for the macros' conditions: Desktop Duplication of each monitor, one per monitor for the whole
/// app (Windows allows a process only one per monitor), running only while a condition looks at that monitor
/// (<see cref="Start"/>). The frame stays on the graphics card the monitor is connected to; a condition copies or searches
/// only its area. Frames that only move the mouse pointer carry no new image and are skipped. When Windows refuses for
/// a moment (a display mode or HDR change, a UAC prompt, the lock screen) it tries again until the screen is back.
/// </summary>
internal sealed unsafe class ScreenCapture : IScreen
{
    /// <summary>How long a monitor's capture keeps running after the last condition: the next If starts at once.</summary>
    private const int LingerMs = 5000;

    public static ScreenCapture Shared { get; } = new();

    private readonly object _sync = new();
    private readonly List<MonitorCapture> _monitors = new();
    private readonly List<AutoResetEvent> _listeners = new();
    private int _watchers;
    private Timer? _linger;

    /// <summary>
    /// True while a condition looks at the screen (the app then leaves its own windows out of every capture, so a
    /// condition never sees PuppyMacro's windows). Raised on the thread that changed it.
    /// </summary>
    public event Action<bool>? ActiveChanged;

    public bool Active
    {
        get { lock (_sync) return _watchers > 0; }
    }

    /// <summary>
    /// Starts looking at the monitor that holds <paramref name="area"/>'s center (desktop coordinates, physical pixels),
    /// until the returned watch is disposed. Null when no monitor holds it (one that was disconnected).
    /// </summary>
    public IScreenWatch? Start(Rect area)
    {
        bool first;
        lock (_sync)
        {
            first = _watchers == 0 && !_lingering;
            _watchers++;
            _linger?.Dispose();
            _linger = null;
            _lingering = false;
        }
        // PuppyMacro's windows leave the capture before it starts, so its first frames do not show them.
        if (first)
            ActiveChanged?.Invoke(true);
        lock (_sync)
        {
            if (_monitors.Count == 0)
                _monitors.AddRange(MonitorCapture.All(this));
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            var monitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(center)) ?? _monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(area));
            if (monitor != null)
            {
                monitor.Start();
                return new Watch(this, monitor);
            }
            // No monitor there: as if the watch ended at once.
            if (--_watchers == 0)
            {
                _lingering = true;
                _linger = new Timer(_ => Close(), null, LingerMs, Timeout.Infinite);
            }
            return null;
        }
    }

    private bool _lingering;

    private void Release(MonitorCapture monitor)
    {
        lock (_sync)
        {
            monitor.Stop();
            if (--_watchers > 0)
                return;
            // Every capture keeps running a moment: a macro that checks again soon (a loop) finds the image ready.
            _lingering = true;
            _linger = new Timer(_ => Close(), null, LingerMs, Timeout.Infinite);
        }
    }

    private void Close()
    {
        lock (_sync)
        {
            if (_watchers > 0 || !_lingering)
                return;
            _lingering = false;
            _linger?.Dispose();
            _linger = null;
            // Monitors may change before the next watch: they are taken again then.
            foreach (var m in _monitors)
                m.Dispose();
            foreach (var gpu in _monitors.Select(m => m.Gpu).Distinct())
                gpu.Dispose();
            _monitors.Clear();
        }
        ActiveChanged?.Invoke(false);
    }

    /// <summary>A waiter is set at every new frame of any monitor until it is removed.</summary>
    public void Listen(AutoResetEvent waiter)
    {
        lock (_listeners)
            _listeners.Add(waiter);
    }

    public void Unlisten(AutoResetEvent waiter)
    {
        lock (_listeners)
            _listeners.Remove(waiter);
    }

    private void NewFrame()
    {
        lock (_listeners)
            foreach (var waiter in _listeners)
                waiter.Set();
    }

    /// <summary>The monitors' bounds now (desktop coordinates, physical pixels), for the pickers and the checks.</summary>
    public static List<Rect> MonitorBounds() => MonitorCapture.Outputs().Select(o => o.Bounds).ToList();

    // ================= One monitor =================

    private sealed class MonitorCapture : IDisposable
    {
        private readonly ScreenCapture _owner;
        private readonly string _deviceName;
        private Thread? _thread;
        private int _users;
        private volatile bool _stop;
        private IDXGIOutput5? _output;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _copy;
        private ID3D11ShaderResourceView? _view;
        private DXGI_FORMAT _format;
        private uint _width, _height;

        private MonitorCapture(ScreenCapture owner, string deviceName, Rect bounds, GpuMatcher gpu, bool software)
        {
            _owner = owner;
            _deviceName = deviceName;
            Bounds = bounds;
            Gpu = gpu;
            Software = software;
        }

        public Rect Bounds { get; }
        public GpuMatcher Gpu { get; }

        /// <summary>The monitor's card is the software renderer (no graphics card, a virtual machine).</summary>
        public bool Software { get; }

        public long Version { get; private set; }
        public bool ScRgb => _format == DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;
        public float SdrWhite { get; private set; } = 80;
        public ID3D11ShaderResourceView? Frame => _view;

        /// <summary>Every monitor on the desktop with a device of its own card.</summary>
        public static IEnumerable<MonitorCapture> All(ScreenCapture owner)
        {
            var devices = new Dictionary<long, GpuMatcher>();
            foreach (var output in Outputs())
            {
                long key = ((long)output.Adapter.HighPart << 32) | output.Adapter.LowPart;
                if (!devices.TryGetValue(key, out var gpu))
                {
                    try
                    {
                        PInvoke.D3D11CreateDevice(output.AdapterObject, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null, 0, null,
                            PInvoke.D3D11_SDK_VERSION, out ID3D11Device device, out _, out ID3D11DeviceContext context).ThrowOnFailure();
                        devices[key] = gpu = GpuMatcher.On(output.CardName, device, context);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        continue;
                    }
                }
                yield return new MonitorCapture(owner, output.DeviceName, output.Bounds, gpu, output.Software);
            }
        }

        public sealed record OutputInfo(string DeviceName, Rect Bounds, LUID Adapter, IDXGIAdapter1 AdapterObject, string CardName, bool Software);

        public static List<OutputInfo> Outputs()
        {
            var list = new List<OutputInfo>();
            Guid factoryId = typeof(IDXGIFactory1).GUID;
            if (PInvoke.CreateDXGIFactory1(&factoryId, out object factoryObject).Failed)
                return list;
            var factory = (IDXGIFactory1)factoryObject;
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1? adapter = null;
                try { factory.EnumAdapters1(a, out adapter); } catch (COMException) { break; }
                if (adapter == null)
                    break;
                var card = adapter.GetDesc1();
                bool software = ((uint)card.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                for (uint o = 0; ; o++)
                {
                    IDXGIOutput? output = null;
                    try { adapter.EnumOutputs(o, out output); } catch (COMException) { break; }
                    if (output == null)
                        break;
                    var d = output.GetDesc();
                    if (!d.AttachedToDesktop)
                        continue;
                    var r = d.DesktopCoordinates;
                    list.Add(new OutputInfo(d.DeviceName.ToString(), new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top),
                        card.AdapterLuid, adapter, card.Description.ToString(), software));
                }
            }
            return list;
        }

        public void Start()
        {
            _users++;
            if (_thread != null)
                return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "PuppyMacro screen", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>One user less; the capture keeps running until <see cref="Dispose"/>.</summary>
        public void Stop() => _users--;

        private void Run()
        {
            while (!_stop)
            {
                if (_duplication == null && !Open())
                {
                    Thread.Sleep(250);
                    continue;
                }
                IDXGIResource? resource = null;
                DXGI_OUTDUPL_FRAME_INFO info = default;
                try
                {
                    _duplication!.AcquireNextFrame(100, out info, out resource);
                }
                catch (COMException e) when (e.HResult == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT: nothing changed
                {
                    continue;
                }
                catch (Exception)
                {
                    // Access lost (a display mode or HDR change) or refused: start again.
                    Close();
                    continue;
                }
                try
                {
                    // A frame that only moved the mouse pointer has no new image (the first one can even be empty).
                    if (info.LastPresentTime != 0 && resource is ID3D11Texture2D texture)
                        Keep(texture);
                }
                finally
                {
                    if (resource != null)
                        Marshal.ReleaseComObject(resource);
                    try { _duplication?.ReleaseFrame(); } catch (COMException) { }
                }
            }
        }

        /// <summary>Copies the new frame into the texture the conditions read, under the card's lock.</summary>
        private void Keep(ID3D11Texture2D texture)
        {
            texture.GetDesc(out D3D11_TEXTURE2D_DESC d);
            lock (Gpu.Sync)
            {
                if (_copy == null || d.Format != _format || d.Width != _width || d.Height != _height)
                {
                    ReleaseCopy();
                    var copy = d;
                    copy.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
                    copy.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
                    copy.CPUAccessFlags = 0;
                    copy.MiscFlags = 0;
                    copy.MipLevels = 1;
                    copy.ArraySize = 1;
                    Gpu.Device.CreateTexture2D(copy, null, out _copy);
                    ID3D11ShaderResourceView_unmanaged* view;
                    Gpu.Device.CreateShaderResourceView(_copy, null, &view);
                    _view = GpuMatcher.Take<ID3D11ShaderResourceView>(view);
                    _format = d.Format;
                    _width = d.Width;
                    _height = d.Height;
                }
                Gpu.Context.CopyResource(_copy, texture);
                Version++;
            }
            _owner.NewFrame();
        }

        /// <summary>
        /// Starts the duplication: 16-bit floats are asked first, so an HDR screen keeps its colors (turned to SDR colors by
        /// <see cref="GpuMatcher.Convert"/>); Windows gives 8 bits when it prefers. False while Windows refuses.
        /// </summary>
        private bool Open()
        {
            try
            {
                var output = Outputs().FirstOrDefault(o => o.DeviceName == _deviceName);
                if (output == null)
                    return false;
                for (uint o = 0; ; o++)
                {
                    IDXGIOutput? candidate = null;
                    try { output.AdapterObject.EnumOutputs(o, out candidate); } catch (COMException) { break; }
                    if (candidate == null)
                        break;
                    if (candidate.GetDesc().DeviceName.ToString() == _deviceName)
                    {
                        _output = (IDXGIOutput5)candidate;
                        break;
                    }
                }
                if (_output == null)
                    return false;
                ReadOnlySpan<DXGI_FORMAT> formats = stackalloc[] { DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM };
                lock (Gpu.Sync)
                    _output.DuplicateOutput1(Gpu.Device, 0, formats, out _duplication);
                SdrWhite = SdrWhiteNits(_deviceName);
                return true;
            }
            catch (Exception)
            {
                _duplication = null;
                return false;
            }
        }

        /// <summary>Windows' HDR ("Use HDR") is on for this monitor: asked to Windows, not guessed from the frames' format.</summary>
        public bool Hdr
        {
            get
            {
                try
                {
                    return _output is IDXGIOutput6 o6 && o6.GetDesc1().ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                }
                catch (COMException)
                {
                    return false;
                }
            }
        }

        private void ReleaseCopy()
        {
            if (_view != null)
                Marshal.ReleaseComObject(_view);
            if (_copy != null)
                Marshal.ReleaseComObject(_copy);
            _view = null;
            _copy = null;
        }

        private void Close()
        {
            lock (Gpu.Sync)
            {
                if (_duplication != null)
                    Marshal.ReleaseComObject(_duplication);
                _duplication = null;
                _output = null;
                ReleaseCopy();
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(2000);
            _thread = null;
            Close();
        }

        /// <summary>Windows' "SDR content brightness" of a monitor in nits (80 = the standard, also without HDR).</summary>
        private static float SdrWhiteNits(string deviceName)
        {
            var flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
            if (PInvoke.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != WIN32_ERROR.ERROR_SUCCESS)
                return 80;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes) != WIN32_ERROR.ERROR_SUCCESS)
                return 80;
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0 || source.viewGdiDeviceName.ToString() != deviceName)
                    continue;
                var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                white.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                white.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL);
                white.header.adapterId = path.targetInfo.adapterId;
                white.header.id = path.targetInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo(&white.header) == 0 && white.SDRWhiteLevel > 0)
                    return white.SDRWhiteLevel / 1000f * 80f;
            }
            return 80;
        }
    }

    // ================= A condition's watch =================

    /// <summary>One condition looking at one monitor, until disposed.</summary>
    private sealed class Watch : IScreenWatch
    {
        private readonly ScreenCapture _owner;
        private readonly MonitorCapture _monitor;
        private bool _disposed;

        internal Watch(ScreenCapture owner, MonitorCapture monitor)
        {
            _owner = owner;
            _monitor = monitor;
        }

        public Rect Bounds => _monitor.Bounds;
        public GpuMatcher? Gpu => _monitor.Gpu;

        /// <summary>The monitor's card is the software renderer: the graphics card would be slower than the processor.</summary>
        public bool Software => _monitor.Software;

        /// <summary>Changes every time the monitor shows a new image; 0 until the first one.</summary>
        public long Version => _monitor.Version;

        /// <summary>Windows' HDR is on for this monitor.</summary>
        public bool Hdr => _monitor.Hdr;

        /// <summary>
        /// A part of the last frame (desktop coordinates, clipped to the monitor) as a picture on the card, in SDR colors.
        /// Null before the first frame. Disposed by the caller.
        /// </summary>
        public GpuMatcher.GpuBuffer? Picture(Rect area)
        {
            Rect part = area.Intersect(Bounds);
            if (part.Width <= 0 || part.Height <= 0)
                return null;
            var gpu = _monitor.Gpu;
            lock (gpu.Sync)
            {
                if (_monitor.Frame is not { } frame)
                    return null;
                return gpu.Convert(frame, _monitor.ScRgb, _monitor.SdrWhite, new Rect(part.X - Bounds.X, part.Y - Bounds.Y, part.Width, part.Height));
            }
        }

        public Mat? Copy(Rect area)
        {
            using var picture = Picture(area);
            return picture == null ? null : _monitor.Gpu.Download(picture);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Release(_monitor);
        }
    }
}
