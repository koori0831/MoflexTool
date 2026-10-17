namespace Moflex.Core.Video;

/// <summary>
/// Mobiclip video encoder. Currently produces intra (key) frames only: every macroblock uses an explicit
/// 16x16 prediction mode (DC / vertical / horizontal / diagonal modes that only read already-decoded pixels),
/// 8x8 residual blocks, closed-loop reconstruction shared with the decoder.
/// </summary>
public sealed class MobiclipEncoder
{
    private readonly int _w, _h;
    private readonly MobiclipDecoder _dec;
    private int _q;

    /// <summary>Rate-distortion trade-off multiplier (higher = smaller files).</summary>
    public double LambdaScale { get; set; } = 0.1;
    /// <summary>Dead-zone applied when rounding coefficients (0 = plain rounding).</summary>
    public double DeadZone { get; set; } = 0.15;

    public MobiclipEncoder(int width, int height, int quantizer = 30)
    {
        _w = width; _h = height;
        _dec = new MobiclipDecoder(width, height);
        Quantizer = quantizer;
    }

    /// <summary>Quantiser for key frames (12..63).</summary>
    public int Quantizer
    {
        get => _q;
        set { if (value < 12 || value > 63) throw new ArgumentOutOfRangeException(nameof(value)); _q = value; }
    }

    // ---------- forward transform ----------

    private static readonly double[][] Minv4 = BuildInverse(4), Minv8 = BuildInverse(8);

