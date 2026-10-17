using Moflex.Core.Audio;
using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Core;

public enum AudioFormat { None, ImaAdpcm, Pcm16 }

public sealed class MovieEncodeOptions
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Frame duration is FrameDurNum / FrameDurDen seconds (both must fit in 16 bits).</summary>
    public int FrameDurNum { get; set; } = 1001;
    public int FrameDurDen { get; set; } = 30000;
    /// <summary>Quantiser for key frames (12..63).</summary>
    public int Quantizer { get; set; } = 30;
    /// <summary>Quantiser for predicted frames (12..161).</summary>
    public int PredictedQuantizer { get; set; } = 32;
    /// <summary>Maximum distance between key frames (1 = all key frames).</summary>
    public int MaxKeyInterval { get; set; } = 240;
    /// <summary>Mean absolute luma difference between frames above which a key frame is forced (scene cut).</summary>
    public double SceneCutThreshold { get; set; } = 28;
    public AudioFormat Audio { get; set; } = AudioFormat.ImaAdpcm;
    public int SampleRate { get; set; } = 32000;
    public int Channels { get; set; } = 2;
}

/// <summary>Encodes raw frames and PCM into a complete MOFLEX file.</summary>
public static class MovieEncoder
{
    private static double MeanAbsDiff(YuvFrame a, YuvFrame b)
    {
        long s = 0;
        for (int i = 0; i < a.Y.Length; i += 3) s += Math.Abs(a.Y[i] - b.Y[i]);
        return s / (double)((a.Y.Length + 2) / 3);
    }

    public static byte[] Encode(IEnumerable<YuvFrame> frames, short[][]? pcm, MovieEncodeOptions o)
    {
        var streams = new List<StreamInfo>
        {
            new StreamInfo
            {
                Index = 0, Kind = StreamKind.Video, DescriptorType = 3, CodecId = 0, Width = o.Width, Height = o.Height,
                TimeBaseNum = o.FrameDurNum, TimeBaseDen = o.FrameDurDen, Reserved = new byte[] { 1, 1, 0 },
            },
        };
        bool audio = o.Audio != AudioFormat.None && pcm != null;
        if (audio)
            streams.Add(new StreamInfo
            {
                Index = 1, Kind = StreamKind.Audio, DescriptorType = 2, CodecId = o.Audio == AudioFormat.ImaAdpcm ? 1 : 2,
                SampleRate = o.SampleRate, Channels = o.Channels, Reserved = Array.Empty<byte>(),
            });
        var header = new MoflexHeader { Timestamp = 1, BlockSize = 4096, Streams = streams };

        var packets = new List<MuxPacket>();
        var enc = new MobiclipEncoder(o.Width, o.Height, o.Quantizer);
        long n = 0;
        YuvFrame? prev = null;
        int sinceKey = 0;
        foreach (var f in frames)
        {
            bool key = prev == null || sinceKey >= o.MaxKeyInterval || MeanAbsDiff(prev, f) > o.SceneCutThreshold;
            var data = key ? enc.EncodeKeyFrame(f, out _) : enc.EncodePredictedFrame(f, o.PredictedQuantizer, out _);
            sinceKey = key ? 1 : sinceKey + 1;
            prev = f;
            packets.Add(new MuxPacket
            {
                StreamIndex = 0, Data = data, Value1 = 1,
                TimestampUs = (long)Math.Round(n * 1e6 * o.FrameDurNum / o.FrameDurDen),
            });
            n++;
        }
        if (audio)
        {
            int total = pcm![0].Length;
            if (o.Audio == AudioFormat.ImaAdpcm)
            {
                const int per = 1536; // samples per packet, as in existing files
                int padded = (total + per - 1) / per * per;
                var src = pcm.Select(c => { var a = new short[padded]; Array.Copy(c, a, total); return a; }).ToArray();
                var st = new ImaMoflex.State[o.Channels];
                for (int off = 0, i = 0; off < padded; off += per, i++)
                    packets.Add(new MuxPacket
                    {
                        StreamIndex = 1, Data = ImaMoflex.EncodePacket(src, off, per, st, i == 0),
                        TimestampUs = (long)Math.Round(off * 1e6 / o.SampleRate),
                    });
            }
            else
            {
                const int per = 1024;
                for (int off = 0; off < total; off += per)
                {
                    int cnt = Math.Min(per, total - off);
                    var b = new byte[cnt * o.Channels * 2];
                    for (int i = 0; i < cnt; i++)
                        for (int c = 0; c < o.Channels; c++)
                        {
                            short s = pcm[c][off + i];
                            b[(i * o.Channels + c) * 2] = (byte)s; b[(i * o.Channels + c) * 2 + 1] = (byte)(s >> 8);
                        }
                    packets.Add(new MuxPacket { StreamIndex = 1, Data = b, TimestampUs = (long)Math.Round(off * 1e6 / o.SampleRate) });
                }
            }
        }
        // interleave by time (video first on ties)
        var ordered = packets.Select((p, i) => (p, i)).OrderBy(t => t.p.TimestampUs).ThenBy(t => t.p.StreamIndex).ThenBy(t => t.i).Select(t => t.p).ToList();
        return MoflexMuxer.Mux(header, ordered);
    }
}
