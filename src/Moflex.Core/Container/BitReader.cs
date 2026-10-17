namespace Moflex.Core.Container;

/// <summary>MSB-first bit reader over a byte span (used for MOFLEX chunk headers).</summary>
public ref struct BitReaderMsb
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private int _bitPos; // bits consumed in the current byte (0..7)

    public BitReaderMsb(ReadOnlySpan<byte> data, int startByte)
    {
        _data = data;
        _bytePos = startByte;
        _bitPos = 0;
    }

    /// <summary>Index of the first byte that has not been touched (a partially read byte counts as consumed).</summary>
    public int AlignedBytePosition => _bitPos == 0 ? _bytePos : _bytePos + 1;

    public int ReadBit()
    {
        if (_bytePos >= _data.Length) throw new EndOfStreamException("Bit reader ran past the end of the data.");
        int bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
        if (++_bitPos == 8) { _bitPos = 0; _bytePos++; }
        return bit;
    }

    public ulong ReadBits(int count)
    {
        if (count > 64) throw new InvalidDataException("More than 64 bits requested.");
        ulong v = 0;
        for (int i = 0; i < count; i++) v = (v << 1) | (uint)ReadBit();
        return v;
    }

    /// <summary>Unary length: number of bits up to and including the first 1 bit.</summary>
    public int ReadUnaryLength()
    {
        int n = 1;
        while (ReadBit() == 0) n++;
        return n;
    }
}
