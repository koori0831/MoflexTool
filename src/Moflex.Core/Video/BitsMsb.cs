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
        return _sym[idx];
    }
}
