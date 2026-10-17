using System.Runtime.InteropServices;

namespace Moflex.Gui;

/// <summary>
/// Plays 16-bit PCM through the Windows waveOut API (no extra dependencies). On other systems
/// <see cref="IsSupported"/> is false and the preview stays silent.
/// </summary>
internal sealed class WaveOutPlayer : IDisposable
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private readonly byte[] _pcm;
    private readonly int _rate, _channels;
    private IntPtr _device;
    private GCHandle _data;
    private IntPtr _header;
    private readonly object _gate = new();

    public WaveOutPlayer(byte[] interleavedPcm16, int sampleRate, int channels)
    {
        _pcm = interleavedPcm16; _rate = sampleRate; _channels = channels;
    }

    public double Duration => _pcm.Length / (2.0 * _channels * _rate);

    /// <summary>Starts playing from <paramref name="seconds"/> (stops any earlier playback).</summary>
    public void Play(double seconds)
    {
        lock (_gate) PlayLocked(seconds);
    }

    private void PlayLocked(double seconds)
    {
        StopLocked();
        long offset = (long)Math.Max(0, seconds * _rate) * _channels * 2;
        if (offset >= _pcm.Length) return;
        var fmt = new WaveFormat
        {
            Tag = 1, Channels = (short)_channels, SampleRate = _rate, AvgBytesPerSec = _rate * _channels * 2,
            BlockAlign = (short)(_channels * 2), BitsPerSample = 16,
        };
        if (waveOutOpen(out _device, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { _device = IntPtr.Zero; return; }
        _data = GCHandle.Alloc(_pcm, GCHandleType.Pinned);
        var hdr = new WaveHeader { Data = _data.AddrOfPinnedObject() + (nint)offset, BufferLength = (int)(_pcm.Length - offset) };
        _header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(hdr, _header, false);
        int size = Marshal.SizeOf<WaveHeader>();
        if (waveOutPrepareHeader(_device, _header, size) != 0 || waveOutWrite(_device, _header, size) != 0) StopLocked();
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            if (_header != IntPtr.Zero) waveOutUnprepareHeader(_device, _header, Marshal.SizeOf<WaveHeader>());
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
        if (_data.IsAllocated) _data.Free();
    }

    public void Dispose() => Stop();

    private const int WaveMapper = -1;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short Tag, Channels;
        public int SampleRate, AvgBytesPerSec;
        public short BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength, BytesRecorded;
        public IntPtr User;
        public int Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
}
