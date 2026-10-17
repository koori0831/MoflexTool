using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Core.Pipeline;

/// <summary>Which picture of a stereoscopic 3D movie to output.</summary>
public enum EyeLayout
{
    /// <summary>Both eyes in one frame twice as wide, left eye on the left (the layout <c>encode --sbs</c> reads back).</summary>
    SideBySide,
    Left,
    Right,
}

/// <summary>How different the two eyes of a 3D movie are.</summary>
/// <param name="Pairs">Left/right pictures compared.</param>
/// <param name="DifferentPairs">Pairs whose eyes differ by more than <see cref="DifferentThreshold"/> (mean absolute luma difference).</param>
/// <param name="MaxDifference">Largest mean absolute luma difference of a pair.</param>
public sealed record StereoReport(int Pairs, int DifferentPairs, double MaxDifference)
{
    /// <summary>Above coding noise: the same picture coded twice stays well below this.</summary>
    public const double DifferentThreshold = 1.5;

    /// <summary>The eyes really differ (depth), rather than both showing the same flat picture.</summary>
    public bool IsReal3D => DifferentPairs > 0;

    public override string ToString() => IsReal3D
        ? $"real 3D: the eyes differ in {DifferentPairs} of {Pairs} pictures ({100.0 * DifferentPairs / Math.Max(1, Pairs):0.#} %)"
        : $"flat: 3D layout, but both eyes show the same picture in all {Pairs} pictures";
}

/// <summary>
/// Decodes the video stream of a MOFLEX file as displayed pictures: one picture per time stamp, with the two eyes
/// of a 3D movie combined side by side or one eye picked. Supports seeking (decoding restarts at the preceding key frame).
/// </summary>
public sealed class MoflexVideoReader
{
    private readonly List<Packet> _packets;
    private readonly int _eyeW;
    private MobiclipDecoder _dec;
    private int _next; // next packet to decode

    public MoflexVideoReader(MoflexFile file, EyeLayout eyes = EyeLayout.SideBySide)
    {
        var v = file.Streams.FirstOrDefault(x => x.Kind == StreamKind.Video) ?? throw new InvalidDataException("The file has no video stream.");
        _packets = file.Packets.Where(p => p.StreamIndex == v.Index).ToList();
        _eyeW = v.Width;
        Height = v.Height;
        IsStereo = v.DescriptorType == 3;
        Eyes = IsStereo ? eyes : EyeLayout.Left;
        Width = IsStereo && Eyes == EyeLayout.SideBySide ? v.Width * 2 : v.Width;
        FrameCount = IsStereo ? (_packets.Count + 1) / 2 : _packets.Count;
        // a stored frame lasts TimeBaseNum/TimeBaseDen s; the two eyes of a 3D picture share one time stamp
        long num = v.TimeBaseDen, den = (long)Math.Max(1, v.TimeBaseNum) * (IsStereo ? 2 : 1);
        long g = Gcd(num, den);
        FpsNum = num / g; FpsDen = den / g;
        _dec = new MobiclipDecoder(v.Width, v.Height);
    }

    /// <summary>The movie stores left/right frame pairs (stereoscopic 3D).</summary>
    public bool IsStereo { get; }
    public EyeLayout Eyes { get; }
    /// <summary>Size of the pictures returned by <see cref="Read"/>.</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>Pictures per second = FpsNum / FpsDen.</summary>
    public long FpsNum { get; }
    public long FpsDen { get; }
    public int FrameCount { get; }
    /// <summary>Index of the picture the next <see cref="Read"/> returns.</summary>
    public int Position => IsStereo ? (_next + 1) / 2 : _next;

    public double Seconds(int frame) => frame * (double)FpsDen / FpsNum;

    /// <summary>Decodes the next picture, or returns null at the end.</summary>
    public YuvFrame? Read()
    {
        if (_next >= _packets.Count) return null;
        if (!IsStereo) return _dec.Decode(_packets[_next++].Data);
        var left = _dec.Decode(_packets[_next++].Data);
        var right = _next < _packets.Count ? _dec.Decode(_packets[_next++].Data) : left;
        return Eyes switch
        {
            EyeLayout.Left => left,
            EyeLayout.Right => right,
            _ => SideBySide(left, right),
        };
    }

