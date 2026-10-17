namespace Moflex.Core.Video;

/// <summary>
/// Mobiclip video encoder with closed-loop reconstruction shared with the decoder.
/// Key frames: explicit 16x16 intra modes that only read decoded pixels, 8x8 residual blocks.
/// Predicted frames: half-pel motion search against the previous picture, intra fallback, 8x8 residuals.
/// </summary>
public sealed class MobiclipEncoder
{
    private readonly int _w, _h;
    private readonly MobiclipDecoder _dec;
    private readonly (int x, int y)[] _mot;
    private int _q;

    /// <summary>Rate-distortion trade-off multiplier (higher = smaller files).</summary>
    public double LambdaScale { get; set; } = 0.1;
    /// <summary>Dead-zone applied when rounding coefficients (0 = plain rounding).</summary>
    public double DeadZone { get; set; } = 0.15;
    /// <summary>Residual-coding table used by key frames (0 or 1). Existing files use 1.</summary>
    public int KeyFrameTable { get; set; } = 1;
    /// <summary>Full-pel search radius for motion estimation.</summary>
    public int SearchRange { get; set; } = 8;

    public MobiclipEncoder(int width, int height, int quantizer = 30)
    {
        _w = width; _h = height;
        _dec = new MobiclipDecoder(width, height);
        _mot = new (int, int)[width / 16 + 3];
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
        var tmp = new double[size * size];
        for (int k = 0; k < size; k++)
            for (int b = 0; b < size; b++)
            {
                double s = 0;
                for (int a = 0; a < size; a++) s += mi[k][a] * resid[b * size + a];
                tmp[k * size + b] = s * 64.0;
            }
        var c = new double[size * size];
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

    private static void WriteCoeffs(BitWriterMsb bw, int[] levels, int tab)
    {
        var vlc = MobiclipDecoder.RlVlc[tab];
        var res = MobiclipTables.RunResidue;
        int ro = tab * 256;
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
            int r1 = res[ro + (isLast ? 64 : 0) + run];
            if (r1 > 0 && a - r1 >= 1 && a - r1 <= 31 && vlc.Codes.ContainsKey(lb | (run << 5) | (a - r1)))
            {
                bw.Code(vlc, 0); bw.Bit(0); bw.Code(vlc, lb | (run << 5) | (a - r1)); bw.Bit(l < 0 ? 1 : 0); continue;
            }
            bool done = false;
            if (a <= 31)
            {
                int r2 = res[ro + 128 + (isLast ? 64 : 0) + a];
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

    private static int BlockBits(int[]? levels, int tab)
    {
        if (levels == null) return 0;
        var bw = new BitWriterMsb();
        bw.UE(0); WriteCoeffs(bw, levels, tab);
        return bw.BitCount;
    }

    private static int UeBits(int v) { var b = new BitWriterMsb(); b.UE(v); return b.BitCount; }
    private static int SeBits(int v) { var b = new BitWriterMsb(); b.SE(v); return b.BitCount; }

    // ---------- shared block coding ----------

    private sealed class Region
    {
        public readonly byte[] Y = new byte[256], U = new byte[64], V = new byte[64];

        public void Save(byte[][] f, int x, int y, int w)
        {
            for (int r = 0; r < 16; r++) Array.Copy(f[0], (y + r) * w + x, Y, r * 16, 16);
            int cw = w >> 1;
            for (int r = 0; r < 8; r++)
            {
                Array.Copy(f[1], ((y >> 1) + r) * cw + (x >> 1), U, r * 8, 8);
                Array.Copy(f[2], ((y >> 1) + r) * cw + (x >> 1), V, r * 8, 8);
            }
        }

        public void Restore(byte[][] f, int x, int y, int w)
        {
            for (int r = 0; r < 16; r++) Array.Copy(Y, r * 16, f[0], (y + r) * w + x, 16);
            int cw = w >> 1;
            for (int r = 0; r < 8; r++)
            {
                Array.Copy(U, r * 8, f[1], ((y >> 1) + r) * cw + (x >> 1), 8);
                Array.Copy(V, r * 8, f[2], ((y >> 1) + r) * cw + (x >> 1), 8);
            }
        }
    }

    /// <summary>Quantises src-minus-current-picture for one block and adds the reconstructed residual to the picture.</summary>
    private int[]? Residual(byte[][] frame, byte[] src, int stride, int bx, int by, int size, int plane)
    {
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

    private static double Sse(byte[] a, byte[] b, int stride, int x0, int y0, int w, int h)
    {
        double s = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) { int d = a[y * stride + x] - b[y * stride + x]; s += d * d; }
        return s;
    }

    private double MbSse(byte[][] frame, YuvFrame src, int x, int y) =>
        Sse(src.Y, frame[0], _w, x, y, 16, 16) + Sse(src.U, frame[1], _w >> 1, x >> 1, y >> 1, 8, 8) + Sse(src.V, frame[2], _w >> 1, x >> 1, y >> 1, 8, 8);

    // ---------- intra macroblocks ----------

    private sealed class Plan
    {
        public double Cost;
        public int Mode;
        public int[]?[] Levels = Array.Empty<int[]?>();
    }

    private sealed class IntraMb
    {
        public Plan Luma = null!, Chroma = null!;
        public double Cost;
        public int Flags, CbpIdx;
    }

    private static bool ModeValid(int mode, int ax, int ay) => mode switch
    {
        3 => true,
        0 => ay > 0,
        1 or 4 => ax > 0,
        _ => ax > 0 && ay > 0,
    };

    private static readonly int[] CandidateModes = { 3, 0, 1, 4, 5, 6, 7 };

    private int[]? IntraBlock(byte[][] frame, byte[] src, int stride, int bx, int by, int size, int plane, int mode)
    {
        _dec.PredictIntra(frame, bx, by, mode, false, size, plane);
        return Residual(frame, src, stride, bx, by, size, plane);
    }

    private Plan PlanLuma(byte[][] frame, YuvFrame src, int x, int y, int mode, double lambda, int tab)
    {
        var plan = new Plan { Mode = mode, Levels = new int[]?[4] };
        double bits = 0;
        int i = 0;
        foreach (var (bx, by) in new[] { (x, y), (x + 8, y), (x, y + 8), (x + 8, y + 8) })
        {
            plan.Levels[i] = IntraBlock(frame, src.Y, _w, bx, by, 8, 0, mode);
            bits += BlockBits(plan.Levels[i], tab);
            i++;
        }
        plan.Cost = Sse(src.Y, frame[0], _w, x, y, 16, 16) + lambda * bits;
        return plan;
    }

    private Plan PlanChroma(byte[][] frame, YuvFrame src, int x, int y, int mode, double lambda, int tab)
    {
        var plan = new Plan { Mode = mode, Levels = new int[]?[2] };
        double bits = 0, sse = 0;
        int cw = _w >> 1;
        for (int k = 0; k < 2; k++)
        {
            var sp = k == 0 ? src.U : src.V;
            plan.Levels[k] = IntraBlock(frame, sp, cw, x >> 1, y >> 1, 8, 1 + k, mode);
            bits += BlockBits(plan.Levels[k], tab);
            sse += Sse(sp, frame[1 + k], cw, x >> 1, y >> 1, 8, 8);
        }
        plan.Cost = sse + lambda * bits;
        return plan;
    }

    /// <summary>Chooses intra modes for a macroblock and leaves its reconstruction in the picture.</summary>
    private IntraMb PlanIntraMb(byte[][] frame, YuvFrame src, int x, int y, double lambda, int tab)
    {
        Plan? best = null;
        foreach (int m in CandidateModes)
        {
            if (!ModeValid(m, x, y)) continue;
            var p = PlanLuma(frame, src, x, y, m, lambda, tab);
            if (best == null || p.Cost < best.Cost) best = p;
        }
        var luma = PlanLuma(frame, src, x, y, best!.Mode, lambda, tab);
        Plan? bestC = null;
        foreach (int m in CandidateModes)
        {
            if (!ModeValid(m, x >> 1, y >> 1)) continue;
            var p = PlanChroma(frame, src, x, y, m, lambda, tab);
            if (bestC == null || p.Cost < bestC.Cost) bestC = p;
        }
        var chroma = PlanChroma(frame, src, x, y, bestC!.Mode, lambda, tab);
        int flags = 0;
        for (int i = 0; i < 4; i++) if (luma.Levels[i] != null) flags |= 1 << i;
        for (int i = 0; i < 2; i++) if (chroma.Levels[i] != null) flags |= 1 << (4 + i);
        int idx = Array.IndexOf(MobiclipTables.Block8x8CoefficientsTab, (byte)flags);
        return new IntraMb
        {
            Luma = luma, Chroma = chroma, Flags = flags, CbpIdx = idx,
            Cost = luma.Cost + chroma.Cost + lambda * (UeBits(idx) + 6),
        };
    }

    private static void WriteIntraPayload(BitWriterMsb bw, IntraMb mb, int tab)
    {
        bw.UE(mb.CbpIdx);
        bw.Bits(mb.Luma.Mode, 3);
        for (int i = 0; i < 4; i++)
            if (mb.Luma.Levels[i] is { } lv) { bw.UE(0); WriteCoeffs(bw, lv, tab); }
        bw.Bits(mb.Chroma.Mode, 3);
        for (int i = 0; i < 2; i++)
            if (mb.Chroma.Levels[i] is { } lv) { bw.UE(0); WriteCoeffs(bw, lv, tab); }
    }

    // ---------- inter macroblocks ----------

    private sealed class InterMb
    {
        public int Index, Mvx, Mvy;
        public double Cost;
        public int[]?[] Levels = new int[]?[6];
        public int CbpIdx;
    }

    private InterMb EvalInter(byte[][] frame, YuvFrame src, int x, int y, int index, int mvx, int mvy, (int x, int y) pred, double lambda)
    {
        int sidx = _dec.RingIndex(Math.Max(1, index));
        _dec.MotionCompensate(sidx, mvx, mvy, 16, 16, x, y);
        var mb = new InterMb { Index = index, Mvx = mvx, Mvy = mvy };
        double bits = MobiclipDecoder.MvVlc[1][0].Codes[index].Len;
        if (index > 0) bits += SeBits(mvx - pred.x) + SeBits(mvy - pred.y);
        int i = 0, flags = 0;
        foreach (var (bx, by) in new[] { (x, y), (x + 8, y), (x, y + 8), (x + 8, y + 8) })
        {
            mb.Levels[i] = Residual(frame, src.Y, _w, bx, by, 8, 0);
            if (mb.Levels[i] != null) { flags |= 1 << i; bits += BlockBits(mb.Levels[i], 0); }
            i++;
        }
        int cw = _w >> 1;
        mb.Levels[4] = Residual(frame, src.U, cw, x >> 1, y >> 1, 8, 1);
        mb.Levels[5] = Residual(frame, src.V, cw, x >> 1, y >> 1, 8, 2);
        for (int k = 4; k < 6; k++)
            if (mb.Levels[k] != null) { flags |= 1 << k; bits += BlockBits(mb.Levels[k], 0); }
        mb.CbpIdx = Array.IndexOf(MobiclipTables.PframeBlock8x8CoefficientsTab, (byte)flags);
        bits += UeBits(mb.CbpIdx);
        mb.Cost = MbSse(frame, src, x, y) + lambda * bits;
        return mb;
    }

    private static int PelAt(byte[] r, int stride, int x, int y, int method)
    {
        int s = y * stride + x;
        return method switch
        {
            0 => r[s],
            1 => (r[s] >> 1) + (r[s + 1] >> 1),
            2 => (r[s] >> 1) + (r[s + stride] >> 1),
            _ => (((r[s] >> 1) + (r[s + 1] >> 1)) >> 1) + (((r[s + stride] >> 1) + (r[s + 1 + stride] >> 1)) >> 1),
        };
    }

    private int LumaSad(byte[] refY, YuvFrame src, int x, int y, int mvx, int mvy, int limit)
    {
        int method = (mvx & 1) | ((mvy & 1) << 1);
        int rx = x + (mvx >> 1), ry = y + (mvy >> 1);
        int sad = 0;
        for (int r = 0; r < 16; r++)
        {
            for (int c = 0; c < 16; c++)
                sad += Math.Abs(src.Y[(y + r) * _w + x + c] - PelAt(refY, _w, rx + c, ry + r, method));
            if (sad >= limit) return sad;
        }
        return sad;
    }

    private (int x, int y) Search(YuvFrame src, int x, int y, (int x, int y) pred)
    {
        var refY = _dec.RefFrame(1)[0];
        int bestSad = int.MaxValue;
        (int x, int y) best = (0, 0);
        void Try(int mx, int my)
        {
            if (!_dec.MvInRange(mx, my, 16, 16, x, y)) return;
            int pen = 2 * (Math.Abs(mx - pred.x) + Math.Abs(my - pred.y)) / 2;
            int s = LumaSad(refY, src, x, y, mx, my, bestSad) + pen;
            if (s < bestSad) { bestSad = s; best = (mx, my); }
        }
        Try(pred.x, pred.y);
        Try(0, 0);
        var centers = new[] { ((pred.x >> 1) << 1, (pred.y >> 1) << 1), (0, 0) };
        foreach (var (cx, cy) in centers)
            for (int dy = -SearchRange; dy <= SearchRange; dy++)
                for (int dx = -SearchRange; dx <= SearchRange; dx++)
                    Try(cx + dx * 2, cy + dy * 2);
        var c0 = best;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0) Try(c0.x + dx, c0.y + dy);
        return best;
    }

    // ---------- frames ----------

    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    /// <summary>Encodes a key frame and returns the packet; <paramref name="recon"/> is what a decoder will output.</summary>
    public byte[] EncodeKeyFrame(YuvFrame src, out YuvFrame recon)
    {
        if (src.Width != _w || src.Height != _h) throw new ArgumentException("Frame size mismatch.");
        int tab = KeyFrameTable;
        var bw = new BitWriterMsb();
        bw.Bit(1); bw.Bit(1); bw.Bit(tab); bw.Bits(_q, 6);
        _dec.SetupQ(_q);
        _dec.SetMoflex(true);
        _dec.SetDctTable(tab);
        var frame = _dec.CurrentFrame;
        double lambda = Lambda();
        for (int y = 0; y < _h; y += 16)
            for (int x = 0; x < _w; x += 16)
            {
                var mb = PlanIntraMb(frame, src, x, y, lambda, tab);
                bw.Bit(0);
                WriteIntraPayload(bw, mb, tab);
            }
        recon = Snapshot(frame, true);
        _dec.AdvanceFrame();
        return bw.ToPacket();
    }

    /// <summary>Encodes a predicted frame (needs a previous frame to have been encoded).</summary>
    public byte[] EncodePredictedFrame(YuvFrame src, int quantizer, out YuvFrame recon)
    {
        if (src.Width != _w || src.Height != _h) throw new ArgumentException("Frame size mismatch.");
        if (quantizer < 12 || quantizer > 161) throw new ArgumentOutOfRangeException(nameof(quantizer));
        var bw = new BitWriterMsb();
        bw.Bit(0);
        bw.SE(quantizer - _dec.Quantizer);
        _dec.SetupQ(quantizer);
        _dec.SetDctTable(0);
        var frame = _dec.CurrentFrame;
        double lambda = Lambda();
        Array.Clear(_mot);
        var best = new Region();
        var trial = new Region();
        for (int y = 0; y < _h; y += 16)
            for (int x = 0; x < _w; x += 16)
            {
                int mx = x / 16;
                var pred = (x: Median(_mot[mx + 1].x, _mot[mx + 2].x, _mot[mx + 3].x),
                            y: Median(_mot[mx + 1].y, _mot[mx + 2].y, _mot[mx + 3].y));
                _mot[0] = pred;
                _mot[mx + 2] = (0, 0);

                InterMb? bestInter = null;
                void Consider(int index, int mvx, int mvy)
                {
                    if (!_dec.MvInRange(mvx, mvy, 16, 16, x, y)) return;
                    var c = EvalInter(frame, src, x, y, index, mvx, mvy, pred, lambda);
                    if (bestInter == null || c.Cost < bestInter.Cost)
                    {
                        bestInter = c;
                        best.Save(frame, x, y, _w);
                    }
                }
                Consider(0, pred.x, pred.y);
                var mv = Search(src, x, y, pred);
                if (mv != pred) Consider(1, mv.x, mv.y);
                if (mv != (0, 0) && pred != (0, 0)) Consider(1, 0, 0);

                var intra = PlanIntraMb(frame, src, x, y, lambda, 0);
                double intraCost = intra.Cost + lambda * MobiclipDecoder.MvVlc[1][0].Codes[6].Len;
                if (bestInter == null || intraCost < bestInter.Cost)
                {
                    bw.Code(MobiclipDecoder.MvVlc[1][0], 6);
                    WriteIntraPayload(bw, intra, 0);
                }
                else
                {
                    best.Restore(frame, x, y, _w);
                    var m = bestInter;
                    bw.Code(MobiclipDecoder.MvVlc[1][0], m.Index);
                    if (m.Index > 0) { bw.SE(m.Mvx - pred.x); bw.SE(m.Mvy - pred.y); }
                    bw.UE(m.CbpIdx);
                    for (int k = 0; k < 6; k++)
                        if (m.Levels[k] is { } lv) { bw.UE(0); WriteCoeffs(bw, lv, 0); }
                    _mot[mx + 2] = (m.Mvx, m.Mvy);
                }
                _ = trial;
            }
        recon = Snapshot(frame, false);
        _dec.AdvanceFrame();
        return bw.ToPacket();
    }

    private double Lambda()
    {
        double qs = _dec.QuantTable(8)[0] / 8.0;
        return LambdaScale * qs * qs;
    }

    private YuvFrame Snapshot(byte[][] frame, bool key) =>
        new() { Width = _w, Height = _h, IsKey = key, Y = (byte[])frame[0].Clone(), U = (byte[])frame[1].Clone(), V = (byte[])frame[2].Clone() };
}
