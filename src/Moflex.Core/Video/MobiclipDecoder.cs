namespace Moflex.Core.Video;

/// <summary>Decoded planar 4:2:0 frame (planes are tightly packed).</summary>
public sealed class YuvFrame
{
    public int Width, Height;
    public byte[] Y = Array.Empty<byte>(), U = Array.Empty<byte>(), V = Array.Empty<byte>();
    public bool IsKey;
}

/// <summary>Independent C# implementation of the Mobiclip video bitstream decoder.</summary>
public sealed class MobiclipDecoder
{
    private static readonly byte[] Zigzag8 =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    internal static readonly Vlc[] RlVlc = new Vlc[2];
    internal static readonly Vlc[][] MvVlc = { new Vlc[16], new Vlc[16] };

    static MobiclipDecoder()
    {
        var bits = MobiclipTables.Bits0;
        for (int i = 0; i < 2; i++)
        {
            var syms = (i == 0 ? MobiclipTables.Syms0 : MobiclipTables.Syms1).Select(v => (int)v).ToArray();
            RlVlc[i] = new Vlc(bits, syms);
            int off = 0;
            for (int j = 0; j < 16; j++)
            {
                int n = MobiclipTables.MvLen[j];
                int baseOff = i * 122 + off; // 122 = sum of MvLen
                MvVlc[i][j] = new Vlc(MobiclipTables.MvBits.AsSpan(baseOff, n),
                    MobiclipTables.MvSyms.Skip(baseOff).Take(n).Select(v => (int)v).ToArray());
                off += n;
            }
        }
    }

    private readonly int _w, _h;
    private readonly byte[][][] _pic = new byte[6][][];
    private int _cur;
    private bool _moflex;
    private int _dctTab;
    private int _quantizer;
    private BitsMsb _br = null!;
    private readonly int[][] _qtab = { new int[16], new int[64] };
    private readonly byte[] _pre = new byte[32];
    private readonly (int x, int y)[] _motion;

    public MobiclipDecoder(int width, int height)
    {
        if ((width & 15) != 0 || (height & 15) != 0) throw new ArgumentException("Size must be a multiple of 16.");
        _w = width; _h = height;
        for (int i = 0; i < 6; i++)
            _pic[i] = new[] { new byte[width * height], new byte[width * height / 4], new byte[width * height / 4] };
        _motion = new (int, int)[width / 16 + 3];
    }

    // --- hooks used by the encoder (closed-loop reconstruction) ---
    internal byte[][] CurrentFrame => _pic[_cur];
    internal void AdvanceFrame() => _cur = (_cur + 1) % 6;
    internal int[] QuantTable(int size) => _qtab[size == 8 ? 1 : 0];
    internal static byte[] ZigzagFor(int size) => size == 8 ? Zigzag8 : MobiclipTables.Zigzag4x4Tab;
    internal void SetMoflex(bool v) => _moflex = v;
    internal void SetDctTable(int t) => _dctTab = t;

    private int PlaneW(int p) => p == 0 ? _w : _w >> 1;
    private int PlaneH(int p) => p == 0 ? _h : _h >> 1;

