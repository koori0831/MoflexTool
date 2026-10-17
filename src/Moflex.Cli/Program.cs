using Moflex.Core.Container;
using Moflex.Core.Pipeline;
using Moflex.Core.Video;

namespace Moflex.Cli;

internal static class Program
{
    private const string Usage = """
        moflex - MOFLEX (Mobiclip) toolkit

        Usage:
          moflex info <file.moflex>                       Streams, frame rate, stereo layout, sizes
          moflex export-audio <file.moflex> <out.wav>     Decode the audio stream to WAV
          moflex export-video <file.moflex> <out.yuv> [--max-frames N]
                                                          Decode the video to raw YUV 4:2:0 (all stored frames)
          moflex encode <input> -o <out.moflex> [options] Encode a video file (via FFmpeg) or raw .yuv
          moflex remux <file.moflex> <out.moflex>         Rebuild a file from its packets with the muxer
          moflex verify <file.moflex>                     Check that the muxer reproduces the file byte for byte
                                                          and that every video frame decodes

        Encode options:
          --like <ref.moflex>     copy size, frame rate, 3D layout and audio format from an existing movie
          --size WxH              frame size (when --like is not used)
          --fps A/B               input frame rate (default: derived from --like, else 30000/1001)
          --stereo none|duplicate|interleaved
                                  2D, same picture for both eyes, or input already alternates L/R frames
          --sbs                   input is side-by-side 3D (left half = left eye)
          -q N                    key-frame quantiser 12..63 (lower = better, default 30)
          --pq N                  predicted-frame quantiser 12..161 (default 32)
          --gop N                 maximum distance between key frames (default 240)
          --audio <file.wav>      use this audio instead of the input's audio track
          --no-audio              write no audio stream
          --pcm                   store audio as 16-bit PCM instead of IMA-ADPCM
          --rate HZ               audio sample rate (default: from --like, else 47864)
          --max-frames N          encode only the first N input frames
          --ffmpeg <path>         FFmpeg executable used to decode non-.yuv input (default: ffmpeg on PATH)

        Developer commands:
          packets | blocks | chunks | frames | features <file>   container/bitstream dumps
          mix <video-src.moflex> <audio-src.moflex> <out>        combine streams of two files
          enc-test <raw.yuv> <w> <h> <frames> <q> [gop] [pq]     encoder round-trip statistics

        """;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[0] is "-h" or "--help") { Console.Write(Usage); return args.Length == 0 ? 1 : 0; }
            switch (args[0])
            {
                case "encode": return EncodeCommand.Run(args);
                case "enc-test": return EncTest(args);
            }
            var file = MoflexFile.Load(args[1]);
            switch (args[0])
            {
                case "info": Console.Write(MoflexExport.Describe(file)); return 0;
                case "export-audio":
                {
                    using var o = File.Create(Need(args, 2, "output .wav"));
                    MoflexExport.WriteWav(file, o);
                    return 0;
                }
                case "export-video":
                {
                    int max = Option(args, "--max-frames") is { } m ? int.Parse(m) : int.MaxValue;
                    using var o = File.Create(Need(args, 2, "output .yuv"));
                    var v = file.Streams.First(x => x.Kind == StreamKind.Video);
                    int n = MoflexExport.WriteYuv(file, o, max);
                    Console.WriteLine($"{n} frames, {v.Width}x{v.Height} yuv420p");
                    return 0;
                }
                case "remux":
                    File.WriteAllBytes(Need(args, 2, "output .moflex"), MoflexRemux.Rebuild(file, File.ReadAllBytes(args[1])));
                    return 0;
                case "verify": return Verify(file, File.ReadAllBytes(args[1]));
                case "packets":
                    foreach (var p in file.Packets)
                    {
                        string e = p.EndInfo is { } ei ? $"{ei.Len1}:{ei.Value1}:{ei.Flag}:{ei.Len2}:{ei.Value2}" : "";
                        Console.WriteLine($"{p.StreamIndex},{(p.IsKey ? "K" : "-")},{p.Data.Length},{p.Position},{p.ChunkCount},{e}");
                    }
                    return 0;
                case "blocks":
                    foreach (var b in file.Blocks)
                        Console.WriteLine($"{b.Offset},hdr={b.HeaderLength},flags=0x{b.Flags:X2},chunks={b.ChunkCount},term={b.PaddingBytes}");
                    return 0;
                case "chunks":
                    foreach (var c in file.Chunks) Console.WriteLine($"{c.BlockIndex},{file.Blocks[c.BlockIndex].Flags},{c.StreamIndex},{(c.End ? 1 : 0)},{c.PayloadLength}");
                    return 0;
                case "frames": Frames(file); return 0;
                case "features": Features(file, args.Length > 2 ? int.Parse(args[2]) : int.MaxValue); return 0;
                case "mix": return Mix(file, args);
                default: Console.Error.WriteLine($"Unknown command '{args[0]}'."); Console.Write(Usage); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static string Need(string[] a, int i, string what) =>
        i < a.Length ? a[i] : throw new ArgumentException($"Missing argument: {what}.");

    private static string? Option(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static int Verify(MoflexFile file, byte[] original)
    {
        var rebuilt = MoflexRemux.Rebuild(file, original);
        bool same = rebuilt.AsSpan().SequenceEqual(original);
        Console.WriteLine(same ? "container: muxer reproduces the file byte for byte" : "container: rebuilt file differs (layout not produced by the reference encoder)");
        var v = file.Streams.First(x => x.Kind == StreamKind.Video);
        var dec = new MobiclipDecoder(v.Width, v.Height);
        int n = 0;
        foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index)) { dec.Decode(p.Data); n++; }
        Console.WriteLine($"video: {n} frames decoded");
        return same ? 0 : 3;
    }

    private static void Frames(MoflexFile file)
    {
        var v = file.Streams.First(x => x.Kind == StreamKind.Video);
        int n = 0;
        foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index))
        {
            var d = p.Data;
            int Bit(int i) { int by = (i >> 3) ^ 1; return by < d.Length ? (d[by] >> (7 - (i & 7))) & 1 : 0; }
            int Bits(int s, int c) { int r = 0; for (int k = 0; k < c; k++) r = (r << 1) | Bit(s + k); return r; }
            Console.WriteLine(Bit(0) == 1 ? $"{n},I,moflex={Bit(1)},table={Bit(2)},q={Bits(3, 6)},size={d.Length}" : $"{n},P,size={d.Length}");
            n++;
        }
    }

    private static void Features(MoflexFile file, int max)
    {
        var v = file.Streams.First(x => x.Kind == StreamKind.Video);
        MobiclipDecoder.Stats = new();
        var dec = new MobiclipDecoder(v.Width, v.Height);
        int n = 0, maxPkt = 0;
        foreach (var p in file.Packets.Where(p => p.StreamIndex == v.Index))
        {
            if (n++ >= max) break;
            maxPkt = Math.Max(maxPkt, p.Data.Length);
            dec.Decode(p.Data);
        }
        foreach (var kv in MobiclipDecoder.Stats.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"{kv.Key}\t{kv.Value}");
        Console.WriteLine($"maxPacket\t{maxPkt}");
        MobiclipDecoder.Stats = null;
    }

    private static int Mix(MoflexFile file, string[] args)
    {
        var fa = MoflexFile.Load(Need(args, 2, "audio source"));
        var pv = MoflexRemux.Packets(file, File.ReadAllBytes(args[1]), out int frames, out long dur);
        var pa = MoflexRemux.Packets(fa, File.ReadAllBytes(args[2]), out _, out _);
        int vIdx = file.Streams.First(x => x.Kind == StreamKind.Video).Index, aIdx = fa.Streams.First(x => x.Kind == StreamKind.Audio).Index;
        var all = pv.Where(p => p.StreamIndex == vIdx).Concat(pa.Where(p => p.StreamIndex == aIdx)).ToList();
        var hdr = new MoflexHeader
        {
            Timestamp = 1, BlockSize = file.Header.BlockSize,
            Streams = file.Streams.Where(s => s.Kind != StreamKind.Audio).Concat(fa.Streams.Where(s => s.Kind == StreamKind.Audio)).OrderBy(s => s.Index).ToList(),
        };
        File.WriteAllBytes(Need(args, 3, "output"), MoflexMuxer.Mux(hdr, all, new MuxOptions { TotalFrames = frames, DurationUs = dur }));
        return 0;
    }

    private static int EncTest(string[] args)
    {
        int w = int.Parse(args[2]), h = int.Parse(args[3]), cnt = int.Parse(args[4]), q = int.Parse(args[5]);
        int gop = args.Length > 6 ? int.Parse(args[6]) : 1, pq = args.Length > 7 ? int.Parse(args[7]) : q;
        int fs = w * h * 3 / 2;
        var all = File.ReadAllBytes(args[1]);
        var enc = new MobiclipEncoder(w, h, q);
        var dec = new MobiclipDecoder(w, h);
        long total = 0; double psnrSum = 0; bool match = true;
        for (int i = 0; i < cnt; i++)
        {
            var f = new YuvFrame { Width = w, Height = h, Y = all[(i * fs)..(i * fs + w * h)], U = all[(i * fs + w * h)..(i * fs + w * h * 5 / 4)], V = all[(i * fs + w * h * 5 / 4)..((i + 1) * fs)] };
            var pkt = i % gop == 0 ? enc.EncodeKeyFrame(f, out var rec) : enc.EncodePredictedFrame(f, pq, out rec);
            var d = dec.Decode(pkt);
            if (!d.Y.SequenceEqual(rec.Y) || !d.U.SequenceEqual(rec.U) || !d.V.SequenceEqual(rec.V)) match = false;
            double se = 0;
            for (int k = 0; k < w * h; k++) { int e = f.Y[k] - d.Y[k]; se += e * e; }
            psnrSum += 10 * Math.Log10(255.0 * 255 * w * h / Math.Max(se, 1));
            total += pkt.Length;
        }
        Console.WriteLine($"frames={cnt} avgBytes={total / cnt} avgLumaPSNR={psnrSum / cnt:F2} decoderMatchesEncoderRecon={match}");
        return 0;
    }
}