    /// <summary>Moves to picture <paramref name="frame"/>, decoding from the key frame before it.</summary>
    public void Seek(int frame)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1));
        int target = IsStereo ? frame * 2 : frame;
        if (target >= _next && target - _next < 64) { Skip(target); return; } // short hop forward: keep decoding
        int key = target;
        while (key > 0 && !_packets[key].IsKey) key--;
        _dec = new MobiclipDecoder(_eyeW, Height);
        _next = key;
        Skip(target);
    }

    private void Skip(int target)
    {
        while (_next < target) _dec.Decode(_packets[_next++].Data);
    }

    private static YuvFrame SideBySide(YuvFrame l, YuvFrame r)
    {
        int w = l.Width, h = l.Height, cw = w / 2, ch = h / 2;
        var f = new YuvFrame { Width = w * 2, Height = h, IsKey = l.IsKey, Y = new byte[w * h * 2], U = new byte[cw * ch * 2], V = new byte[cw * ch * 2] };
        for (int y = 0; y < h; y++)
        {
            Array.Copy(l.Y, y * w, f.Y, y * w * 2, w);
            Array.Copy(r.Y, y * w, f.Y, y * w * 2 + w, w);
        }
        for (int y = 0; y < ch; y++)
        {
            Array.Copy(l.U, y * cw, f.U, y * cw * 2, cw);
            Array.Copy(r.U, y * cw, f.U, y * cw * 2 + cw, cw);
            Array.Copy(l.V, y * cw, f.V, y * cw * 2, cw);
            Array.Copy(r.V, y * cw, f.V, y * cw * 2 + cw, cw);
        }
        return f;
    }

    /// <summary>
    /// Decodes every picture of a 3D movie and compares the two eyes. Returns null for a 2D movie.
    /// </summary>
    public static StereoReport? AnalyzeStereo(MoflexFile file, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var r = new MoflexVideoReader(file, EyeLayout.SideBySide);
        if (!r.IsStereo) return null;
        var dec = new MobiclipDecoder(r._eyeW, r.Height);
        int pairs = 0, differ = 0;
        double max = 0;
        for (int i = 0; i + 1 < r._packets.Count; i += 2)
        {
            ct.ThrowIfCancellationRequested();
            var l = dec.Decode(r._packets[i].Data);
            var rt = dec.Decode(r._packets[i + 1].Data);
            long s = 0;
            for (int k = 0; k < l.Y.Length; k++) s += Math.Abs(l.Y[k] - rt.Y[k]);
            double mad = s / (double)l.Y.Length;
            max = Math.Max(max, mad);
            if (mad > StereoReport.DifferentThreshold) differ++;
            pairs++;
            progress?.Invoke(pairs, r.FrameCount);
        }
        return new StereoReport(pairs, differ, max);
    }

    /// <summary>Converts a picture to 32-bit BGRA (BT.601, limited range), e.g. for display.</summary>
    public static void ToBgra(YuvFrame f, Span<byte> dst)
    {
        int w = f.Width, h = f.Height, cw = w / 2;
        for (int y = 0; y < h; y++)
        {
            int o = y * w * 4, yRow = y * w, cRow = (y / 2) * cw;
            for (int x = 0; x < w; x++)
            {
                int c = 298 * (f.Y[yRow + x] - 16), d = f.U[cRow + x / 2] - 128, e = f.V[cRow + x / 2] - 128;
                dst[o++] = Clip((c + 516 * d + 128) >> 8);
                dst[o++] = Clip((c - 100 * d - 208 * e + 128) >> 8);
                dst[o++] = Clip((c + 409 * e + 128) >> 8);
                dst[o++] = 255;
            }
        }
    }

    private static byte Clip(int v) => (byte)Math.Clamp(v, 0, 255);
    private static long Gcd(long x, long y) { while (y != 0) (x, y) = (y, x % y); return x == 0 ? 1 : x; }
}