    public YuvFrame Decode(byte[] packet)
    {
        int len = (packet.Length + 1) & ~1;
        var buf = new byte[len];
        for (int i = 0; i + 1 < len; i += 2)
        {
            buf[i] = i + 1 < packet.Length ? packet[i + 1] : (byte)0;
            buf[i + 1] = packet[i];
        }
        _br = new BitsMsb(buf, len);
        if ((long)(_h / 16) * (_w / 16) * 2 > 8L * len) throw new InvalidDataException("Packet too small.");
        var frame = _pic[_cur];
        bool key = _br.Bit() == 1;
        if (key)
        {
            _moflex = _br.Bit() == 1;
            _dctTab = _br.Bit();
            SetupQ(_br.Bits(6));
            for (int y = 0; y < _h; y += 16)
                for (int x = 0; x < _w; x += 16)
                    DecodeMacroblock(frame, x, y, _br.Bit() == 1);
        }
        else
        {
            Array.Clear(_motion);
            _dctTab = 0;
            SetupQ(_quantizer + _br.SE());
            for (int y = 0; y < _h; y += 16)
                for (int x = 0; x < _w; x += 16)
                {
                    int mx = x / 16;
                    _motion[0] = (Med(_motion[mx + 1].x, _motion[mx + 2].x, _motion[mx + 3].x),
                                  Med(_motion[mx + 1].y, _motion[mx + 2].y, _motion[mx + 3].y));
                    _motion[mx + 2] = (0, 0);
                    int idx = MvVlc[_moflex ? 1 : 0][0].Read(_br);
                    if (idx == 6 || idx == 7) DecodeMacroblock(frame, x, y, idx == 7);
                    else
                    {
                        PredictMotion(16, 16, idx, mx + 2, x, y);
                        int idx2 = _br.UE();
                        if (idx2 >= 64) throw new InvalidDataException("Bad coefficient flags.");
                        int flags = MobiclipTables.PframeBlock8x8CoefficientsTab[idx2];
                        for (int sy = y; sy < y + 16; sy += 8)
                            for (int sx = x; sx < x + 16; sx += 8)
                            {
                                if ((flags & 1) != 0) AddPframeCoefficients(frame, sx, sy, 8, 0);
                                flags >>= 1;
                            }
                        if ((flags & 1) != 0) AddPframeCoefficients(frame, x >> 1, y >> 1, 8, _moflex ? 1 : 2);
                        flags >>= 1;
                        if ((flags & 1) != 0) AddPframeCoefficients(frame, x >> 1, y >> 1, 8, _moflex ? 2 : 1);
                    }
                }
        }
        _cur = (_cur + 1) % 6;
        return new YuvFrame { Width = _w, Height = _h, IsKey = key, Y = (byte[])frame[0].Clone(), U = (byte[])frame[1].Clone(), V = (byte[])frame[2].Clone() };
    }

