namespace Moflex.Core.Container;

/// <summary>Serialises a parsed MOFLEX structure back to bytes (lossless re-mux of an existing layout).</summary>
public static class MoflexWriter
{
    public static void WriteVarInt(Stream s, int v)
    {
        if (v < 0x80) { s.WriteByte((byte)v); return; }
        var parts = new List<byte>();
        while (v > 0) { parts.Add((byte)(v & 0x7F)); v >>= 7; }
        for (int i = parts.Count - 1; i >= 0; i--) s.WriteByte((byte)(parts[i] | (i > 0 ? 0x80 : 0)));
    }

    public static byte[] WriteSyncHeader(MoflexHeader h)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x4C, 0x32, 0xAA, 0xAB });
        for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(h.Timestamp >> (i * 8)));
        ms.WriteByte((byte)((h.BlockSize - 1) >> 8)); ms.WriteByte((byte)(h.BlockSize - 1));
        foreach (var st in h.Streams)
        {
            var body = new MemoryStream();
            void U16(int v) { body.WriteByte((byte)(v >> 8)); body.WriteByte((byte)v); }
            switch (st.Kind)
            {
                case StreamKind.Video:
                    body.WriteByte((byte)st.Index); body.WriteByte((byte)st.CodecId);
                    U16(st.TimeBaseDen); U16(st.TimeBaseNum); U16(st.Width); U16(st.Height);
                    break;
                case StreamKind.Audio:
                    body.WriteByte((byte)st.Index); body.WriteByte((byte)st.CodecId);
                    int sr = st.SampleRate - 1;
                    body.WriteByte((byte)(sr >> 16)); body.WriteByte((byte)(sr >> 8)); body.WriteByte((byte)sr);
                    body.WriteByte((byte)(st.Channels - 1));
                    break;
                default:
                    body.WriteByte((byte)st.Index);
                    break;
            }
            body.Write(st.Reserved);
            WriteVarInt(ms, st.DescriptorType); WriteVarInt(ms, (int)body.Length);
            body.WriteTo(ms);
        }
        ms.WriteByte(0); ms.WriteByte(0);
        return ms.ToArray();
    }

    private sealed class BitWriter
    {
        public readonly List<byte> Bytes = new();
        private int _cur, _n;
        public void Bit(int b) { _cur = (_cur << 1) | (b & 1); if (++_n == 8) { Bytes.Add((byte)_cur); _cur = 0; _n = 0; } }
        public void Bits(ulong v, int count) { for (int i = count - 1; i >= 0; i--) Bit((int)(v >> i) & 1); }
        public void Unary(int len) { for (int i = 1; i < len; i++) Bit(0); Bit(1); }
        public void Align() { while (_n != 0) Bit(0); }
    }

    public static int BitLength(int v) { int n = 1; while ((v >>= 1) > 0) n++; return n; }

    public static byte[] WriteChunkHeader(int stream, bool end, EndInfo? ei, int size)
    {
        var w = new BitWriter();
        int bl = BitLength(stream);
        w.Unary(bl); w.Bits((ulong)stream, bl);
        w.Bit(end ? 1 : 0);
        if (end)
        {
            var e = ei ?? new EndInfo(1, 0, 0, 1, 0);
            w.Unary(e.Len1); w.Bits(e.Value1, e.Len1);
            w.Bit(e.Flag);
            w.Unary(e.Len2); w.Bits(e.Value2, e.Len2 * 2 + 26);
        }
        w.Bits((ulong)(size - 1), 13);
        w.Align();
        return w.Bytes.ToArray();
    }

    /// <summary>Re-emits a parsed file using its original block/chunk layout.</summary>
    public static byte[] Rewrite(MoflexFile f, byte[] original)
    {
        var ms = new MemoryStream();
        int blockSize = f.Header.BlockSize;
        for (int bi = 0; bi < f.Blocks.Count; bi++)
        {
            var b = f.Blocks[bi];
            long start = ms.Length;
            if (b.HeaderLength > 0) ms.Write(original, (int)b.Offset, b.HeaderLength);
            ms.WriteByte((byte)b.Flags);
            if (b.ExtraBytes > 0) { ms.WriteByte((byte)(b.ExtraValue >> 8)); ms.WriteByte((byte)b.ExtraValue); }
            for (int c = b.FirstChunk; c < b.FirstChunk + b.ChunkCount; c++)
            {
                var ch = f.Chunks[c];
                ms.Write(WriteChunkHeader(ch.StreamIndex, ch.End, ch.EndInfo, ch.PayloadLength));
                ms.Write(original, ch.PayloadOffset, ch.PayloadLength);
            }
            if (b.PaddingBytes > 0) ms.WriteByte(0);
            if ((b.Flags & 1) == 0)
                while (ms.Length - start < blockSize) ms.WriteByte(0);
        }
        return ms.ToArray();
    }
}
