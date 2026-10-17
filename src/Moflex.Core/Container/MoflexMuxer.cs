namespace Moflex.Core.Container;

/// <summary>A complete packet to be placed in a MOFLEX file.</summary>
public sealed class MuxPacket
{
    public int StreamIndex { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>Absolute presentation time in microseconds (files conventionally start at 1).</summary>
    public long TimeUs { get; init; }
    /// <summary>Video key frame that starts a new sync segment (sync header, seek-table entry).</summary>
    public bool IsSync { get; init; }
    /// <summary>Index of the video frame (used for the seek table).</summary>
    public int FrameIndex { get; init; }
}

public sealed class MuxOptions
{
    /// <summary>A video packet is written before an audio packet when its time is not later than the audio time plus this lead.</summary>
    public long VideoLeadUs { get; set; } = 16714;
    /// <summary>Total number of video frames (seek table).</summary>
    public int TotalFrames { get; set; }
    /// <summary>Total duration in microseconds (seek table).</summary>
    public long DurationUs { get; set; }
}

/// <summary>
/// Builds MOFLEX files from scratch following the layout of existing files: a header block with the seek table,
/// then 4096-byte blocks; every video key frame starts a block with a sync header; packet times are written
/// relative to the current sync segment.
/// </summary>
public static class MoflexMuxer
{
    private static int NextCounter(int c) => c == 0 ? 3 : c == 63 ? 1 : c + 1;

    private sealed class Writer
    {
        private readonly MemoryStream _ms;
        private readonly MoflexHeader _header;
        private long _blockStart;
        private bool _closed;
        public int Counter;
        public long SegmentTime = 1;

        public Writer(MemoryStream ms, MoflexHeader header) { _ms = ms; _header = header; }

        public long Position => _ms.Length;
        private long Remaining => _blockStart + _header.BlockSize - _ms.Length;

        public void StartBlock(bool sync, long syncTime)
        {
            _blockStart = _ms.Length;
            _closed = false;
            if (sync)
            {
                SegmentTime = syncTime;
                _ms.Write(MoflexWriter.WriteSyncHeader(_header, (ulong)syncTime));
            }
            _ms.WriteByte((byte)((Counter << 2) | 1));
        }

        public void EndBlock()
        {
            if (Remaining > 0 && !_closed) _ms.WriteByte(0);
            _closed = true;
        }

        public void WritePacket(int stream, byte[] data, long relTime, int value1)
        {
            long mag = Math.Abs(relTime);
            int len2 = 1;
            while (len2 < 32 && mag >= (1L << (len2 * 2 + 26))) len2++;
            var end = new EndInfo(MoflexWriter.BitLength(value1), (ulong)value1, relTime < 0 ? 1 : 0, len2, (ulong)mag);
            int endLen = MoflexWriter.WriteChunkHeader(stream, true, end, 1).Length;
            int midLen = MoflexWriter.WriteChunkHeader(stream, false, null, 1).Length;
            int off = 0;
            if (_closed) StartBlock(false, 0);
            while (true)
            {
                long remaining = Remaining;
                if (remaining <= 0) { StartBlock(false, 0); continue; }
                int r = data.Length - off;
                if (r <= 8192 && endLen + r <= remaining)
                {
                    _ms.Write(MoflexWriter.WriteChunkHeader(stream, true, end, r));
                    _ms.Write(data, off, r);
                    return;
                }
                int n = (int)Math.Min(Math.Min(remaining - midLen, 8192), r - 1);
                if (n >= 16 || (n >= 1 && remaining - midLen == n))
                {
                    _ms.Write(MoflexWriter.WriteChunkHeader(stream, false, null, n));
                    _ms.Write(data, off, n);
                    off += n;
                    continue;
                }
                _ms.WriteByte(0);
                StartBlock(false, 0);
            }
        }
    }

    private static byte[] BuildTable(int total, long duration, IReadOnlyList<(int Frame, long Time, long Offset)> syncs)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((uint)syncs.Count); bw.Write((uint)total); bw.Write((uint)duration); bw.Write(0u);
        foreach (var s in syncs) { bw.Write((ulong)s.Frame); bw.Write((ulong)s.Time); bw.Write((ulong)s.Offset); }
        return ms.ToArray();
    }

    public static byte[] Mux(MoflexHeader header, IReadOnlyList<MuxPacket> packets, MuxOptions? options = null)
    {
        options ??= new MuxOptions();
        var videoIdx = header.Streams.Where(s => s.Kind == StreamKind.Video).Select(s => s.Index).ToHashSet();
        var dataStream = header.Streams.FirstOrDefault(s => s.Kind == StreamKind.Data);
        var videos = packets.Where(p => videoIdx.Contains(p.StreamIndex)).ToList();
        var audios = packets.Where(p => !videoIdx.Contains(p.StreamIndex)).ToList();

        // interleave: video first when it is due no later than the audio packet plus the lead
        var order = new List<MuxPacket>(packets.Count);
        int vi = 0, ai = 0;
        while (vi < videos.Count || ai < audios.Count)
        {
            if (ai >= audios.Count || (vi < videos.Count && videos[vi].TimeUs <= audios[ai].TimeUs + options.VideoLeadUs)) order.Add(videos[vi++]);
            else order.Add(audios[ai++]);
        }

        bool IsSyncPacket(MuxPacket p) => videoIdx.Contains(p.StreamIndex) && p.IsSync;
        int syncCount = 0;
        for (int i = 0; i < order.Count; i++) if (IsSyncPacket(order[i]) || i == 0) syncCount++;
        int counter = 0;
        for (int i = 0; i < syncCount; i++) counter = NextCounter(counter);
        int headerCounter = NextCounter(counter);

        var ms = new MemoryStream();
        var w = new Writer(ms, header);

        void WriteHeaderBlock(Writer hw, IReadOnlyList<(int Frame, long Time, long Offset)> syncs)
        {
            hw.Counter = headerCounter;
            hw.StartBlock(true, 1);
            hw.WritePacket(dataStream!.Index, BuildTable(options.TotalFrames, options.DurationUs, syncs), 0, 0);
            hw.EndBlock();
        }

        int headerLen = 0;
        if (dataStream != null)
        {
            WriteHeaderBlock(w, Enumerable.Repeat((0, 0L, 0L), syncCount).ToList());
            headerLen = (int)ms.Length;
        }

        var syncList = new List<(int Frame, long Time, long Offset)>();
        w.Counter = 0;
        for (int i = 0; i < order.Count; i++)
        {
            var p = order[i];
            bool isVideo = videoIdx.Contains(p.StreamIndex);
            bool sync = IsSyncPacket(p) || i == 0;
            if (sync)
            {
                if (i > 0) w.EndBlock();
                w.Counter = NextCounter(w.Counter);
                syncList.Add((p.FrameIndex, p.TimeUs, w.Position));
                w.StartBlock(true, p.TimeUs);
            }
            w.WritePacket(p.StreamIndex, p.Data, p.TimeUs - w.SegmentTime, isVideo ? (sync ? 0 : 1) : 0);
            if (sync) w.EndBlock(); // a sync block holds just the key frame
        }
        w.EndBlock();

        var all = ms.ToArray();
        if (dataStream != null)
        {
            var hms = new MemoryStream();
            WriteHeaderBlock(new Writer(hms, header), syncList);
            if (hms.Length != headerLen) throw new InvalidOperationException("Header block length changed.");
            hms.ToArray().CopyTo(all, 0);
        }
        return all;
    }
}
