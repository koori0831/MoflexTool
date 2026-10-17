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
            var file = MoflexFile.Load(args[1]);
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
