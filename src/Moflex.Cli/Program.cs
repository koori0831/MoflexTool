using Moflex.Core.Container;

namespace Moflex.Cli;

internal static class Program
{
    private const string Usage = """
        moflex - MOFLEX (Mobiclip) toolkit

        Usage:
          moflex info <file.moflex>                Show streams and packet statistics
          moflex packets <file.moflex>             List packets as CSV (stream,key,size,pos,chunks,end-info)
          moflex blocks <file.moflex>              List container blocks

        """;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[0] is "-h" or "--help") { Console.Write(Usage); return args.Length == 0 ? 1 : 0; }
            if (args[0] == "encode") return EncodeCommand.Run(args);
            var file = args[0] == "enc-test" ? null! : MoflexFile.Load(args[1]);
            switch (args[0])
            {
                case "info": Info(file); return 0;
                case "packets": Packets(file); return 0;
                case "blocks": Blocks(file); return 0;
                case "audio-wav":
                {
                    var a = file.Streams.First(x => x.Kind == StreamKind.Audio);
                    using var o = File.Create(args[2]);
                    using var bw = new BinaryWriter(o);
                    var pcm = new MemoryStream();
                    foreach (var p in file.Packets.Where(p => p.StreamIndex == a.Index))
                    {
                        if (a.CodecId == 1)
                        {
                            var ch = Moflex.Core.Audio.ImaMoflex.DecodePacket(p.Data, a.Channels);
                            for (int i = 0; i < ch[0].Length; i++) for (int c = 0; c < a.Channels; c++) { pcm.WriteByte((byte)ch[c][i]); pcm.WriteByte((byte)(ch[c][i] >> 8)); }
                        }
                        else if (a.CodecId == 2) pcm.Write(p.Data);
                        else throw new NotSupportedException(a.CodecName);
                    }
                    bw.Write("RIFF"u8); bw.Write((int)(36 + pcm.Length)); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1);
                    bw.Write((short)a.Channels); bw.Write(a.SampleRate); bw.Write(a.SampleRate * a.Channels * 2); bw.Write((short)(a.Channels * 2)); bw.Write((short)16);
                    bw.Write("data"u8); bw.Write((int)pcm.Length); pcm.WriteTo(o);
                    return 0;
                }
                case "video-yuv":
                {
                    var v = file.Streams.First(x => x.Kind == StreamKind.Video);
                    var dec = new Moflex.Core.Video.MobiclipDecoder(v.Width, v.Height);
                    using var o = File.Create(args[2]);
                    int n = 0, max = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
                    foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index))
                    {
                        if (n++ >= max) break;
                        var fr = dec.Decode(p.Data);
                        o.Write(fr.Y); o.Write(fr.U); o.Write(fr.V);
                    }
                    return 0;
                }
                case "enc-test": // moflex enc-test <raw.yuv> <w> <h> <frames> <q>
                {
                    int w = int.Parse(args[2]), h = int.Parse(args[3]), cnt = int.Parse(args[4]), q = int.Parse(args[5]);
                    int fs = w * h * 3 / 2;
                    var all = File.ReadAllBytes(args[1]);
                    var enc = new Moflex.Core.Video.MobiclipEncoder(w, h, q);
                    var dec = new Moflex.Core.Video.MobiclipDecoder(w, h);
                    long total = 0; double psnrSum = 0; bool match = true;
                    for (int i = 0; i < cnt; i++)
                    {
                        var f = new Moflex.Core.Video.YuvFrame { Width = w, Height = h, Y = all[(i * fs)..(i * fs + w * h)], U = all[(i * fs + w * h)..(i * fs + w * h * 5 / 4)], V = all[(i * fs + w * h * 5 / 4)..((i + 1) * fs)] };
                        Moflex.Core.Video.YuvFrame rec;
                        int gop = args.Length > 6 ? int.Parse(args[6]) : 1, pq = args.Length > 7 ? int.Parse(args[7]) : q;
                        var pkt = i % gop == 0 ? enc.EncodeKeyFrame(f, out rec) : enc.EncodePredictedFrame(f, pq, out rec);
                        var d = dec.Decode(pkt);
                        if (!d.Y.SequenceEqual(rec.Y) || !d.U.SequenceEqual(rec.U) || !d.V.SequenceEqual(rec.V)) match = false;
                        double se = 0; for (int k = 0; k < w * h; k++) { int e = f.Y[k] - d.Y[k]; se += e * e; }
                        psnrSum += 10 * Math.Log10(255.0 * 255 * w * h / Math.Max(se, 1));
                        total += pkt.Length;
                    }
                    Console.WriteLine($"frames={cnt} avgBytes={total / cnt} avgLumaPSNR={psnrSum / cnt:F2} decoderMatchesEncoderRecon={match}");
                    return 0;
                }
                case "chunks":
                    foreach (var c in file.Chunks) Console.WriteLine($"{c.BlockIndex},{file.Blocks[c.BlockIndex].Flags},{c.StreamIndex},{(c.End ? 1 : 0)},{c.PayloadLength}");
                    return 0;
                case "remux": // rebuild the file from its packets with the muxer
                    File.WriteAllBytes(args[2], MoflexRemux.Rebuild(file, File.ReadAllBytes(args[1])));
                    return 0;
                case "hdr-check":
                {
                    var orig = File.ReadAllBytes(args[1]);
                    var w = MoflexWriter.WriteSyncHeader(file.Header);
                    Console.WriteLine(w.AsSpan().SequenceEqual(orig.AsSpan(0, file.Header.Length)) ? "header identical" : "header differs: " + Convert.ToHexString(w) + " vs " + Convert.ToHexString(orig.AsSpan(0, file.Header.Length)));
                    return 0;
                }
                case "vstats":
                {
                    var v = file.Streams.First(x => x.Kind == StreamKind.Video);
                    int n = 0;
                    foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index))
                    {
                        var d = p.Data;
                        int Bit(int i) { int by = (i >> 3) ^ 1; return by < d.Length ? (d[by] >> (7 - (i & 7))) & 1 : 0; }
                        int Bits(int s, int c) { int r = 0; for (int k = 0; k < c; k++) r = (r << 1) | Bit(s + k); return r; }
                        if (Bit(0) == 1) Console.WriteLine($"{n},I,moflex={Bit(1)},dct={Bit(2)},q={Bits(3, 6)},size={d.Length}");
                        else Console.WriteLine($"{n},P,size={d.Length}");
                        n++;
                    }
                    return 0;
                }
                case "features": // bitstream feature usage counters
                {
                    var v = file.Streams.First(x => x.Kind == StreamKind.Video);
                    Moflex.Core.Video.MobiclipDecoder.Stats = new();
                    var dec = new Moflex.Core.Video.MobiclipDecoder(v.Width, v.Height);
                    int max = args.Length > 2 ? int.Parse(args[2]) : int.MaxValue, n = 0, maxPkt = 0;
                    foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index)) { if (n++ >= max) break; maxPkt = Math.Max(maxPkt, p.Data.Length); dec.Decode(p.Data); }
                    foreach (var kv in Moflex.Core.Video.MobiclipDecoder.Stats.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"{kv.Key}	{kv.Value}");
                    Console.WriteLine($"maxPacket	{maxPkt}");
                    return 0;
                }
                case "mix": // mix <videoSource> <audioSource> <out>: video packets of one file + audio packets of another
                {
                    var fa = MoflexFile.Load(args[2]);
                    var pv = MoflexRemux.Packets(file, File.ReadAllBytes(args[1]), out int frames, out long dur);
                    var pa = MoflexRemux.Packets(fa, File.ReadAllBytes(args[2]), out _, out _);
                    int vIdx = file.Streams.First(x => x.Kind == StreamKind.Video).Index, aIdx = fa.Streams.First(x => x.Kind == StreamKind.Audio).Index;
                    var all = pv.Where(p => p.StreamIndex == vIdx).Concat(pa.Where(p => p.StreamIndex == aIdx)).ToList();
                    var hdr = new MoflexHeader { Timestamp = 1, BlockSize = file.Header.BlockSize, Streams = file.Streams.Where(s => s.Kind != StreamKind.Audio).Concat(fa.Streams.Where(s => s.Kind == StreamKind.Audio)).OrderBy(s => s.Index).ToList() };
                    File.WriteAllBytes(args[3], MoflexMuxer.Mux(hdr, all, new MuxOptions { TotalFrames = frames, DurationUs = dur }));
                    return 0;
                }
                case "remux-check":
                {
                    var orig = File.ReadAllBytes(args[1]);
                    var outp = MoflexWriter.Rewrite(file, orig);
                    int diff = -1;
                    for (int i = 0; i < Math.Min(orig.Length, outp.Length); i++) if (orig[i] != outp[i]) { diff = i; break; }
                    Console.WriteLine($"orig={orig.Length} out={outp.Length} firstDiff={diff}");
                    return diff < 0 && orig.Length == outp.Length ? 0 : 3;
                }
                default: Console.Error.WriteLine($"Unknown command '{args[0]}'."); Console.Write(Usage); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static void Info(MoflexFile f)
    {
        var counts = f.Packets.GroupBy(p => p.StreamIndex).ToDictionary(g => g.Key, g => (Count: g.Count(), Bytes: g.Sum(p => (long)p.Data.Length)));
        Console.WriteLine($"block size : {f.Header.BlockSize}");
        Console.WriteLine($"blocks     : {f.Blocks.Count}");
        foreach (var s in f.Streams)
        {
            counts.TryGetValue(s.Index, out var c);
            string detail = s.Kind switch
            {
                StreamKind.Video => $"{s.Width}x{s.Height} timebase {s.TimeBaseNum}/{s.TimeBaseDen}",
                StreamKind.Audio => $"{s.SampleRate} Hz, {s.Channels} ch",
                _ => "",
            };
            Console.WriteLine($"stream #{s.Index}: {s.Kind,-5} {s.CodecName,-17} {detail}  packets={c.Count} bytes={c.Bytes}");
        }
    }

    private static void Packets(MoflexFile f)
    {
        foreach (var p in f.Packets)
        {
            string e = p.EndInfo is { } ei ? $"{ei.Len1}:{ei.Value1}:{ei.Flag}:{ei.Len2}:{ei.Value2}" : "";
            Console.WriteLine($"{p.StreamIndex},{(p.IsKey ? "K" : "-")},{p.Data.Length},{p.Position},{p.ChunkCount},{e}");
        }
    }

    private static void Blocks(MoflexFile f)
    {
        foreach (var b in f.Blocks)
            Console.WriteLine($"{b.Offset},hdr={b.HeaderLength},flags=0x{b.Flags:X2},chunks={b.ChunkCount},term={b.PaddingBytes}");
    }
}
