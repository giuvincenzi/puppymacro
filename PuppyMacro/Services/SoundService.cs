using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Native;

namespace PuppyMacro.Services;

/// <summary>
/// Plays the built-in loop sounds (Assets\Sounds\*-on.wav / *-off.wav) at the configured volume.
/// Must be used on the UI thread. One sound plays at a time; a new sound replaces the current one.
/// </summary>
internal sealed class SoundService
{
    // The first one is the default. New sounds go at the end, so the order users know stays the same.
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Chime", "Blip", "Pop", "Bell", "Arcade", "Soft",
        "Marimba", "Pluck", "Harp", "Kalimba", "Xylophone", "Glock", "Organ", "Flute", "Synth", "Pad", "Brass", "Bass",
        "Click", "Tick", "Knock", "Switch", "Tom", "Conga", "Cowbell", "Triangle", "Gong",
        "Coin", "Jump", "Laser", "Zap", "Powerup", "Retro", "Fanfare", "Twinkle", "Sparkle",
        "Bubble", "Drop", "Whoosh", "Whistle", "Chirp", "Cricket",
        "Doorbell", "Phone", "Alarm", "Modem", "Radar", "Sonar", "Echo", "Robot", "Siren", "Hum", "Ripple", "Wobble", "Warp", "Ufo",
    };

    private sealed class Clip
    {
        public required byte[] Header;      // everything before the samples
        public required short[] Samples;    // 16-bit PCM
        public required int BytesPerSecond;
    }

    private readonly Dictionary<string, Clip> _clips = new();
    private readonly Dictionary<string, byte[]> _scaled = new();
    private readonly Dispatcher _dispatcher;
    private GCHandle _playing;
    private int _volume = 70;

    public SoundService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Volume in percent (0 = silent).</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (clamped == _volume)
                return;
            _volume = clamped;
            _scaled.Clear();
        }
    }

    /// <summary>Plays the start ("in") or stop ("out") version of a sound.</summary>
    public void Play(string name, bool start)
    {
        if (_volume == 0)
            return;
        try
        {
            byte[]? wav = GetScaled(FileKey(name, start));
            if (wav == null)
                return;

            var handle = GCHandle.Alloc(wav, GCHandleType.Pinned);
            NativeMethods.PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero,
                NativeMethods.SND_MEMORY | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT);

            // The new sound replaced the previous one, so its buffer can be released now.
            if (_playing.IsAllocated)
                _playing.Free();
            _playing = handle;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sound failed: {ex.Message}");
        }
    }

    /// <summary>Plays the start version, then the stop version right after it.</summary>
    public void Preview(string name)
    {
        Play(name, start: true);
        if (!_clips.TryGetValue(FileKey(name, true), out var clip))
            return;

        double seconds = clip.Samples.Length * 2.0 / clip.BytesPerSecond;
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(seconds + 0.15),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Play(name, start: false);
        };
        timer.Start();
    }

    private static string FileKey(string name, bool start) =>
        $"{name.ToLowerInvariant()}-{(start ? "on" : "off")}";

    private byte[]? GetScaled(string key)
    {
        if (_scaled.TryGetValue(key, out var cached))
            return cached;

        Clip? clip = Load(key);
        if (clip == null)
            return null;

        // Perceived loudness is roughly logarithmic: a squared curve feels more even on the slider.
        double gain = Math.Pow(_volume / 100.0, 2);
        var bytes = new byte[clip.Header.Length + clip.Samples.Length * 2];
        Buffer.BlockCopy(clip.Header, 0, bytes, 0, clip.Header.Length);
        int offset = clip.Header.Length;
        foreach (short sample in clip.Samples)
        {
            short scaled = (short)Math.Clamp(Math.Round(sample * gain), short.MinValue, short.MaxValue);
            bytes[offset++] = (byte)(scaled & 0xFF);
            bytes[offset++] = (byte)((scaled >> 8) & 0xFF);
        }
        _scaled[key] = bytes;
        return bytes;
    }

    private Clip? Load(string key)
    {
        if (_clips.TryGetValue(key, out var existing))
            return existing;

        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Sounds/{key}.wav"));
        if (info == null)
            return null;

        using var stream = info.Stream;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] file = memory.ToArray();

        // Minimal RIFF/WAVE parser: finds "fmt " (16-bit PCM expected) and "data".
        int position = 12;
        int bytesPerSecond = 88200;
        while (position + 8 <= file.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(file, position, 4);
            int chunkSize = BitConverter.ToInt32(file, position + 4);
            if (chunkId == "fmt ")
            {
                bytesPerSecond = BitConverter.ToInt32(file, position + 16);
            }
            else if (chunkId == "data")
            {
                int dataStart = position + 8;
                int dataLength = Math.Min(chunkSize, file.Length - dataStart);
                var samples = new short[dataLength / 2];
                Buffer.BlockCopy(file, dataStart, samples, 0, samples.Length * 2);
                var header = new byte[dataStart];
                Buffer.BlockCopy(file, 0, header, 0, dataStart);
                var clip = new Clip { Header = header, Samples = samples, BytesPerSecond = bytesPerSecond };
                _clips[key] = clip;
                return clip;
            }
            position += 8 + chunkSize + (chunkSize & 1);
        }
        return null;
    }
}
