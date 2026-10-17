namespace Moflex.Core.Container;

/// <summary>Parses a MOFLEX file into streams, chunks and reassembled packets.</summary>
public sealed class MoflexFile
{
    public MoflexHeader Header { get; private init; } = null!;
    public IReadOnlyList<StreamInfo> Streams => Header.Streams;
    public IReadOnlyList<Chunk> Chunks { get; private init; } = Array.Empty<Chunk>();
    public IReadOnlyList<Packet> Packets { get; private init; } = Array.Empty<Packet>();
    public IReadOnlyList<BlockInfo> Blocks { get; private init; } = Array.Empty<BlockInfo>();

    public sealed record BlockInfo(long Offset, int HeaderLength, int Flags, int ExtraBytes, int FirstChunk, int ChunkCount, int PaddingBytes, int ExtraValue = 0);

    public static MoflexFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static int ReadVarInt(ReadOnlySpan<byte> d, ref int pos)
    {
        int value = 0;
        for (int i = 0; i < 3; i++)
        {
            int b = d[pos++];
            if ((b & 0x80) == 0) return value | b;
            value = (value | (b & 0x7F)) << 7;
        }
        return value | d[pos++];
    }

    private static bool IsSync(ReadOnlySpan<byte> d, int pos) => pos + 2 <= d.Length && d[pos] == 0x4C && d[pos + 1] == 0x32;

    public static MoflexHeader ParseSyncHeader(ReadOnlySpan<byte> d, int start)
    {
        if (!IsSync(d, start)) throw new InvalidDataException("Missing 'L2' sync header.");
        int pos = start + 4;
        ulong ts = 0;
        for (int i = 0; i < 8; i++) ts = (ts << 8) | d[pos++];
        int blockSize = ((d[pos] << 8) | d[pos + 1]) + 1; pos += 2;
        var streams = new List<StreamInfo>();
        while (pos < d.Length)
        {
            int type = ReadVarInt(d, ref pos);
            int size = ReadVarInt(d, ref pos);
            if (type == 0) { pos += size; break; }
            int p = pos;
            switch (type)
            {
                case 1:
                case 3:
                {
                    int idx = d[p++]; int codec = d[p++];
                    int den = (d[p] << 8) | d[p + 1]; p += 2;
                    int num = (d[p] << 8) | d[p + 1]; p += 2;
                    int w = (d[p] << 8) | d[p + 1]; p += 2;
                    int h = (d[p] << 8) | d[p + 1]; p += 2;
                    streams.Add(new StreamInfo
                    {
                        Index = idx, Kind = StreamKind.Video, DescriptorType = type, CodecId = codec,
                        TimeBaseNum = num, TimeBaseDen = den, Width = w, Height = h,
                        Reserved = d.Slice(p, size - (p - pos)).ToArray(),
                    });
                    break;
                }
                case 2:
                {
                    int idx = d[p++]; int codec = d[p++];
                    int sr = ((d[p] << 16) | (d[p + 1] << 8) | d[p + 2]) + 1; p += 3;
                    int ch = d[p++] + 1;
                    streams.Add(new StreamInfo
                    {
                        Index = idx, Kind = StreamKind.Audio, DescriptorType = type, CodecId = codec,
                        SampleRate = sr, Channels = ch,
                        Reserved = d.Slice(p, size - (p - pos)).ToArray(),
                    });
                    break;
                }
                case 4:
                {
                    int idx = d[p++];
                    streams.Add(new StreamInfo
                    {
                        Index = idx, Kind = StreamKind.Data, DescriptorType = type,
                        Reserved = d.Slice(p, size - (p - pos)).ToArray(),
                    });
                    break;
                }
                default:
                    throw new InvalidDataException($"Unknown stream descriptor type {type}.");
            }
            pos += size;
        }
        return new MoflexHeader { Timestamp = ts, BlockSize = blockSize, Streams = streams, Length = pos - start };
    }

    public static MoflexFile Parse(byte[] data)
    {
        var header = ParseSyncHeader(data, 0);
        var chunks = new List<Chunk>();
        var packets = new List<Packet>();
        var blocks = new List<BlockInfo>();
        var pending = new Dictionary<int, MemoryStream>();
        var pendingFirst = new Dictionary<int, int>();
        int blockSize = header.BlockSize;
        long pos = 0;
        int blockIndex = 0;
        while (pos < data.Length)
        {
            long blockStart = pos;
            int hdrLen = 0;
            if (IsSync(data, (int)pos))
            {
                var h = ParseSyncHeader(data, (int)pos);
                hdrLen = h.Length;
                pos += hdrLen;
                blockSize = h.BlockSize;
            }
            int flags = data[pos++];
            int extra = 0;
            int extraValue = 0;
            if ((flags & 2) != 0) { extraValue = (data[pos] << 8) | data[pos + 1]; pos += 2; extra = 2; }
            int firstChunk = chunks.Count;
            long blockEnd = blockStart + blockSize;
            bool terminated = false;
            while (pos < blockEnd && pos < data.Length)
            {
                if (data[pos] == 0) { terminated = true; pos++; break; }
                long chunkOffset = pos;
                var br = new BitReaderMsb(data, (int)pos);
                int bits = br.ReadUnaryLength();
                int stream = (int)br.ReadBits(bits);
                bool end = br.ReadBit() == 1;
                EndInfo? ei = null;
                if (end)
                {
                    int l1 = br.ReadUnaryLength(); ulong v1 = br.ReadBits(l1);
                    int fl = br.ReadBit();
                    int l2 = br.ReadUnaryLength(); ulong v2 = br.ReadBits(l2 * 2 + 26);
                    ei = new EndInfo(l1, v1, fl, l2, v2);
                }
                int size = (int)br.ReadBits(13) + 1;
                int payload = br.AlignedBytePosition;
                if (payload + size > data.Length) throw new InvalidDataException("Chunk runs past end of file.");
                chunks.Add(new Chunk { StreamIndex = stream, End = end, EndInfo = ei, Offset = chunkOffset, PayloadOffset = payload, PayloadLength = size, BlockIndex = blockIndex });
                if (!pending.TryGetValue(stream, out var ms)) { ms = new MemoryStream(); pending[stream] = ms; }
                if (ms.Length == 0) pendingFirst[stream] = chunks.Count - 1;
                ms.Write(data, payload, size);
                pos = payload + size;
                if (end && ms.Length > 0)
                {
                    var bytes = ms.ToArray();
                    var kind = header.Streams.FirstOrDefault(s => s.Index == stream)?.Kind ?? StreamKind.Data;
                    packets.Add(new Packet
                    {
                        StreamIndex = stream, Data = bytes, IsKey = kind != StreamKind.Video || (bytes[0] & 0x80) != 0,
                        Position = blockStart, EndInfo = ei, FirstChunk = pendingFirst[stream], ChunkCount = chunks.Count - pendingFirst[stream],
                    });
                    ms.SetLength(0);
                }
            }
            long consumedEnd = pos;
            blocks.Add(new BlockInfo(blockStart, hdrLen, flags, extra, firstChunk, chunks.Count - firstChunk,
                terminated ? 1 : 0, extraValue));
            // Odd flags: the next block starts right after the last consumed byte;
            // even flags: skip to the declared block end.
            if ((flags & 1) == 0) pos = blockEnd;
            blockIndex++;
        }
        return new MoflexFile { Header = header, Chunks = chunks, Packets = packets, Blocks = blocks };
    }
}