    private static double[][] BuildInverse(int n)
    {
        var m = new double[n][];
        for (int i = 0; i < n; i++) m[i] = new double[n];
        for (int k = 0; k < n; k++)
        {
            var v = new int[8];
            v[k] = 4096;
            MobiclipDecoder.Idct(v, n);
            for (int x = 0; x < n; x++) m[x][k] = v[x] / 4096.0;
        }
        // Gauss-Jordan inverse
        var a = new double[n][];
        for (int i = 0; i < n; i++) { a[i] = new double[2 * n]; Array.Copy(m[i], a[i], n); a[i][n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r][c]) > Math.Abs(a[piv][c])) piv = r;
            (a[c], a[piv]) = (a[piv], a[c]);
            double d = a[c][c];
            for (int j = 0; j < 2 * n; j++) a[c][j] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = a[r][c];
                if (f != 0) for (int j = 0; j < 2 * n; j++) a[r][j] -= f * a[c][j];
            }
        }
        var inv = new double[n][];
        for (int i = 0; i < n; i++) { inv[i] = new double[n]; Array.Copy(a[i], n, inv[i], 0, n); }
        return inv;
    }

    /// <summary>Transforms a spatial residual (row-major) into quantised levels in zigzag order. Returns null if all zero.</summary>
    private int[]? ForwardQuant(int[] resid, int size, int[] qtab)
    {
        var mi = size == 8 ? Minv8 : Minv4;
        var tmp = new double[size * size]; // tmp[k][b] = sum_a Minv[k][a] * R[b][a]
        for (int k = 0; k < size; k++)
            for (int b = 0; b < size; b++)
            {
                double s = 0;
                for (int a = 0; a < size; a++) s += mi[k][a] * resid[b * size + a];
                tmp[k * size + b] = s * 64.0;
            }
        var c = new double[size * size]; // C[k][j] = sum_b tmp[k][b] * Minv[j][b]
        for (int k = 0; k < size; k++)
            for (int j = 0; j < size; j++)
            {
                double s = 0;
                for (int b = 0; b < size; b++) s += tmp[k * size + b] * mi[j][b];
                c[k * size + j] = s;
            }
        var zt = MobiclipDecoder.ZigzagFor(size);
        var levels = new int[size * size];
        bool any = false;
        for (int p = 0; p < size * size; p++)
        {
            double v = c[zt[p]] / qtab[p];
            int l = (int)Math.Floor(Math.Abs(v) + 0.5 - DeadZone);
            if (l < 0) l = 0;
            if (l > 2047) l = 2047;
            if (v < 0) l = -l;
            levels[p] = l;
            if (l != 0) any = true;
        }
        return any ? levels : null;
    }

    // ---------- entropy coding ----------

    private static void WriteCoeffs(BitWriterMsb bw, int[] levels)
    {
        var vlc = MobiclipDecoder.RlVlc[0];
        var res = MobiclipTables.RunResidue;
        int last = -1;
        for (int p = 0; p < levels.Length; p++) if (levels[p] != 0) last = p;
        int prev = -1;
        for (int p = 0; p <= last; p++)
        {
            int l = levels[p];
            if (l == 0) continue;
            bool isLast = p == last;
            int run = p - prev - 1, a = Math.Abs(l);
            prev = p;
            int lb = isLast ? 1 << 11 : 0;
            int sym = lb | (run << 5) | a;
            if (a <= 31 && vlc.Codes.ContainsKey(sym)) { bw.Code(vlc, sym); bw.Bit(l < 0 ? 1 : 0); continue; }
            int r1 = res[(isLast ? 64 : 0) + run];
            if (a <= 31 + r1 && r1 > 0 && a - r1 >= 1 && a - r1 <= 31 && vlc.Codes.ContainsKey(lb | (run << 5) | (a - r1)))
            {
                bw.Code(vlc, 0); bw.Bit(0); bw.Code(vlc, lb | (run << 5) | (a - r1)); bw.Bit(l < 0 ? 1 : 0); continue;
            }
            bool done = false;
            if (a <= 31)
            {
                int r2 = res[128 + (isLast ? 64 : 0) + a];
                int baseRun = run - r2;
                if (r2 > 0 && baseRun >= 0 && vlc.Codes.ContainsKey(lb | (baseRun << 5) | a))
                {
                    bw.Code(vlc, 0); bw.Bit(1); bw.Bit(0); bw.Code(vlc, lb | (baseRun << 5) | a); bw.Bit(l < 0 ? 1 : 0); done = true;
                }
            }
            if (done) continue;
            bw.Code(vlc, 0); bw.Bit(1); bw.Bit(1);
            bw.Bit(isLast ? 1 : 0); bw.Bits(run, 6); bw.Bits(l & 0xFFF, 12);
        }
    }

    // ---------- macroblock coding ----------

    private sealed class Plan
    {
        public double Cost;
        public int Mode;
        public int[]?[] Levels = Array.Empty<int[]?>();
    }

    private static bool ModeValid(int mode, int ax, int ay) => mode switch
    {
        3 => true,
        0 => ay > 0,
        1 or 4 => ax > 0,
        _ => ax > 0 && ay > 0, // 5,6,7
    };

    private static readonly int[] CandidateModes = { 3, 0, 1, 4, 5, 6, 7 };

    private int[]? CodeBlock(byte[][] frame, byte[] src, int stride, int bx, int by, int size, int plane, int mode)
    {
        _dec.PredictIntra(frame, bx, by, mode, false, size, plane);
        var recon = frame[plane];
        var resid = new int[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                resid[y * size + x] = src[(by + y) * stride + bx + x] - recon[(by + y) * stride + bx + x];
        var qtab = _dec.QuantTable(size);
        var levels = ForwardQuant(resid, size, qtab);
        if (levels != null)
        {
            Span<int> mat = stackalloc int[64];
            mat.Clear();
            var zt = MobiclipDecoder.ZigzagFor(size);
            for (int p = 0; p < size * size; p++) mat[zt[p]] = qtab[p] * levels[p];
            MobiclipDecoder.Reconstruct(mat, recon, stride, bx, by, size);
        }
        return levels;
    }

    private double Sse(byte[] a, byte[] b, int stride, int x0, int y0, int w, int h)
    {
        double s = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) { int d = a[y * stride + x] - b[y * stride + x]; s += d * d; }
        return s;
    }

    private Plan PlanLuma(byte[][] frame, YuvFrame src, int x, int y, int mode, double lambda)
    {
        var plan = new Plan { Mode = mode, Levels = new int[]?[4] };
        double bits = 0;
        int i = 0;
        foreach (var (bx, by) in new[] { (x, y), (x + 8, y), (x, y + 8), (x + 8, y + 8) })
        {
            plan.Levels[i] = CodeBlock(frame, src.Y, _w, bx, by, 8, 0, mode);
            if (plan.Levels[i] is { } lv)
            {
                var bw = new BitWriterMsb();
                bw.UE(0); WriteCoeffs(bw, lv);
                bits += bw.BitCount;
            }
            i++;
        }
        plan.Cost = Sse(src.Y, frame[0], _w, x, y, 16, 16) + lambda * bits;
        return plan;
    }

    private Plan PlanChroma(byte[][] frame, YuvFrame src, int x, int y, int mode, double lambda)
    {
        var plan = new Plan { Mode = mode, Levels = new int[]?[2] };
        double bits = 0, sse = 0;
        int cw = _w >> 1;
        for (int k = 0; k < 2; k++)
        {
            var sp = k == 0 ? src.U : src.V;
            plan.Levels[k] = CodeBlock(frame, sp, cw, x >> 1, y >> 1, 8, 1 + k, mode);
            if (plan.Levels[k] is { } lv)
            {
                var bw = new BitWriterMsb();
                bw.UE(0); WriteCoeffs(bw, lv);
                bits += bw.BitCount;
            }
            sse += Sse(sp, frame[1 + k], cw, x >> 1, y >> 1, 8, 8);
        }
        plan.Cost = sse + lambda * bits;
        return plan;
    }

    private void EncodeMacroblock(BitWriterMsb bw, byte[][] frame, YuvFrame src, int x, int y, double lambda)
    {
        Plan? best = null;
        foreach (int m in CandidateModes)
        {
            if (!ModeValid(m, x, y)) continue;
            var p = PlanLuma(frame, src, x, y, m, lambda);
            if (best == null || p.Cost < best.Cost) best = p;
        }
        var luma = PlanLuma(frame, src, x, y, best!.Mode, lambda); // leaves the winning reconstruction in place
        Plan? bestC = null;
        foreach (int m in CandidateModes)
        {
            if (!ModeValid(m, x >> 1, y >> 1)) continue;
            var p = PlanChroma(frame, src, x, y, m, lambda);
            if (bestC == null || p.Cost < bestC.Cost) bestC = p;
        }
        var chroma = PlanChroma(frame, src, x, y, bestC!.Mode, lambda);

        int flags = 0;
        for (int i = 0; i < 4; i++) if (luma.Levels[i] != null) flags |= 1 << i;
        for (int i = 0; i < 2; i++) if (chroma.Levels[i] != null) flags |= 1 << (4 + i);
        int idx = Array.IndexOf(MobiclipTables.Block8x8CoefficientsTab, (byte)flags);

        bw.Bit(0);
        bw.UE(idx);
        bw.Bits(luma.Mode, 3);
        for (int i = 0; i < 4; i++)
            if (luma.Levels[i] is { } lv) { bw.UE(0); WriteCoeffs(bw, lv); }
        bw.Bits(chroma.Mode, 3);
        for (int i = 0; i < 2; i++)
            if (chroma.Levels[i] is { } lv) { bw.UE(0); WriteCoeffs(bw, lv); }
    }

    /// <summary>Encodes one key frame and returns the packet; <paramref name="recon"/> is what a decoder will output.</summary>
    public byte[] EncodeKeyFrame(YuvFrame src, out YuvFrame recon)
    {
        if (src.Width != _w || src.Height != _h) throw new ArgumentException("Frame size mismatch.");
        var bw = new BitWriterMsb();
        bw.Bit(1); bw.Bit(1); bw.Bit(0); bw.Bits(_q, 6);
        _dec.SetupQ(_q);
        _dec.SetMoflex(true);
        var frame = _dec.CurrentFrame;
        double qs = _dec.QuantTable(8)[0] / 8.0;
        double lambda = LambdaScale * qs * qs;
        for (int y = 0; y < _h; y += 16)
            for (int x = 0; x < _w; x += 16)
                EncodeMacroblock(bw, frame, src, x, y, lambda);
        recon = new YuvFrame { Width = _w, Height = _h, IsKey = true, Y = (byte[])frame[0].Clone(), U = (byte[])frame[1].Clone(), V = (byte[])frame[2].Clone() };
        _dec.AdvanceFrame();
        return bw.ToPacket();
    }
}
