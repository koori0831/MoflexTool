namespace Moflex.Core.Container;

/// <summary>Rebuilds a file from its packets with <see cref="MoflexMuxer"/> (used as a self-test of the layout rules).</summary>
public static class MoflexRemux
{
    public static byte[] Rebuild(MoflexFile file, byte[] original)
    {
        int videoStream = file.Streams.First(x => x.Kind == StreamKind.Video).Index;
        var syncTimes = new Dictionary<long, long>();
        foreach (var b in file.Blocks.Where(b => b.HeaderLength > 0))
        {
            ulong ts = 0;
            for (int i = 0; i < 8; i++) ts = (ts << 8) | original[b.Offset + 4 + i];
            syncTimes[b.Offset] = (long)ts;
        }
        long segT = 1;
        int frame = 0, lastBlock = -1;
        var packets = new List<MuxPacket>();
        foreach (var p in file.Packets)
        {
            if (file.Streams.First(x => x.Index == p.StreamIndex).Kind == StreamKind.Data) continue;
            int block = file.Chunks[p.FirstChunk].BlockIndex;
            for (int bi = lastBlock + 1; bi <= block; bi++)
                if (file.Blocks[bi].HeaderLength > 0) segT = syncTimes[file.Blocks[bi].Offset];
            lastBlock = Math.Max(lastBlock, block);
            var ei = p.EndInfo!.Value;
            long rel = ei.Flag == 1 ? -(long)ei.Value2 : (long)ei.Value2;
            bool video = p.StreamIndex == videoStream;
            packets.Add(new MuxPacket { StreamIndex = p.StreamIndex, Data = p.Data, TimeUs = segT + rel, IsSync = video && ei.Value1 == 0, FrameIndex = video ? frame : 0 });
            if (video) frame++;
        }
        var table = file.Packets.FirstOrDefault(x => file.Streams.First(y => y.Index == x.StreamIndex).Kind == StreamKind.Data);
        long duration = table != null ? BitConverter.ToUInt32(table.Data, 8) : 0;
        return MoflexMuxer.Mux(file.Header, packets, new MuxOptions { TotalFrames = frame, DurationUs = duration });
    }
}
