namespace Moflex.Core.Video;

/// <summary>MSB-first bit reader over a zero-padded buffer (reads past the end yield zeros).</summary>
internal sealed class BitsMsb
{
    private readonly byte[] _d;
    private readonly int _len;
    private int _pos;

    public BitsMsb(byte[] data, int length) { _d = data; _len = length; }

    public int BitsLeft => _len * 8 - _pos;

    public uint Peek(int n)
    {
        int bp = _pos >> 3;
        ulong v = 0;
        for (int i = 0; i < 5; i++) v = (v << 8) | (bp + i < _len ? _d[bp + i] : (byte)0);
        return (uint)((v >> (40 - (_pos & 7) - n)) & ((1UL << n) - 1));
    }

    public void Skip(int n) => _pos += n;
    public int Bit() { int b = (int)Peek(1); _pos++; return b; }
    public int Bits(int n) { if (n == 0) return 0; int v = (int)Peek(n); _pos += n; return v; }
    public int SBits(int n) { int v = Bits(n); return (v << (32 - n)) >> (32 - n); }

    public int UE()
    {
        int n = 0;
        while (Bit() == 0) { if (++n > 31) throw new InvalidDataException("Bad Exp-Golomb code."); }
        return n == 0 ? 0 : (int)((1u << n) - 1 + (uint)Bits(n));
    }

    public int SE()
    {
        int k = UE();
        return (k & 1) != 0 ? (k + 1) >> 1 : -(k >> 1);
    }
}

/// <summary>Prefix-code table built from (length, symbol) lists in the order the codes are assigned.</summary>
internal sealed class Vlc
{
    private readonly int _maxLen;
    private readonly int[] _sym;
    private readonly byte[] _len;
    public readonly Dictionary<int, (uint Code, int Len)> Codes = new();
    public int LastLen;

    public Vlc(ReadOnlySpan<byte> lens, ReadOnlySpan<int> syms)
    {
        _maxLen = 0;
        foreach (var l in lens) _maxLen = Math.Max(_maxLen, l);
        _sym = new int[1 << _maxLen];
        _len = new byte[1 << _maxLen];
        ulong code = 0;
        for (int i = 0; i < lens.Length; i++)
        {
            int l = lens[i];
            if (code >> 32 != 0) throw new InvalidDataException("VLC overflow.");
            int c = (int)(code >> (32 - l));
            // Some symbols appear twice (the escape symbol 0 has a 9-bit filler code and the 7-bit code real
            // streams use); always encode with the shortest code.
            if (!Codes.TryGetValue(syms[i], out var prev) || l < prev.Len) Codes[syms[i]] = ((uint)c, l);
            int lo = c << (_maxLen - l), hi = lo + (1 << (_maxLen - l));
            for (int k = lo; k < hi; k++) { _sym[k] = syms[i]; _len[k] = (byte)l; }
            code += 1UL << (32 - l);
        }
    }

    public int Read(BitsMsb br)
    {
        int idx = (int)br.Peek(_maxLen);
        int l = _len[idx];
        if (l == 0) throw new InvalidDataException("Invalid VLC code.");
        br.Skip(l);
        LastLen = l;
        return _sym[idx];
    }
}

/// <summary>MSB-first bit writer.</summary>
internal sealed class BitWriterMsb
{
    private readonly List<byte> _bytes = new();
    private int _cur, _n;
    public int BitCount => _bytes.Count * 8 + _n;

    public void Bit(int b) { _cur = (_cur << 1) | (b & 1); if (++_n == 8) { _bytes.Add((byte)_cur); _cur = 0; _n = 0; } }
    public void Bits(int v, int count) { for (int i = count - 1; i >= 0; i--) Bit((v >> i) & 1); }
    public void UE(int v)
    {
        int x = v + 1, len = 0;
        while ((x >> len) > 1) len++;
        Bits(0, len); Bits(x, len + 1);
    }
    public void SE(int v) => UE(v > 0 ? 2 * v - 1 : -2 * v);
    public void Code(Vlc vlc, int sym) { var (c, l) = vlc.Codes[sym]; Bits((int)c, l); }

    /// <summary>Returns the stream padded to a whole number of 16-bit words with each word byte-swapped (the on-disk order).</summary>
    public byte[] ToPacket()
    {
        var list = new List<byte>(_bytes);
        if (_n != 0) list.Add((byte)(_cur << (8 - _n)));
        if ((list.Count & 1) != 0) list.Add(0);
        var a = list.ToArray();
        for (int i = 0; i < a.Length; i += 2) (a[i], a[i + 1]) = (a[i + 1], a[i]);
        return a;
    }
}
