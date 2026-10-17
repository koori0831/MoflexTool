namespace Moflex.Core.Container;

/// <summary>A complete packet to be placed in a MOFLEX file.</summary>
public sealed class MuxPacket
{
    public int StreamIndex { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>Presentation time in microseconds (written to the end-of-packet timing field).</summary>
    public long TimestampUs { get; init; }
    public int Value1 { get; init; }
    public int Flag { get; init; }
}

public sealed class MuxOptions
{
    /// <summary>Flags byte written at the start of every block (bit0 = blocks follow each other without padding).</summary>
    public int BlockFlags { get; set; } = 0x19;
    /// <summary>Sync-header timestamp field.</summary>
    public ulong SyncTimestamp { get; set; } = 1;
}

/// <summary>Builds MOFLEX files from scratch: header, 4096-byte blocks, chunk splitting across blocks.</summary>
public static class MoflexMuxer
{
    private static EndInfo MakeEnd(MuxPacket p)
    {
        long v = Math.Max(0, p.TimestampUs);
        int len2 = 1;
        while (len2 < 32 && v >= (1L << (len2 * 2 + 26))) len2++;
        int v1 = p.Value1;
        int len1 = MoflexWriter.BitLength(v1);
        return new EndInfo(len1, (ulong)v1, p.Flag, len2, (ulong)v);
    }

    public static byte[] Mux(MoflexHeader header, IReadOnlyList<MuxPacket> packets, MuxOptions? options = null)
    {
        options ??= new MuxOptions();
        int blockSize = header.BlockSize;
        var syncHeader = MoflexWriter.WriteSyncHeader(new MoflexHeader
        {
            Timestamp = options.SyncTimestamp, BlockSize = header.BlockSize, Streams = header.Streams,
        });
        var ms = new MemoryStream();
        long blockStart = 0;

        void StartBlock()
        {
            blockStart = ms.Length;
            if (blockStart == 0) ms.Write(syncHeader);
            ms.WriteByte((byte)options.BlockFlags);
        }

        StartBlock();
        foreach (var p in packets)
        {
            var end = MakeEnd(p);
            int endLen = MoflexWriter.WriteChunkHeader(p.StreamIndex, true, end, 1).Length;
            int midLen = MoflexWriter.WriteChunkHeader(p.StreamIndex, false, null, 1).Length;
            int off = 0;
            while (true)
            {
                long remaining = blockStart + blockSize - ms.Length;
                if (remaining <= 0) { StartBlock(); continue; }
                int r = p.Data.Length - off;
                if (r <= 8192 && endLen + r <= remaining)
                {
                    ms.Write(MoflexWriter.WriteChunkHeader(p.StreamIndex, true, end, r));
                    ms.Write(p.Data, off, r);
                    break;
                }
                int n = (int)Math.Min(Math.Min(remaining - midLen, 8192), r - 1);
                if (n >= 16 || (n >= 1 && remaining - midLen == n))
                {
                    ms.Write(MoflexWriter.WriteChunkHeader(p.StreamIndex, false, null, n));
                    ms.Write(p.Data, off, n);
                    off += n;
                    continue;
                }
                ms.WriteByte(0); // terminator: the next block follows immediately
                StartBlock();
            }
        }
        if (blockStart + blockSize - ms.Length > 0) ms.WriteByte(0);
        return ms.ToArray();
    }
}
