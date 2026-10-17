using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// Image search on a graphics card: the same normalized correlation as <see cref="CpuMatcher"/> (TM_CCOEFF_NORMED on
/// color), in compute shaders, coarse-to-fine as the image's <see cref="Calibration"/> says. The coarse scores stay on
/// the card: only the best of each 16 × 16 block comes back. Not thread safe on its own: every call takes its lock.
/// </summary>
internal sealed unsafe class GpuMatcher : IDisposable
{
    private const string Shaders = """
        cbuffer P : register(b0) {
          uint IW; uint IH; uint TW; uint TH;
          uint OX; uint OY; uint OW; uint OH;
          float TN; float N; uint F; uint SCRGB;
          float WHITE; uint ROT; uint DW; uint DH;
        };
        Texture2D<float4> Src : register(t0);
        StructuredBuffer<uint> Img : register(t1);
        StructuredBuffer<float4> Tpl : register(t2);
        StructuredBuffer<float> Scores : register(t3);
        StructuredBuffer<float> Small : register(t4);
        StructuredBuffer<float> TplG : register(t5);
        RWStructuredBuffer<uint> Dst : register(u0);
        RWStructuredBuffer<float> Out : register(u1);
        RWStructuredBuffer<float2> Tiles : register(u2);
        RWStructuredBuffer<float> Gray : register(u3);

        float3 unpackBgr(uint p) { return float3(p & 255, (p >> 8) & 255, (p >> 16) & 255); }
        uint packBgr(float3 c) { c = clamp(round(c), 0, 255); return (uint)c.x | ((uint)c.y << 8) | ((uint)c.z << 16) | (255u << 24); }
        float3 srgb(float3 l) { l = saturate(l); return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1 / 2.4) - 0.055; }

        // A captured frame (BGRA8, or scRGB in 16-bit floats) to packed BGR in the sRGB of SDR content, from OX, OY.
        [numthreads(16,16,1)]
        void Convert(uint3 id : SV_DispatchThreadID) {
          if (id.x >= OW || id.y >= OH) return;
          // The frame is the monitor's image before Windows rotates it (DXGI_MODE_ROTATION: 2 = 90, 3 = 180, 4 = 270 degrees
          // clockwise): the desktop point dx, dy of a monitor DW x DH is read where it is in the frame.
          uint dx = OX + id.x, dy = OY + id.y, tx = dx, ty = dy;
          if (ROT == 2) { tx = dy; ty = DW - 1 - dx; }
          else if (ROT == 3) { tx = DW - 1 - dx; ty = DH - 1 - dy; }
          else if (ROT == 4) { tx = DH - 1 - dy; ty = dx; }
          float4 v = Src.Load(int3(tx, ty, 0));
          float3 rgb = SCRGB ? srgb(v.rgb * (80.0 / WHITE)) : v.rgb;
          Dst[id.y * OW + id.x] = packBgr(rgb.bgr * 255);
        }

        // Area average by F: Img (IW x IH) to Dst (OW x OH).
        [numthreads(16,16,1)]
        void Down(uint3 id : SV_DispatchThreadID) {
          if (id.x >= OW || id.y >= OH) return;
          float3 s = 0;
          for (uint y = 0; y < F; y++) for (uint x = 0; x < F; x++) s += unpackBgr(Img[(id.y * F + y) * IW + id.x * F + x]);
          Dst[id.y * OW + id.x] = packBgr(s / (F * F));
        }

        // Area average by F to gray: Img (IW x IH) to Gray (OW x OH).
        [numthreads(16,16,1)]
        void DownGray(uint3 id : SV_DispatchThreadID) {
          if (id.x >= OW || id.y >= OH) return;
          float3 s = 0;
          for (uint y = 0; y < F; y++) for (uint x = 0; x < F; x++) s += unpackBgr(Img[(id.y * F + y) * IW + id.x * F + x]);
          s /= F * F;
          Gray[id.y * OW + id.x] = dot(s, float3(0.114, 0.587, 0.299));
        }

        // The correlation of the zero-mean template at every position OX + x, OY + y (x < OW, y < OH). A flat window
        // (no variation) matches nothing.
        [numthreads(16,16,1)]
        void Match(uint3 id : SV_DispatchThreadID) {
          if (id.x >= OW || id.y >= OH) return;
          float3 s = 0, si = 0, sii = 0;
          for (uint y = 0; y < TH; y++) {
            uint row = (OY + id.y + y) * IW + OX + id.x; uint trow = y * TW;
            for (uint x = 0; x < TW; x++) {
              float3 v = unpackBgr(Img[row + x]); float4 t = Tpl[trow + x];
              s += v * t.xyz; si += v * t.w; sii += v * v * t.w;
            }
          }
          float var = dot(sii - si * si / N, 1);
          Out[id.y * OW + id.x] = var > N * 0.5 ? dot(s, 1) / sqrt(var * TN) : -2;
        }

        // The same on gray, for the coarse step.
        [numthreads(16,16,1)]
        void MatchGray(uint3 id : SV_DispatchThreadID) {
          if (id.x >= OW || id.y >= OH) return;
          float s = 0, si = 0, sii = 0;
          for (uint y = 0; y < TH; y++) {
            uint row = (OY + id.y + y) * IW + OX + id.x; uint trow = y * TW;
            for (uint x = 0; x < TW; x++) { float v = Small[row + x]; s += v * TplG[trow + x]; si += v; sii += v * v; }
          }
          float var = sii - si * si / N;
          Out[id.y * OW + id.x] = var > N * 0.5 ? s / sqrt(var * TN) : -2;
        }

        // The best score of each 16 x 16 block of Scores (OW x OH) and where it is: Tiles[block] = (score, index). On a
        // tie the first in reading order stays: the highest, then the leftmost.
        groupshared float gs[256];
        groupshared uint gi[256];
        [numthreads(16,16,1)]
        void TileMax(uint3 id : SV_DispatchThreadID, uint3 g : SV_GroupID, uint t : SV_GroupIndex) {
          bool inside = id.x < OW && id.y < OH;
          uint index = id.y * OW + id.x;
          gs[t] = inside ? Scores[index] : -3; gi[t] = inside ? index : 0xFFFFFFFF;
          GroupMemoryBarrierWithGroupSync();
          for (uint step = 128; step > 0; step >>= 1) {
            if (t < step && (gs[t + step] > gs[t] || (gs[t + step] == gs[t] && gi[t + step] < gi[t]))) { gs[t] = gs[t + step]; gi[t] = gi[t + step]; }
            GroupMemoryBarrierWithGroupSync();
          }
          if (t == 0) Tiles[g.y * ((OW + 15) / 16) + g.x] = float2(gs[0], asfloat(gi[0]));
        }
        """;

    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _convert, _down, _downGray, _match, _matchGray, _tileMax;
    private readonly ID3D11Buffer _parameters;
    private readonly Dictionary<(string Role, int Count, int Stride), GpuBuffer> _pool = new();
    private readonly Dictionary<int, ID3D11Buffer> _staging = new();
    private readonly Dictionary<Mat, Prepared> _prepared = new(ReferenceEqualityComparer.Instance);

