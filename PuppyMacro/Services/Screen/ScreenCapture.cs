using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>The capture gave up (errors in a row) or its monitor changed: a new watch gets a new capture.</summary>
    bool Failed { get; }
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

    /// <summary>
    /// Captures that gave up while watches still used them: replaced in <see cref="_monitors"/>, released with their last
    /// watch (<see cref="Release"/>).
    /// </summary>
    private readonly List<MonitorCapture> _retired = new();
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
        // Also while a capture lingers after the last condition: it still runs.
        get { lock (_sync) return _watchers > 0 || _lingering; }
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
            MonitorCapture? monitor = null;
            try
            {
                Mta(() =>
                {
                    RefreshMonitors();
                    return 0;
                });
                var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                monitor = _monitors.FirstOrDefault(m => m.Bounds.Contains(center)) ?? _monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(area));
                monitor?.Start();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
            {
                // Logged, and as if no monitor were there: the count of watches must come back down.
                CaptureLog.Write($"could not start the capture: {ex.Message}");
                monitor = null;
            }
            if (monitor != null)
                return new Watch(this, monitor);
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

    /// <summary>
    /// The monitors as they are now, under <see cref="_sync"/>: a capture that changed (its monitor's place or size in the
    /// desktop) or went away is released once no watch uses it, and a new or changed monitor gets a capture of its own. One
    /// in use and working keeps running as it is until it is free: Windows lets a process duplicate a monitor only once.
    /// A capture that gave up is replaced at once, also while watches use it: it duplicates nothing any more (its thread
    /// ended and its duplication is closed), so the new one can duplicate the monitor, and the watches still on it, which
    /// see it failed, never keep the monitor from coming back. The new capture is made only here, when a watch starts (a
    /// condition tries again at most every <see cref="ConditionChecker.RestartMs"/>), and waits between its own tries
    /// (<see cref="CaptureRetry"/>): never a duplication made again and again without a pause.
    /// </summary>
    private void RefreshMonitors()
    {
        var now = MonitorCapture.Outputs();
        foreach (var monitor in _monitors.ToList())
        {
            bool same = now.Any(o => o.DeviceName == monitor.DeviceName && o.Bounds == monitor.Bounds);
            if ((same || monitor.InUse) && monitor.Failure == null)
                continue;
            _monitors.Remove(monitor);
            if (monitor.InUse)
                _retired.Add(monitor);
            else
                monitor.Dispose();
        }
        foreach (var output in now.Where(o => !_monitors.Any(m => m.DeviceName == o.DeviceName)))
        {
            if (MonitorCapture.For(this, output) is { } monitor)
                _monitors.Add(monitor);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a multithreaded-apartment thread. The capture's Direct3D and DXGI objects are created
    /// and used only there: they cannot be marshaled between apartments, and a call from the UI thread (a single-threaded
    /// apartment) to one made on a capture thread, or the other way round, crashes the process.
    /// </summary>
    internal static T Mta<T>(Func<T> work) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? Task.Run(work).GetAwaiter().GetResult() : work();

    /// <summary>Fails clearly instead of crashing: the capture's objects are used only from MTA threads (<see cref="Mta{T}"/>).</summary>
    internal static void RequireMta()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            throw new InvalidOperationException("The screen capture's graphics card objects are used only from MTA threads.");
    }

    private void Release(MonitorCapture monitor)
    {
        lock (_sync)
        {
            monitor.Stop();
            if (!monitor.InUse && _retired.Remove(monitor))
                monitor.Dispose();
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
            // Each monitor releases its device and its DXGI objects.
            foreach (var m in _monitors.Concat(_retired))
                m.Dispose();
            _monitors.Clear();
            _retired.Clear();
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

    /// <summary>
    /// The capture of one monitor, on its own thread with its own Direct3D device, made from the adapter the monitor is
    /// connected to (Microsoft's Desktop Duplication sample keeps one device per output thread; DuplicateOutput needs the
    /// device of the output's own adapter). Every use of the device, the duplication's too, is under the device's lock.
    /// After an error it waits longer each time, and gives up after <see cref="CaptureRetry.MaxFailures"/> errors in a row:
    /// a duplication made again and again can exhaust the system's memory (reported with NVIDIA's drivers).
    /// </summary>
    private sealed class MonitorCapture : IDisposable
    {
        /// <summary>How long one AcquireNextFrame waits, holding the device's lock.</summary>
        private const uint AcquireMs = 10;

        private readonly ScreenCapture _owner;
        private readonly string _deviceName;
        private readonly IDXGIFactory1 _factory;
        private readonly IDXGIAdapter1 _adapter;
        private readonly IDXGIOutput5 _output;
        private readonly CaptureRetry _retry = new();
        private Thread? _thread;
        private int _users;
        private volatile bool _stop;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _copy;
        private ID3D11ShaderResourceView? _view;
        private DXGI_FORMAT _format;
        private uint _width, _height;

        private MonitorCapture(ScreenCapture owner, string deviceName, Rect bounds, bool software,
            IDXGIFactory1 factory, IDXGIAdapter1 adapter, IDXGIOutput5 output, GpuMatcher gpu)
        {
            _owner = owner;
            _deviceName = deviceName;
            Bounds = bounds;
            Software = software;
            _factory = factory;
            _adapter = adapter;
            _output = output;
            Gpu = gpu;
        }

        public Rect Bounds { get; }
        public GpuMatcher Gpu { get; }

        /// <summary>The monitor's card is the software renderer (no graphics card, a virtual machine).</summary>
        public bool Software { get; }

        public long Version { get; private set; }
        public bool ScRgb => _format == DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;
        public float SdrWhite { get; private set; } = 80;

        /// <summary>How Windows rotates the monitor's image (DXGI_MODE_ROTATION; 1 = not rotated).</summary>
        public uint Rotation { get; private set; } = 1;
        public ID3D11ShaderResourceView? Frame => _view;

        /// <summary>Why the capture stopped trying (logged too), or null while it works or tries.</summary>
        public string? Failure { get; private set; }

        /// <summary>Every monitor on the desktop, each with a device of its own. A monitor that can't get one is logged and left out.</summary>
        /// <summary>A capture of one monitor with a device of its own; null (logged) when it can't get one.</summary>
        public static MonitorCapture? For(ScreenCapture owner, OutputInfo info)
        {
            try
            {
                return Create(owner, info);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
            {
                CaptureLog.Write($"{info.DeviceName}: no device on {info.CardName}: {Describe(ex)}");
                return null;
            }
        }

        /// <summary>The monitor's name in Windows ("\\.\DISPLAY1").</summary>
        public string DeviceName => _deviceName;

        /// <summary>A condition, a picker or the bench looks at it.</summary>
        public bool InUse => _users > 0;

        /// <summary>The monitor is still where it was when its capture was made (a display change moves or resizes it).</summary>
        private bool StillThere()
        {
            try
            {
                var r = _output.GetDesc().DesktopCoordinates;
                return r.left == Bounds.X && r.top == Bounds.Y && r.right - r.left == Bounds.Width && r.bottom - r.top == Bounds.Height;
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>The monitor's adapter and output, from one factory of its own, and a device made from that adapter.</summary>
        private static MonitorCapture? Create(ScreenCapture owner, OutputInfo info)
        {
            Guid factoryId = typeof(IDXGIFactory1).GUID;
            PInvoke.CreateDXGIFactory1(&factoryId, out object factoryObject).ThrowOnFailure();
            var factory = (IDXGIFactory1)factoryObject;
            IDXGIAdapter1? keptAdapter = null;
            IDXGIOutput5? keptOutput = null;
            try
            {
                for (uint a = 0; keptOutput == null; a++)
                {
                    IDXGIAdapter1? adapter = null;
                    try { factory.EnumAdapters1(a, out adapter); } catch (COMException) { break; }
                    if (adapter == null)
                        break;
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput? output = null;
                        try { adapter.EnumOutputs(o, out output); } catch (COMException) { break; }
                        if (output == null)
                            break;
                        if (keptOutput == null && output.GetDesc().DeviceName.ToString() == info.DeviceName && output is IDXGIOutput5 output5)
                        {
                            keptOutput = output5;
                            keptAdapter = adapter;
                            continue;
                        }
                        Free(output);
                    }
                    if (keptAdapter != adapter)
                        Free(adapter);
                }
                if (keptAdapter == null || keptOutput == null)
                    throw new InvalidOperationException("the monitor is not there any more, or Windows is older than 10 version 1703");
                PInvoke.D3D11CreateDevice(keptAdapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null, 0, null,
                    PInvoke.D3D11_SDK_VERSION, out ID3D11Device device, out _, out ID3D11DeviceContext context).ThrowOnFailure();
                var gpu = GpuMatcher.On(info.CardName, device, context);
                return new MonitorCapture(owner, info.DeviceName, info.Bounds, info.Software, factory, keptAdapter, keptOutput, gpu);
            }
            catch
            {
                Free(keptOutput);
                Free(keptAdapter);
                Free(factory);
                throw;
            }
        }

        /// <summary>A monitor as DXGI lists it; nothing of DXGI is kept.</summary>
        public sealed record OutputInfo(string DeviceName, Rect Bounds, string CardName, bool Software);

        /// <summary>The monitors attached to the desktop. Every DXGI object it opens is released before it returns.</summary>
        public static List<OutputInfo> Outputs()
        {
            var list = new List<OutputInfo>();
            Guid factoryId = typeof(IDXGIFactory1).GUID;
            if (PInvoke.CreateDXGIFactory1(&factoryId, out object factoryObject).Failed)
                return list;
            var factory = (IDXGIFactory1)factoryObject;
            try
            {
                for (uint a = 0; ; a++)
                {
                    IDXGIAdapter1? adapter = null;
                    try { factory.EnumAdapters1(a, out adapter); } catch (COMException) { break; }
                    if (adapter == null)
                        break;
                    try
                    {
                        var card = adapter.GetDesc1();
                        bool software = ((uint)card.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                        for (uint o = 0; ; o++)
                        {
                            IDXGIOutput? output = null;
                            try { adapter.EnumOutputs(o, out output); } catch (COMException) { break; }
                            if (output == null)
                                break;
                            try
                            {
                                var d = output.GetDesc();
                                if (!d.AttachedToDesktop)
                                    continue;
                                var r = d.DesktopCoordinates;
                                list.Add(new OutputInfo(d.DeviceName.ToString(), new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top),
                                    card.Description.ToString(), software));
                            }
                            finally
                            {
                                Free(output);
                            }
                        }
                    }
                    finally
                    {
                        Free(adapter);
                    }
                }
            }
            finally
            {
                Free(factory);
            }
            return list;
        }

        /// <summary>Releases a COM object now, not when the garbage collector gets to it.</summary>
        private static void Free(object? com)
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.FinalReleaseComObject(com);
        }

        private static string Describe(Exception ex) => ex is COMException com ? $"0x{com.HResult:X8} {com.Message.Trim()}" : ex.Message;

        public void Start()
        {
            _users++;
            if (_thread != null || Failure != null)
                return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "PuppyMacro screen", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>One user less; the capture keeps running until <see cref="Dispose"/>.</summary>
        public void Stop() => _users--;

        private void Run()
        {
            while (!_stop && Failure == null)
            {
                if (_duplication == null && Open() is { } refused)
                {
                    if (GiveUpAfter("start", refused))
                        return;
                    continue;
                }
                bool gotFrame = false, newImage = false;
                Exception? error = null;
                lock (Gpu.Sync)
                {
                    IDXGIResource? resource = null;
                    try
                    {
                        _duplication!.AcquireNextFrame(AcquireMs, out DXGI_OUTDUPL_FRAME_INFO info, out resource);
                        gotFrame = true;
                        // A frame that only moved the mouse pointer has no new image (the first one can even be empty).
                        if (info.LastPresentTime != 0 && resource is ID3D11Texture2D texture)
                            newImage = Keep(texture);
                    }
                    catch (COMException e) when (e.HResult == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT: nothing changed
                    {
                    }
                    catch (Exception e)
                    {
                        // Access lost (a display mode or HDR change, the lock screen) or another error.
                        error = e;
                    }
                    finally
                    {
                        Free(resource);
                        if (gotFrame)
                        {
                            try { _duplication?.ReleaseFrame(); }
                            catch (COMException e) { error ??= e; }
                        }
                    }
                }
                if (error != null)
                {
                    CloseDuplication();
                    if (GiveUpAfter("read", Describe(error)))
                        return;
                    continue;
                }
                _retry.Succeeded();
                if (newImage)
                    _owner.NewFrame();
                // The others using the card get the lock between two waits, also when the screen changes all the time (a
                // game): the lock is not fair, and this thread would take it back at once.
                Thread.Sleep(newImage ? 1 : 4);
            }
        }

        /// <summary>Logs an error and waits before the next try; true when it gave up (logged too).</summary>
        private bool GiveUpAfter(string what, string error)
        {
            int delay = _retry.Failed();
            CaptureLog.Write($"{_deviceName}: could not {what} the capture: {error} (error {_retry.Failures} in a row)");
            if (_retry.GaveUp)
            {
                Failure = error;
                CaptureLog.Write($"{_deviceName}: gave up after {_retry.Failures} errors in a row");
                return true;
            }
            for (int waited = 0; waited < delay && !_stop; waited += 50)
                Thread.Sleep(50);
            return false;
        }

        /// <summary>Copies the new frame into the texture the conditions read. Under the device's lock.</summary>
        private bool Keep(ID3D11Texture2D texture)
        {
            texture.GetDesc(out D3D11_TEXTURE2D_DESC d);
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
            return true;
        }

        /// <summary>
        /// Starts the duplication on the monitor's own output: 16-bit floats are asked first, so an HDR screen keeps its colors
        /// (turned to SDR colors by <see cref="GpuMatcher.Convert"/>); Windows gives 8 bits when it prefers. Null when it
        /// started, otherwise the error.
        /// </summary>
        private string? Open()
        {
            try
            {
                ReadOnlySpan<DXGI_FORMAT> formats = stackalloc[] { DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM };
                lock (Gpu.Sync)
                    _output.DuplicateOutput1(Gpu.Device, 0, formats, out _duplication);
                if (!StillThere())
                {
                    // The display changed: this capture's coordinates are old. It stops and is made again once free.
                    lock (Gpu.Sync)
                        Free(_duplication);
                    _duplication = null;
                    Failure = "the monitor moved, changed its size or is gone";
                    CaptureLog.Write($"{_deviceName}: {Failure}");
                    return Failure;
                }
                SdrWhite = SdrWhiteNits(_deviceName);
                _duplication!.GetDesc(out DXGI_OUTDUPL_DESC description);
                Rotation = (uint)description.Rotation;
                return null;
            }
            catch (Exception ex)
            {
                _duplication = null;
                return Describe(ex);
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
            Free(_view);
            Free(_copy);
            _view = null;
            _copy = null;
        }

        private void CloseDuplication()
        {
            lock (Gpu.Sync)
            {
                Free(_duplication);
                _duplication = null;
                ReleaseCopy();
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(2000);
            _thread = null;
            CloseDuplication();
            Gpu.Dispose();
            Free(_output);
            Free(_adapter);
            Free(_factory);
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

        public bool Failed => _monitor.Failure != null;

        /// <summary>
        /// A part of the last frame (desktop coordinates, clipped to the monitor) as a picture on the card, in SDR colors.
        /// Null before the first frame. Disposed by the caller.
        /// </summary>
        public GpuMatcher.GpuBuffer? Picture(Rect area)
        {
            RequireMta();
            Rect part = area.Intersect(Bounds);
            if (part.Width <= 0 || part.Height <= 0)
                return null;
            var gpu = _monitor.Gpu;
            lock (gpu.Sync)
            {
                if (_monitor.Frame is not { } frame)
                    return null;
                return gpu.Convert(frame, _monitor.ScRgb, _monitor.SdrWhite, new Rect(part.X - Bounds.X, part.Y - Bounds.Y, part.Width, part.Height),
                    _monitor.Rotation, Bounds.Width, Bounds.Height);
            }
        }

        public Mat? Copy(Rect area) => Mta(() =>
        {
            using var picture = Picture(area);
            return picture == null ? null : _monitor.Gpu.Download(picture);
        });

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Release(_monitor);
        }
    }
}
