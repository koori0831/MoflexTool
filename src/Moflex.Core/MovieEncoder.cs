using Moflex.Core.Audio;
using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Core;

public enum AudioFormat { None, ImaAdpcm, Pcm16 }

/// <summary>How video frames map to eyes.</summary>
public enum StereoMode
{
    /// <summary>Plain 2D video, one frame per time stamp.</summary>
    None,
    /// <summary>Input already alternates left/right frames (two frames share a time stamp), like the game's own movies.</summary>
    Interleaved,
    /// <summary>Each input frame is used for both eyes (flat picture shown in stereoscopic layout).</summary>
    Duplicate,
}

public sealed class MovieEncodeOptions
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Duration of one stored frame is FrameDurNum / FrameDurDen seconds (both must fit in 16 bits).</summary>
    public int FrameDurNum { get; set; } = 1001;
    public int FrameDurDen { get; set; } = 30000;
    /// <summary>Quantiser for key frames (12..63).</summary>
    public int Quantizer { get; set; } = 30;
    /// <summary>Quantiser for predicted frames (12..161).</summary>
    public int PredictedQuantizer { get; set; } = 32;
    /// <summary>Maximum distance between key frames in stored frames (1 = all key frames).</summary>
    public int MaxKeyInterval { get; set; } = 240;
    /// <summary>Mean absolute luma difference between frames above which a key frame is forced (scene cut).</summary>
    public double SceneCutThreshold { get; set; } = 28;
    public StereoMode Stereo { get; set; } = StereoMode.None;
    public AudioFormat Audio { get; set; } = AudioFormat.ImaAdpcm;
    public int SampleRate { get; set; } = 32000;
    public int Channels { get; set; } = 2;
    /// <summary>Called after every encoded stored frame with the number of frames done.</summary>
    public Action<long>? Progress { get; set; }
    /// <summary>Number of key-frame segments encoded at once (0 = one per CPU core). The output does not depend on it.</summary>
    public int Threads { get; set; }
    /// <summary>Write the seek table (data stream) like existing files.</summary>
    public bool SeekTable { get; set; } = true;
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
        bool stereo = o.Stereo != StereoMode.None;
        int group = stereo ? 2 : 1; // stored frames per time stamp
        var streams = new List<StreamInfo>
        {
            new StreamInfo
            {
                Index = 0, Kind = StreamKind.Video, DescriptorType = stereo ? 3 : 1, CodecId = 0, Width = o.Width, Height = o.Height,
                TimeBaseNum = o.FrameDurNum, TimeBaseDen = o.FrameDurDen,
                Reserved = stereo ? new byte[] { 1, 1, 0 } : new byte[] { 1, 1 }, // 0 = left-first interleaved layout
            },
        };
        bool audio = o.Audio != AudioFormat.None && pcm != null;
        if (audio)
            streams.Add(new StreamInfo
            {
                Index = 1, Kind = StreamKind.Audio, DescriptorType = 2, CodecId = o.Audio == AudioFormat.ImaAdpcm ? 1 : 2,
                SampleRate = o.SampleRate, Channels = o.Channels, Reserved = Array.Empty<byte>(),
            });
        if (o.SeekTable)
            streams.Add(new StreamInfo { Index = audio ? 2 : 1, Kind = StreamKind.Data, DescriptorType = 4, Reserved = new byte[] { 0 } });
        var header = new MoflexHeader { Timestamp = 1, BlockSize = 4096, Streams = streams };

        // time stamp of the k-th group of stored frames, microseconds (+1: files start at 1)
        long VideoTime(long frame) => frame / group * (long)group * o.FrameDurNum * 1000000L / o.FrameDurDen + 1;

        // Key frames are chosen from the source alone and nothing is predicted across them, so every
        // key-frame-led segment is encoded independently (and in parallel) with a fresh encoder.
        long done = 0;
        var progressLock = new object();
        List<MuxPacket> EncodeSegment(List<YuvFrame> seg, long start)
        {
            var enc = new MobiclipEncoder(o.Width, o.Height, o.Quantizer) { MaxRefs = stereo ? 2 : 1 };
            var res = new List<MuxPacket>(seg.Count);
            for (int i = 0; i < seg.Count; i++)
            {
                bool key = i == 0;
                var data = key ? enc.EncodeKeyFrame(seg[i], out _) : enc.EncodePredictedFrame(seg[i], o.PredictedQuantizer, out _);
                long k = start + i;
                res.Add(new MuxPacket { StreamIndex = 0, Data = data, TimeUs = VideoTime(k), IsSync = key, FrameIndex = (int)k });
                if (o.Progress != null) lock (progressLock) o.Progress(++done);
            }
            seg.Clear();
            return res;
        }

        int threads = o.Threads > 0 ? o.Threads : Environment.ProcessorCount;
        using var gate = new SemaphoreSlim(threads);
        var segments = new List<Task<List<MuxPacket>>>();
        var current = new List<YuvFrame>();
        long segStart = 0;
        void CloseSegment()
        {
            if (current.Count == 0) return;
            var seg = current; long start = segStart;
            current = new List<YuvFrame>();
            gate.Wait(); // bounds the number of buffered segments
            segments.Add(Task.Run(() => { try { return EncodeSegment(seg, start); } finally { gate.Release(); } }));
        }

        long n = 0;
        YuvFrame? prev = null;
        int sinceKey = 0;
        void PlanOne(YuvFrame f)
        {
            // a scene cut is only allowed on the first frame of a stamp group
            bool canCut = n % group == 0;
            bool key = prev == null || (canCut && (sinceKey >= o.MaxKeyInterval || MeanAbsDiff(prev, f) > o.SceneCutThreshold));
            if (key) { CloseSegment(); segStart = n; }
            current.Add(f);
            sinceKey = key ? 1 : sinceKey + 1;
            prev = f;
            n++;
        }
        foreach (var f in frames)
        {
            PlanOne(f);
            if (o.Stereo == StereoMode.Duplicate) PlanOne(f);
        }
        CloseSegment();
        var packets = new List<MuxPacket>();
        foreach (var t in segments) packets.AddRange(t.GetAwaiter().GetResult());
        long totalFrames = n;
        long duration = (totalFrames + group - 1) / group * (long)group * o.FrameDurNum * 1000000L / o.FrameDurDen;

        if (audio)
        {
            int total = pcm![0].Length;
            int audioStream = 1;
            if (o.Audio == AudioFormat.ImaAdpcm)
            {
                // Packet boundaries follow the video time stamps: one packet per stamp group, rounded to 256-sample sub-frames.
                double perGroup = o.SampleRate * (double)group * o.FrameDurNum / o.FrameDurDen;
                int padded = (total + 255) / 256 * 256;
                var src = pcm.Select(c => { var a = new short[padded]; Array.Copy(c, a, total); return a; }).ToArray();
                var st = new ImaMoflex.State[o.Channels];
                int off = 0;
                for (int i = 1; off < padded; i++)
                {
                    int end = (int)Math.Floor(i * perGroup / 256 + 0.5) * 256;
                    if (end <= off) end = off + 256;
                    if (end > padded || padded - end < 256) end = padded;
                    packets.Add(new MuxPacket
                    {
                        StreamIndex = audioStream, Data = ImaMoflex.EncodePacket(src, off, end - off, st, off == 0),
                        TimeUs = off * 1000000L / o.SampleRate + 1,
                    });
                    off = end;
                }
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
                    packets.Add(new MuxPacket { StreamIndex = audioStream, Data = b, TimeUs = off * 1000000L / o.SampleRate + 1 });
                }
            }
        }
        return MoflexMuxer.Mux(header, packets, new MuxOptions { TotalFrames = (int)totalFrames, DurationUs = duration, VideoLeadUs = o.FrameDurNum * 1000000L / o.FrameDurDen });
    }
}