    private GpuMatcher(string name, ID3D11Device device, ID3D11DeviceContext context)
    {
        Name = name;
        _device = device;
        _context = context;
        _convert = Compile("Convert");
        _down = Compile("Down");
        _downGray = Compile("DownGray");
        _match = Compile("Match");
        _matchGray = Compile("MatchGray");
        _tileMax = Compile("TileMax");
        var d = new D3D11_BUFFER_DESC { ByteWidth = 64, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER };
        ID3D11Buffer_unmanaged* p;
        _device.CreateBuffer(&d, null, &p);
        _parameters = Take<ID3D11Buffer>(p);
    }

    /// <summary>The graphics card's name ("NVIDIA GeForce RTX 5080", "WARP").</summary>
    public string Name { get; }

    /// <summary>The device, for <see cref="ScreenCapture"/>: the card a monitor is connected to.</summary>
    internal ID3D11Device Device => _device;

    internal ID3D11DeviceContext Context => _context;

    /// <summary>The lock of the device's context: every use of the card holds it (the capture's copy too).</summary>
    internal object Sync => _sync;

    /// <summary>On a device that exists already (a monitor's capture).</summary>
    internal static GpuMatcher On(string name, ID3D11Device device, ID3D11DeviceContext context)
    {
        try
        {
            return new GpuMatcher(name, device, context);
        }
        catch
        {
            // Not made: its device is not kept by anyone.
            Release(context);
            Release(device);
            throw;
        }
    }

