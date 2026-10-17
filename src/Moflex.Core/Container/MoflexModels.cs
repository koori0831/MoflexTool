namespace Moflex.Core.Container;

public enum StreamKind { Video, Audio, Data }

public sealed class StreamInfo
{
    public int Index { get; init; }
    public StreamKind Kind { get; init; }
    /// <summary>Raw descriptor type (1/3 video, 2 audio, 4 data).</summary>
    public int DescriptorType { get; init; }
    public int CodecId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int TimeBaseNum { get; init; }
    public int TimeBaseDen { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    /// <summary>Reserved/unknown payload bytes of the descriptor (kept for exact re-muxing).</summary>
    public byte[] Reserved { get; init; } = Array.Empty<byte>();

    public string CodecName => Kind switch
    {
        StreamKind.Video => CodecId == 0 ? "mobiclip" : $"video#{CodecId}",
        StreamKind.Audio => CodecId switch { 0 => "fastaudio", 1 => "adpcm_ima_moflex", 2 => "pcm_s16le", _ => $"audio#{CodecId}" },
        _ => "data",
    };
}

public sealed class MoflexHeader
{
    public ulong Timestamp { get; init; }
    public int BlockSize { get; init; }
    public IReadOnlyList<StreamInfo> Streams { get; init; } = Array.Empty<StreamInfo>();
    /// <summary>Total length in bytes of the sync header including descriptors.</summary>
    public int Length { get; init; }
}

/// <summary>Timing fields that follow the end-of-frame flag of a chunk header (meaning only partly known).</summary>
public readonly record struct EndInfo(int Len1, ulong Value1, int Flag, int Len2, ulong Value2);

public sealed class Chunk
{
    public int StreamIndex { get; init; }
    public bool End { get; init; }
    public EndInfo? EndInfo { get; init; }
    public long Offset { get; init; }          // file offset of the chunk header
    public int PayloadOffset { get; init; }
    public int PayloadLength { get; init; }
    public int BlockIndex { get; init; }
}

public sealed class Packet
{
    public int StreamIndex { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public bool IsKey { get; init; }
    public long Position { get; init; }       // start offset of the block containing the last chunk
    public EndInfo? EndInfo { get; init; }
    public int FirstChunk { get; init; }
    public int ChunkCount { get; init; }
}