    private static int Med(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    internal void SetupQ(long q)
    {
        if (q < 12 || q > 161) throw new InvalidDataException("Quantizer out of range.");
        _quantizer = (int)q;
        int qx = _quantizer % 6, qy = _quantizer / 6;
        for (int i = 0; i < 16; i++) _qtab[0][i] = MobiclipTables.Quant4x4Tab[qx * 16 + i] << qy;
        for (int i = 0; i < 64; i++) _qtab[1][i] = MobiclipTables.Quant8x8Tab[qx * 64 + i] << (qy - 2);
        for (int i = 0; i < 20; i++) _pre[i] = 9;
    }

    // ---------------- residual ----------------

    private static void Inverse4(Span<int> r)
    {
        int a = r[0] + r[2], b = r[0] - r[2], c = r[1] + (r[3] >> 1), d = (r[1] >> 1) - r[3];
        r[0] = a + c; r[1] = b + d; r[2] = b - d; r[3] = a - c;
    }

    internal static void Idct(Span<int> arr, int size)
    {
        if (size == 4) { Inverse4(arr); return; }
        Span<int> t = stackalloc int[4];
        t[0] = arr[0]; t[1] = arr[2]; t[2] = arr[4]; t[3] = arr[6];
        Inverse4(t);
        int e = arr[7] + arr[1] - arr[3] - (arr[3] >> 1);
        int f = arr[7] - arr[1] + arr[5] + (arr[5] >> 1);
        int g = arr[5] - arr[3] - arr[7] - (arr[7] >> 1);
        int h = arr[5] + arr[3] + arr[1] + (arr[1] >> 1);
        int x3 = g + (h >> 2), x2 = e + (f >> 2), x1 = (e >> 2) - f, x0 = h - (g >> 2);
        arr[0] = t[0] + x0; arr[1] = t[1] + x1; arr[2] = t[2] + x2; arr[3] = t[3] + x3;
        arr[4] = t[3] - x3; arr[5] = t[2] - x2; arr[6] = t[1] - x1; arr[7] = t[0] - x0;
    }

    private void ReadRun(out bool last, out int run, out int level)
    {
        int n = RlVlc[_dctTab].Read(_br);
        last = (n >> 11) == 1; run = (n >> 5) & 0x3F; level = n & 0x1F;
    }

    private void AddCoefficients(byte[][] frame, int bx, int by, int size, int plane)
    {
        Span<int> mat = stackalloc int[64];
        mat.Clear();
        var zt = size == 8 ? Zigzag8 : MobiclipTables.Zigzag4x4Tab;
        var qtab = _qtab[size == 8 ? 1 : 0];
        int stride = PlaneW(plane);
        var dst = frame[plane];
        for (int pos = 0; _br.BitsLeft > 0; pos++)
        {
            bool last; int run, level;
            ReadRun(out last, out run, out level);
            if (level != 0)
            {
                if (_br.Bit() == 1) level = -level;
            }
            else if (_br.Bit() == 0)
            {
                ReadRun(out last, out run, out level);
                level += MobiclipTables.RunResidue[_dctTab * 256 + (last ? 64 : 0) + run];
                if (_br.Bit() == 1) level = -level;
            }
            else if (_br.Bit() == 0)
            {
                ReadRun(out last, out run, out level);
                run += MobiclipTables.RunResidue[_dctTab * 256 + 128 + (last ? 64 : 0) + level];
                if (_br.Bit() == 1) level = -level;
            }
            else
            {
                last = _br.Bit() == 1;
                run = _br.Bits(6);
                level = _br.SBits(12);
            }
            pos += run;
            if (pos >= size * size) throw new InvalidDataException("Coefficient position out of range.");
            mat[zt[pos]] = qtab[pos] * level;
            if (last) break;
        }
        Reconstruct(mat, dst, stride, bx, by, size);
    }

    /// <summary>Inverse-transforms dequantised coefficients (row-major, zigzag already undone) and adds them to the picture.</summary>
    internal static void Reconstruct(Span<int> mat, byte[] dst, int stride, int bx, int by, int size)
    {
        mat[0] += 32;
        for (int y = 0; y < size; y++) Idct(mat.Slice(y * size, size), size);
        int o = by * stride + bx;
        for (int y = 0; y < size; y++)
        {
            for (int x = y + 1; x < size; x++)
            {
                int a = mat[x * size + y], b = mat[y * size + x];
                mat[y * size + x] = a; mat[x * size + y] = b;
            }
            Idct(mat.Slice(y * size, size), size);
            for (int x = 0; x < size; x++) dst[o + x] = (byte)Math.Clamp(dst[o + x] + (mat[y * size + x] >> 6), 0, 255);
            o += stride;
        }
    }

    private void AddPframeCoefficients(byte[][] frame, int bx, int by, int size, int plane)
    {
        int idx = _br.UE();
        if (idx == 0) { AddCoefficients(frame, bx, by, size, plane); return; }
        if (idx >= 16) throw new InvalidDataException("Bad P-frame coefficient index.");
        int flags = MobiclipTables.PframeBlock4x4CoefficientsTab[idx];
        for (int y = by; y < by + 8; y += 4)
            for (int x = bx; x < bx + 8; x += 4)
            {
                if ((flags & 1) != 0) AddCoefficients(frame, x, y, 4, plane);
                flags >>= 1;
            }
    }

    // ---------------- intra prediction ----------------

    private byte[] _pb = null!;
    private int _ps, _pw, _ph, _pax, _pay, _psize;

    private int Pget(int x, int y)
    {
        int rx = x, ry = y;
        if (x == -1 && y >= _psize) { rx = -1; ry = _psize - 1; }
        else if (x >= -1 && y >= -1) { }
        else if (x == -1 && y == -2) { rx = 0; ry = -1; }
        else if (x == -2 && y == -1) { rx = -1; ry = 0; }
        int yy = Math.Clamp(_pay + ry, 0, _ph - 1), xx = Math.Clamp(_pax + rx, 0, _pw - 1);
        return _pb[yy * _ps + xx];
    }

    private static int Half(int a, int b) => (a + b + 1) / 2;
    private static int Half3(int a, int b, int c) => ((a + b + b + c) * 2 / 4 + 1) / 2;
    private int HalfHorz(int x, int y) => Half3(Pget(x - 1, y), Pget(x, y), Pget(x + 1, y));
    private int HalfVert(int x, int y) => Half3(Pget(x, y - 1), Pget(x, y), Pget(x, y + 1));

    private int Pick(int mode, int x, int y)
    {
        switch (mode)
        {
            case 0: return Pget(x, y - 1);
            case 1: return Pget(x - 1, y);
            case 4:
                if ((x % 2) == 0) return Half(Pget(-1, y + x / 2), Pget(-1, y + x / 2 + 1));
                return HalfVert(-1, y + x / 2 + 1);
            case 5:
                if (x == 0) return Half(Pget(-1, y - 1), Pget(-1, y));
                if (y == 0) return HalfHorz(x - 2, y - 1);
                if (x == 1) return HalfVert(x - 2, y - 1);
                return Pget(x - 2, y - 1);
            case 6:
                if (y == 0) return Half(Pget(x - 1, -1), Pget(x, -1));
                if (x == 0) return HalfVert(x - 1, y - 2);
                if (y == 1) return HalfHorz(x - 1, y - 2);
                return Pget(x - 1, y - 2);
            case 7:
            {
                int clr = Pget(x - 1, y - 1);
                if (x != 0 && y != 0) return clr;
                int acc1 = x == 0 ? Pget(-1, y) : Pget(x - 2, -1);
                int acc2 = y == 0 ? Pget(x, -1) : Pget(-1, y - 2);
                return Half3(acc1, clr, acc2);
            }
            default: // 8
                if (y == 0) return Half(Pget(x, -1), Pget(x + 1, -1));
                if (y == 1) return HalfHorz(x + 1, y - 2);
                if (x < _psize - 1) return Pget(x + 1, y - 2);
                if (y % 2 == 0) return Half(Pget(y / 2 + _psize - 1, -1), Pget(y / 2 + _psize, -1));
                return HalfHorz(y / 2 + _psize, -1);
        }
    }

    private static int Adjust(int x, int size) => size == 16 ? (x + 1) >> 1 : x;

    private static int BlockSum(byte[] b, int off, int w, int h, int stride)
    {
        int s = 0;
        for (int y = 0; y < h; y++) { for (int x = 0; x < w; x++) s += b[off + x]; off += stride; }
        return s;
    }

    internal void PredictIntra(byte[][] frame, int ax, int ay, int pmode, bool addCoeffs, int size, int plane)
    {
        var buf = frame[plane];
        int stride = PlaneW(plane);
        _pb = buf; _ps = stride; _pw = PlaneW(plane); _ph = PlaneH(plane); _pax = ax; _pay = ay; _psize = size;
        switch (pmode)
        {
            case 0: case 1: case 4: case 5: case 6: case 7: case 8:
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        buf[ax + x + (ay + y) * stride] = (byte)Pick(pmode, x, y);
                break;
            case 2:
            {
                int topOff = Math.Max(ay - 1, 0) * stride + ax;
                int leftOff = ay * stride + Math.Max(ax - 1, 0);
                int bottommost = buf[(ay + size - 1) * stride + Math.Max(ax - 1, 0)];
                int rightmost = buf[Math.Max(ay - 1, 0) * stride + ax + size - 1];
                int avg = (bottommost + rightmost + 1) / 2 + 2 * Math.Clamp(_br.SE(), -(1 << 16), 1 << 16);
                int r6 = Adjust(avg - bottommost, size), r9 = Adjust(avg - rightmost, size);
                int shift = Adjust(size, size) == 8 ? 3 : 2;
                Span<int> arr1 = stackalloc int[16], arr2 = stackalloc int[16];
                for (int x = 0; x < size; x++) arr1[x] = Adjust(((bottommost - buf[topOff + x]) * (1 << shift)) + r6 * (x + 1), size);
                for (int y = 0; y < size; y++) arr2[y] = Adjust(((rightmost - buf[leftOff + y * stride]) * (1 << shift)) + r9 * (y + 1), size);
                int blk = ay * stride + ax;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                        buf[blk + x] = (byte)(((buf[topOff + x] + buf[leftOff] + ((arr1[x] * (y + 1) + arr2[y] * (x + 1)) >> (2 * shift))) + 1) / 2);
                    blk += stride; leftOff += stride;
                }
                break;
            }
            case 3:
            {
                int fill;
                if (ax == 0 && ay == 0) fill = 0x80;
                else if (ax >= 1 && ay >= 1)
                {
                    int left = BlockSum(buf, ay * stride + ax - 1, 1, size, stride);
                    int top = BlockSum(buf, (ay - 1) * stride + ax, size, 1, stride);
                    fill = ((left + top) * 2 / (2 * size) + 1) / 2;
                }
                else if (ax >= 1) fill = (BlockSum(buf, ay * stride + ax - 1, 1, size, stride) * 2 / size + 1) / 2;
                else fill = (BlockSum(buf, (ay - 1) * stride + ax, size, 1, stride) * 2 / size + 1) / 2;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++) buf[ax + x + (ay + y) * stride] = (byte)fill;
                break;
            }
        }
        if (addCoeffs) AddCoefficients(frame, ax, ay, size, plane);
    }

    private int GetPrediction(int x, int y, int size)
    {
        int index = (y & 0xC) | (x / 4 % 4);
        int val = Math.Min((int)_pre[index], index % 4 == 0 ? 9 : _pre[index + 3]);
        if (val == 9) val = 3;
        if (_br.Bit() == 0)
        {
            int v = _br.Bits(3);
            val = v + (v >= val ? 1 : 0);
        }
        _pre[index + 4] = (byte)val;
        if (size == 8) _pre[index + 5] = _pre[index + 8] = _pre[index + 9] = (byte)val;
        return val;
    }

    private void ProcessBlock(byte[][] frame, int x, int y, int pmode, bool hasCoeffs, int plane)
    {
        if (!hasCoeffs)
        {
            if (pmode < 0) pmode = GetPrediction(x, y, 8);
            PredictIntra(frame, x, y, pmode, false, 8, plane);
            return;
        }
        int tmp = _br.UE();
        if (tmp > 16) throw new InvalidDataException("Bad block coefficient index.");
        if (tmp == 0)
        {
            if (pmode < 0) pmode = GetPrediction(x, y, 8);
            PredictIntra(frame, x, y, pmode, true, 8, plane);
        }
        else
        {
            int flags = MobiclipTables.Block4x4CoefficientsTab[tmp - 1];
            for (int by = y; by < y + 8; by += 4)
                for (int bx = x; bx < x + 8; bx += 4)
                {
                    int np = pmode;
                    if (np < 0) np = GetPrediction(bx, by, 4);
                    PredictIntra(frame, bx, by, np, (flags & 1) != 0, 4, plane);
                    flags >>= 1;
                }
        }
    }

    private void DecodeMacroblock(byte[][] frame, int x, int y, bool predict)
    {
        int idx = _br.UE();
        if (idx >= 64) throw new InvalidDataException("Bad macroblock index.");
        int flags = MobiclipTables.Block8x8CoefficientsTab[idx];
        if (predict)
        {
            ProcessBlock(frame, x, y, -1, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x + 8, y, -1, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x, y + 8, -1, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x + 8, y + 8, -1, (flags & 1) != 0, 0); flags >>= 1;
        }
        else
        {
            int pmode = _br.Bits(3);
            if (pmode == 2) { PredictIntra(frame, x, y, pmode, false, 16, 0); pmode = 9; }
            ProcessBlock(frame, x, y, pmode, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x + 8, y, pmode, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x, y + 8, pmode, (flags & 1) != 0, 0); flags >>= 1;
            ProcessBlock(frame, x + 8, y + 8, pmode, (flags & 1) != 0, 0); flags >>= 1;
        }
        int pa = _moflex ? 1 : 2, pb = _moflex ? 2 : 1;
        int pmodeUv = _br.Bits(3);
        if (pmodeUv == 2)
        {
            PredictIntra(frame, x >> 1, y >> 1, pmodeUv, false, 8, pa);
            PredictIntra(frame, x >> 1, y >> 1, pmodeUv, false, 8, pb);
            pmodeUv = 9;
        }
        ProcessBlock(frame, x >> 1, y >> 1, pmodeUv, (flags & 1) != 0, pa); flags >>= 1;
        ProcessBlock(frame, x >> 1, y >> 1, pmodeUv, (flags & 1) != 0, pb);
    }

    // ---------------- motion ----------------

    private static int GetIndex(int x) => x == 16 ? 0 : x == 8 ? 1 : x == 4 ? 2 : x == 2 ? 3 : 0;

    private void PredictMotion(int width, int height, int index, int offsetm, int offsetx, int offsety)
    {
        if (index <= 5)
        {
            int sidx = -Math.Max(1, index) + _cur;
            if (sidx < 0) sidx += 6;
            var mv = _motion[0];
            if (index > 0) { mv.x += _br.SE(); mv.y += _br.SE(); }
            _motion[offsetm] = mv;
            MotionCompensate(sidx, mv.x, mv.y, width, height, offsetx, offsety);
        }
        else
        {
            int adjx = index == 8 ? 0 : width / 2;
            int adjy = index == 8 ? height / 2 : 0;
            width -= adjx; height -= adjy;
            int tidx = GetIndex(height) * 4 + GetIndex(width);
            for (int i = 0; i < 2; i++)
            {
                int idx2 = MvVlc[_moflex ? 1 : 0][tidx].Read(_br);
                PredictMotion(width, height, idx2, offsetm, offsetx + i * adjx, offsety + i * adjy);
            }
        }
    }

    /// <summary>Copies a motion-compensated block (half-pel bilinear) from picture <paramref name="sidx"/> into the current picture.</summary>
    internal void MotionCompensate(int sidx, int mvx, int mvy, int width, int height, int offsetx, int offsety)
    {
        var mv = (x: mvx, y: mvy);
        int fw = _w, fh = _h;
        for (int i = 0; i < 3; i++)
        {
            if (i == 1)
            {
                offsetx >>= 1; offsety >>= 1; mv.x >>= 1; mv.y >>= 1;
                width >>= 1; height >>= 1; fw >>= 1; fh >>= 1;
            }
            int method = (mv.x & 1) | ((mv.y & 1) << 1);
            var srcB = _pic[sidx][i];
            var dstB = _pic[_cur][i];
            int stride = fw;
            if (offsetx + (mv.x >> 1) < 0 || offsety + (mv.y >> 1) < 0 ||
                offsetx + width + ((mv.x + 1) >> 1) > fw || offsety + height + ((mv.y + 1) >> 1) > fh)
                throw new InvalidDataException("Motion vector out of range.");
            int src = offsetx + (mv.x >> 1) + (offsety + (mv.y >> 1)) * stride;
            int dst = offsetx + offsety * stride;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int s0 = src + x;
                    dstB[dst + x] = method switch
                    {
                        0 => srcB[s0],
                        1 => (byte)((srcB[s0] >> 1) + (srcB[s0 + 1] >> 1)),
                        2 => (byte)((srcB[s0] >> 1) + (srcB[s0 + stride] >> 1)),
                        _ => (byte)((((srcB[s0] >> 1) + (srcB[s0 + 1] >> 1)) >> 1) + (((srcB[s0 + stride] >> 1) + (srcB[s0 + 1 + stride] >> 1)) >> 1)),
                    };
                }
                dst += stride; src += stride;
            }
        }
    }

    /// <summary>True when the motion vector (half-pel units) keeps the whole block inside the reference picture.</summary>
    internal bool MvInRange(int mvx, int mvy, int width, int height, int offsetx, int offsety)
    {
        int fw = _w, fh = _h;
        for (int i = 0; i < 3; i++)
        {
            if (i == 1) { offsetx >>= 1; offsety >>= 1; mvx >>= 1; mvy >>= 1; width >>= 1; height >>= 1; fw >>= 1; fh >>= 1; }
            if (offsetx + (mvx >> 1) < 0 || offsety + (mvy >> 1) < 0 ||
                offsetx + width + ((mvx + 1) >> 1) > fw || offsety + height + ((mvy + 1) >> 1) > fh) return false;
        }
        return true;
    }

    internal int Quantizer => _quantizer;
    internal int RingIndex(int back) => ((_cur - back) % 6 + 6) % 6;
    internal byte[][] RefFrame(int back) => _pic[((_cur - back) % 6 + 6) % 6];
}