    /// <summary>On the software renderer (no graphics card): the tests, and machines without one.</summary>
    public static GpuMatcher Warp()
    {
        PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null, 0, null, PInvoke.D3D11_SDK_VERSION,
            out ID3D11Device device, out _, out ID3D11DeviceContext context).ThrowOnFailure();
        return new GpuMatcher("WARP", device, context);
    }

    internal static T Take<T>(void* unknown)
    {
        var o = (T)Marshal.GetObjectForIUnknown((nint)unknown);
        Marshal.Release((nint)unknown);
        return o;
    }

    private ID3D11ComputeShader Compile(string entry)
    {
        byte[] source = Encoding.ASCII.GetBytes(Shaders), name = Encoding.ASCII.GetBytes(entry + "\0"), target = Encoding.ASCII.GetBytes("cs_5_0\0");
        ID3DBlob code, errors;
        fixed (byte* s = source) fixed (byte* e = name) fixed (byte* t = target)
        {
            HRESULT hr = PInvoke.D3DCompile(s, (nuint)source.Length, default, null, null, new PCSTR(e), new PCSTR(t), 1u << 15, 0, out code, out errors);
            if (hr.Failed)
            {
                string message = errors == null ? hr.ToString() : Marshal.PtrToStringAnsi((nint)errors.GetBufferPointer()) ?? "";
                Release(code);
                Release(errors);
                throw new InvalidOperationException($"Shader {entry}: " + message);
            }
        }
        try
        {
            ID3D11ComputeShader_unmanaged* p;
            _device.CreateComputeShader(code.GetBufferPointer(), code.GetBufferSize(), null, &p);
            return Take<ID3D11ComputeShader>(p);
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    // ================= Buffers =================

    /// <summary>A structured buffer on the card, readable and writable by the shaders; an image when W and H are set.</summary>
    internal sealed class GpuBuffer : IDisposable
    {
        public required ID3D11Buffer Buffer { get; init; }
        public required ID3D11ShaderResourceView Read { get; init; }
        public required ID3D11UnorderedAccessView Write { get; init; }
        public required int Count { get; init; }
        public required int Stride { get; init; }
        public int W { get; set; }
        public int H { get; set; }

        /// <summary>A template's pixels that count (all of them; fewer with a mask).</summary>
        public int Kept { get; set; }

        public void Dispose()
        {
            Marshal.ReleaseComObject(Read);
            Marshal.ReleaseComObject(Write);
            Marshal.ReleaseComObject(Buffer);
        }
    }

    private GpuBuffer Create(int count, int stride, void* data = null)
    {
        var d = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(count * stride),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            StructureByteStride = (uint)stride,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
        };
        var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
        ID3D11Buffer_unmanaged* b;
        _device.CreateBuffer(&d, data == null ? null : &initial, &b);
        var buffer = Take<ID3D11Buffer>(b);
        var rd = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER };
        rd.Anonymous.Buffer.Anonymous2.NumElements = (uint)count;
        ID3D11ShaderResourceView_unmanaged* r;
        _device.CreateShaderResourceView(buffer, &rd, &r);
        var wd = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER };
        wd.Anonymous.Buffer.NumElements = (uint)count;
        ID3D11UnorderedAccessView_unmanaged* w;
        _device.CreateUnorderedAccessView(buffer, &wd, &w);
        return new GpuBuffer { Buffer = buffer, Read = Take<ID3D11ShaderResourceView>(r), Write = Take<ID3D11UnorderedAccessView>(w), Count = count, Stride = stride };
    }

    /// <summary>A buffer of this role and size kept for the next calls (scores, blocks, shrunk images).</summary>
    private const int MaxPooled = 48;

    private GpuBuffer Pooled(string role, int count, int stride)
    {
        if (!_pool.TryGetValue((role, count, stride), out var buffer))
            _pool[(role, count, stride)] = buffer = Create(count, stride);
        return buffer;
    }

    /// <summary>
    /// One buffer per size: areas of many sizes (images moving near the edges) would keep them all, so past a few dozen
    /// they all go and are made again as needed. Only before a search, when none of them is in use.
    /// </summary>
    private void TrimPool()
    {
        if (_pool.Count < MaxPooled)
            return;
        foreach (var old in _pool.Values)
            old.Dispose();
        _pool.Clear();
        foreach (var staging in _staging.Values)
            Marshal.ReleaseComObject(staging);
        _staging.Clear();
    }

    private T[] ReadBack<T>(GpuBuffer buffer, int count) where T : unmanaged
    {
        int size = buffer.Count * buffer.Stride, bytes = count * sizeof(T);
        if (!_staging.TryGetValue(size, out var staging))
        {
            var d = new D3D11_BUFFER_DESC { ByteWidth = (uint)size, Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ };
            ID3D11Buffer_unmanaged* p;
            _device.CreateBuffer(&d, null, &p);
            _staging[size] = staging = Take<ID3D11Buffer>(p);
        }
        _context.CopyResource(staging, buffer.Buffer);
        D3D11_MAPPED_SUBRESOURCE mapped;
        _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        var result = new T[count];
        fixed (T* r = result)
            System.Buffer.MemoryCopy(mapped.pData, r, bytes, bytes);
        _context.Unmap(staging, 0);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public uint IW, IH, TW, TH, OX, OY, OW, OH;
        public float TN, N;
        public uint F, ScRgb;
        public float White;

        /// <summary>The monitor's rotation (DXGI_MODE_ROTATION) and its size on the desktop, for Convert.</summary>
        public uint Rot, DW, DH;
    }

    private void Set(Parameters p)
    {
        _context.UpdateSubresource(_parameters, 0, null, &p, 0, 0);
        _context.CSSetConstantBuffers(0, 1, new[] { _parameters });
    }

    private static uint Groups(int n) => (uint)((n + 15) / 16);

    private void Run(ID3D11ComputeShader shader, int width, int height, ID3D11ShaderResourceView?[] reads, int readSlot,
        ID3D11UnorderedAccessView write, int writeSlot)
    {
        _context.CSSetShader(shader, null, 0);
        _context.CSSetShaderResources((uint)readSlot, (uint)reads.Length, reads!);
        _context.CSSetUnorderedAccessViews((uint)writeSlot, 1, new[] { write }, null);
        _context.Dispatch(Groups(width), Groups(height), 1);
        _context.CSSetShaderResources((uint)readSlot, (uint)reads.Length, new ID3D11ShaderResourceView[reads.Length]);
        _context.CSSetUnorderedAccessViews((uint)writeSlot, 1, new ID3D11UnorderedAccessView[1], null);
    }

    // ================= Images =================

    /// <summary>A picture (BGR) on the card. Disposed by the caller.</summary>
    public GpuBuffer Upload(Mat bgr)
    {
        lock (_sync)
        {
            using Mat bgra = bgr.CvtColor(ColorConversionCodes.BGR2BGRA);
            using Mat continuous = bgra.IsContinuous() ? bgra : bgra.Clone();
            var image = Create(continuous.Width * continuous.Height, 4, (void*)continuous.Data);
            image.W = continuous.Width;
            image.H = continuous.Height;
            return image;
        }
    }

    /// <summary>
    /// A BGR picture as a captured frame would be on the card (a BGRA8 texture and its view), for the unit tests of
    /// <see cref="Convert"/>. The caller releases both (<see cref="Release"/>).
    /// </summary>
    internal (ID3D11Texture2D Texture, ID3D11ShaderResourceView View) Frame(Mat bgr)
    {
        lock (_sync)
        {
            using Mat bgra = bgr.CvtColor(ColorConversionCodes.BGR2BGRA);
            using Mat continuous = bgra.IsContinuous() ? bgra : bgra.Clone();
            var d = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)continuous.Width,
                Height = (uint)continuous.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = (void*)continuous.Data, SysMemPitch = (uint)(continuous.Width * 4) };
            ID3D11Texture2D_unmanaged* made;
            _device.CreateTexture2D(&d, &initial, &made);
            var texture = Take<ID3D11Texture2D>(made);
            ID3D11ShaderResourceView_unmanaged* view;
            _device.CreateShaderResourceView(texture, null, &view);
            return (texture, Take<ID3D11ShaderResourceView>(view));
        }
    }

    /// <summary>A picture on the card back as BGR.</summary>
    public Mat Download(GpuBuffer image)
    {
        lock (_sync)
        {
            uint[] pixels = ReadBack<uint>(image, image.W * image.H);
            using var bgra = new Mat(image.H, image.W, MatType.CV_8UC4);
            long bytes = pixels.Length * 4L;
            fixed (uint* source = pixels)
                System.Buffer.MemoryCopy(source, (void*)bgra.Data, bytes, bytes);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
    }

    /// <summary>
    /// A part of a captured frame as a picture on the card, in the colors of SDR content: a frame in 16-bit floats (scRGB,
    /// also when Windows' HDR is on) is turned to sRGB with Windows' SDR white level, so images and colors taken in one
    /// mode match in the other.
    /// </summary>
    internal GpuBuffer Convert(ID3D11ShaderResourceView frame, bool scRgb, float sdrWhiteNits, Rect part, uint rotation = 1, int monitorWidth = 0, int monitorHeight = 0)
    {
        lock (_sync)
        {
            var image = Create(part.Width * part.Height, 4);
            image.W = part.Width;
            image.H = part.Height;
            Set(new Parameters
            {
                OX = (uint)part.X, OY = (uint)part.Y, OW = (uint)part.Width, OH = (uint)part.Height, ScRgb = scRgb ? 1u : 0, White = sdrWhiteNits,
                Rot = rotation, DW = (uint)monitorWidth, DH = (uint)monitorHeight,
            });
            _context.CSSetShader(_convert, null, 0);
            _context.CSSetShaderResources(0, 1, new[] { frame });
            _context.CSSetUnorderedAccessViews(0, 1, new[] { image.Write }, null);
            _context.Dispatch(Groups(part.Width), Groups(part.Height), 1);
            _context.CSSetShaderResources(0, 1, new ID3D11ShaderResourceView[1]);
            _context.CSSetUnorderedAccessViews(0, 1, new ID3D11UnorderedAccessView[1], null);
            return image;
        }
    }

    // ================= Search =================

    /// <summary>An image prepared once for the searches: the full-size template and the coarse one, on the card.</summary>
    private sealed class Prepared : IDisposable
    {
        public required Calibration Calibration { get; init; }
        public required GpuBuffer Full { get; init; }
        public required float FullNorm { get; init; }
        public GpuBuffer? Coarse { get; init; }
        public float CoarseNorm { get; init; }
        public int CoarseW { get; init; }
        public int CoarseH { get; init; }

        public void Dispose()
        {
            Full.Dispose();
            Coarse?.Dispose();
        }
    }

    /// <summary>The zero-mean template (color) and its sum of squares.</summary>
    private (GpuBuffer Buffer, float Norm) Template(Mat bgr)
    {
        int width = bgr.Width, height = bgr.Height, n = width * height;
        var values = new float[n * 4];
        var mean = Cv2.Mean(bgr);
        double norm = 0;
        using Mat continuous = bgr.Clone();
        continuous.GetArray(out Vec3b[] pixels);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                int i = (y * width + x) * 4;
                values[i] = (float)(c.Item0 - mean.Val0);
                values[i + 1] = (float)(c.Item1 - mean.Val1);
                values[i + 2] = (float)(c.Item2 - mean.Val2);
                values[i + 3] = 1;
                norm += values[i] * values[i] + values[i + 1] * values[i + 1] + values[i + 2] * values[i + 2];
            }
        }
        fixed (float* p = values)
        {
            var buffer = Create(n, 16, p);
            buffer.W = width;
            buffer.H = height;
            buffer.Kept = n;
            return (buffer, (float)norm);
        }
    }

    /// <summary>The zero-mean gray template and its sum of squares.</summary>
    private (GpuBuffer Buffer, float Norm) GrayTemplate(Mat gray)
    {
        int width = gray.Width, height = gray.Height, n = width * height;
        var values = new float[n];
        double mean = Cv2.Mean(gray).Val0, norm = 0;
        using Mat continuous = gray.Clone();
        continuous.GetArray(out byte[] pixels);
        for (int i = 0; i < n; i++)
        {
            float v = (float)(pixels[i] - mean);
            values[i] = v;
            norm += v * v;
        }
        fixed (float* p = values)
        {
            var buffer = Create(n, 4, p);
            buffer.W = width;
            buffer.H = height;
            buffer.Kept = n;
            return (buffer, (float)norm);
        }
    }

    private Prepared Prepare(Mat image, Calibration calibration)
    {
        var (full, fullNorm) = Template(image);
        int f = calibration.Factor;
        if (f <= 1)
            return new Prepared { Calibration = calibration, Full = full, FullNorm = fullNorm };
        using Mat shrunk = image.Resize(new Size(image.Width / f, image.Height / f), 0, 0, InterpolationFlags.Area);
        GpuBuffer coarse;
        float coarseNorm;
        if (calibration.Gray)
        {
            using Mat gray = shrunk.CvtColor(ColorConversionCodes.BGR2GRAY);
            (coarse, coarseNorm) = GrayTemplate(gray);
        }
        else
        {
            (coarse, coarseNorm) = Template(shrunk);
        }
        return new Prepared
        {
            Calibration = calibration, Full = full, FullNorm = fullNorm,
            Coarse = coarse, CoarseNorm = coarseNorm, CoarseW = shrunk.Width, CoarseH = shrunk.Height,
        };
    }

    /// <summary>Lets go of an image prepared by <see cref="Find"/> (a macro that stopped).</summary>
    public void Forget(Mat image)
    {
        lock (_sync)
        {
            if (_prepared.Remove(image, out var prepared))
                prepared.Dispose();
        }
    }

    /// <summary>
    /// Searches <paramref name="image"/> in <paramref name="area"/> of <paramref name="picture"/> (all of it when null). The
    /// image is prepared on the first search and kept, by reference, until <see cref="Forget"/>.
    /// </summary>
    public ImageMatch Find(GpuBuffer picture, Mat image, Calibration calibration, double need, Rect? area = null)
    {
        lock (_sync)
        {
            TrimPool();
            if (!_prepared.TryGetValue(image, out var prepared))
                _prepared[image] = prepared = Prepare(image, calibration);
            Rect a = (area ?? new Rect(0, 0, picture.W, picture.H)).Intersect(new Rect(0, 0, picture.W, picture.H));
            if (a.Width < image.Width || a.Height < image.Height)
                return ImageMatch.None;
            if (prepared.Coarse == null)
                return Best(FullBlocks(picture, prepared, a.X, a.Y, a.Width - image.Width + 1, a.Height - image.Height + 1));

            int f = prepared.Calibration.Factor;
            int sx = a.X / f, sy = a.Y / f;
            int sw = a.Width / f - prepared.CoarseW + 1, sh = a.Height / f - prepared.CoarseH + 1;
            if (sw <= 0 || sh <= 0)
                return ImageMatch.None;
            GpuBuffer source = CoarseSource(picture, prepared);
            long coarseWork = (long)prepared.CoarseW * prepared.CoarseH;
            // The blocks' best above the coarse threshold, best first, apart from each other by half the image.
            var candidates = new List<(int X, int Y)>();
            foreach (var block in Banded(sw, sh, coarseWork, (y0, rows) => CoarseScores(source, prepared, sx, sy + y0, sw, rows))
                         .Where(b => b.Score >= calibration.CoarseMin).OrderByDescending(b => b.Score))
            {
                if (candidates.Count >= CpuMatcher.MaxCandidates)
                    break;
                if (candidates.Any(c => Math.Abs(c.X - block.X) < prepared.CoarseW / 2 && Math.Abs(c.Y - block.Y) < prepared.CoarseH / 2))
                    continue;
                candidates.Add((block.X, block.Y));
            }
            var best = ImageMatch.None;
            foreach (var c in candidates)
            {
                int m = f * 2;
                int x0 = Math.Max(a.X, (sx + c.X) * f - m), y0 = Math.Max(a.Y, (sy + c.Y) * f - m);
                int x1 = Math.Min(a.Right, (sx + c.X) * f + image.Width + m), y1 = Math.Min(a.Bottom, (sy + c.Y) * f + image.Height + m);
                int ow = x1 - x0 - image.Width + 1, oh = y1 - y0 - image.Height + 1;
                if (ow <= 0 || oh <= 0)
                    continue;
                best = ImageMatch.Best(best, Best(FullBlocks(picture, prepared, x0, y0, ow, oh)));
            }
            return best;
        }
    }

    /// <summary>
    /// The most work one dispatch may do (positions × template pixels). A long dispatch keeps the graphics card busy for
    /// everything else and, after about 2 seconds, Windows resets its driver (TDR; several in a row can stop Windows with a
    /// blue screen): a search over a large area with a large template runs in bands of rows, each one read back before the
    /// next starts.
    /// </summary>
    public const double MaxWorkPerDispatch = 5e7;

    /// <summary>The best of every 16 × 16 block of a score map made band by band: <paramref name="run"/> scores rows y0 to y0 + rows.</summary>
    private List<(float Score, int X, int Y)> Banded(int width, int height, long workPerPosition, Func<int, int, GpuBuffer> run)
    {
        int rows = (int)Math.Clamp(MaxWorkPerDispatch / Math.Max(1.0, (double)width * workPerPosition), 1, height);
        var all = new List<(float, int, int)>();
        for (int y0 = 0; y0 < height; y0 += rows)
        {
            int band = Math.Min(rows, height - y0);
            foreach (var (score, x, y) in Blocks(run(y0, band), width, band))
                all.Add((score, x, y + y0));
        }
        return all;
    }

    /// <summary>The full-size scores at ox + x, oy + y (x &lt; ow, y &lt; oh), band by band, as block bests in picture coordinates.</summary>
    private List<(float Score, int X, int Y)> FullBlocks(GpuBuffer picture, Prepared prepared, int ox, int oy, int ow, int oh) =>
        Banded(ow, oh, (long)prepared.Full.W * prepared.Full.H, (y0, rows) => Scores(picture, prepared.Full, prepared.FullNorm, ox, oy + y0, ow, rows))
            .Select(b => (b.Score, b.X + ox, b.Y + oy)).ToList();

    private static ImageMatch Best(List<(float Score, int X, int Y)> blocks)
    {
        var best = ImageMatch.None;
        foreach (var (score, x, y) in blocks)
            if (score > -1.5)
                best = ImageMatch.Best(best, new ImageMatch(x, y, score));
        return best;
    }

    private GpuBuffer Scores(GpuBuffer picture, GpuBuffer template, float norm, int ox, int oy, int ow, int oh)
    {
        var scores = Pooled("scores", ow * oh, 4);
        Set(new Parameters
        {
            IW = (uint)picture.W, IH = (uint)picture.H, TW = (uint)template.W, TH = (uint)template.H,
            OX = (uint)ox, OY = (uint)oy, OW = (uint)ow, OH = (uint)oh, TN = norm, N = template.Kept,
        });
        Run(_match, ow, oh, new[] { picture.Read, template.Read }, 1, scores.Write, 1);
        return scores;
    }

    /// <summary>The picture shrunk by the calibration's factor, in gray or in color: made once per search.</summary>
    private GpuBuffer CoarseSource(GpuBuffer picture, Prepared prepared)
    {
        int f = prepared.Calibration.Factor, gw = picture.W / f, gh = picture.H / f;
        var shrunk = Pooled(prepared.Calibration.Gray ? "gray" : "small", gw * gh, 4);
        shrunk.W = gw;
        shrunk.H = gh;
        Set(new Parameters { IW = (uint)picture.W, IH = (uint)picture.H, OW = (uint)gw, OH = (uint)gh, F = (uint)f });
        if (prepared.Calibration.Gray)
            Run(_downGray, gw, gh, new[] { picture.Read }, 1, shrunk.Write, 3);
        else
            Run(_down, gw, gh, new[] { picture.Read }, 1, shrunk.Write, 0);
        return shrunk;
    }

    /// <summary>The coarse scores at sx + x, sy + y (x &lt; sw, y &lt; sh) on <see cref="CoarseSource"/>'s picture.</summary>
    private GpuBuffer CoarseScores(GpuBuffer source, Prepared prepared, int sx, int sy, int sw, int sh)
    {
        var scores = Pooled("coarse scores", sw * sh, 4);
        Set(new Parameters
        {
            IW = (uint)source.W, IH = (uint)source.H, TW = (uint)prepared.CoarseW, TH = (uint)prepared.CoarseH,
            OX = (uint)sx, OY = (uint)sy, OW = (uint)sw, OH = (uint)sh, TN = prepared.CoarseNorm, N = prepared.Coarse!.Kept,
        });
        if (prepared.Calibration.Gray)
            Run(_matchGray, sw, sh, new[] { source.Read, prepared.Coarse.Read }, 4, scores.Write, 1);
        else
            Run(_match, sw, sh, new[] { source.Read, prepared.Coarse.Read }, 1, scores.Write, 1);
        return scores;
    }

    /// <summary>The best score of every 16 × 16 block of a score map and its place: a few hundred values come back.</summary>
    private List<(float Score, int X, int Y)> Blocks(GpuBuffer scores, int width, int height)
    {
        int bw = (width + 15) / 16, bh = (height + 15) / 16;
        var blocks = Pooled("blocks", bw * bh, 8);
        Set(new Parameters { OW = (uint)width, OH = (uint)height });
        _context.CSSetShader(_tileMax, null, 0);
        _context.CSSetShaderResources(3, 1, new[] { scores.Read });
        _context.CSSetUnorderedAccessViews(2, 1, new[] { blocks.Write }, null);
        _context.Dispatch((uint)bw, (uint)bh, 1);
        _context.CSSetShaderResources(3, 1, new ID3D11ShaderResourceView[1]);
        _context.CSSetUnorderedAccessViews(2, 1, new ID3D11UnorderedAccessView[1], null);
        float[] raw = ReadBack<float>(blocks, bw * bh * 2);
        var result = new List<(float, int, int)>(bw * bh);
        for (int i = 0; i < bw * bh; i++)
        {
            int index = BitConverter.SingleToInt32Bits(raw[i * 2 + 1]);
            if (index >= 0 && index < width * height)
                result.Add((raw[i * 2], index % width, index / width));
        }
        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var prepared in _prepared.Values)
                prepared.Dispose();
            _prepared.Clear();
            foreach (var buffer in _pool.Values)
                buffer.Dispose();
            _pool.Clear();
            foreach (var staging in _staging.Values)
                Marshal.ReleaseComObject(staging);
            _staging.Clear();
            // The matcher owns its device: released now, not when the garbage collector gets to it.
            try
            {
                _context.ClearState();
                _context.Flush();
            }
            catch (COMException)
            {
            }
            foreach (object com in new object[] { _convert, _down, _downGray, _match, _matchGray, _tileMax, _parameters, _context, _device })
                Release(com);
        }
    }

    /// <summary>Releases a COM object now.</summary>
    internal static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }
}
