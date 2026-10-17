using System.Security.Cryptography;
using Moflex.Core;
using Moflex.Core.Audio;
using Moflex.Core.Container;
using Moflex.Core.Pipeline;
using Moflex.Core.Video;
using Xunit;

namespace Moflex.Tests;

public class FastAudioTests
{
    /// <summary>40 packets of 1..6 sub-frames of pseudo-random bytes (every 40-byte pattern is a valid sub-frame).</summary>
    private static List<byte[]> RandomPackets(int channels)
    {
        uint x = 12345;
        var list = new List<byte[]>();
        for (int p = 0; p < 40; p++)
        {
            var d = new byte[(1 + p % 6) * FastAudio.SubFrameBytes * channels];
            for (int i = 0; i < d.Length; i++) { x = x * 1664525 + 1013904223; d[i] = (byte)(x >> 24); }
            list.Add(d);
        }
        return list;
    }

    [Fact]
    public void DecoderMatchesFfmpegBitExactly()
    {
        // Reference: these packets muxed into a MOFLEX file and decoded with `ffmpeg -i rnd.moflex -f f32le`
        // (FFmpeg's fastaudio decoder) give interleaved float output with this SHA-256.
        const string FfmpegSha256 = "1FD82ECD7EFBEF20A96D7DBC3AF52FA4C106630DB5CC162714C92F767743BFBF";
        var dec = new FastAudioDecoder(2);
        var ms = new MemoryStream();
        foreach (var p in RandomPackets(2))
        {
            var o = dec.DecodePacket(p);
            Assert.Equal(FastAudioDecoder.SamplesInPacket(p.Length, 2), o[0].Length);
            for (int i = 0; i < o[0].Length; i++)
                for (int c = 0; c < 2; c++) ms.Write(BitConverter.GetBytes(o[c][i]));
        }
        Assert.Equal(FfmpegSha256, Convert.ToHexString(SHA256.HashData(ms.ToArray())));
    }

    [Fact]
    public void LatticeMatchesDirectFormAllPoleFilter()
    {
        // Levinson's reflection coefficients k_1..k_8 go to m[8 - i]; the lattice must then equal 1 / A(z)
        // (followed by the decoder's de-emphasis and gain of 2).
        var r = new double[] { 1, 0.9, 0.7, 0.45, 0.2, 0.0, -0.1, -0.15, -0.12 };
        var k = FastAudioEncoder.Levinson(r);
        var m = new float[8];
        for (int i = 0; i < 8; i++) m[7 - i] = (float)k[i];
        // step-up recursion to direct form
        var a = new double[9]; a[0] = 1;
        for (int i = 1; i <= 8; i++)
        {
            var t = (double[])a.Clone();
            for (int j = 1; j < i; j++) a[j] = t[j] + k[i - 1] * t[i - j];
            a[i] = k[i - 1];
        }
        var h = new float[64]; h[0] = 1;
        FastAudio.Synthesize(new FastAudio.ChannelState(), m, h);
        var y = new double[64];
        double last = 0;
        for (int n = 0; n < 64; n++)
        {
            double v = n == 0 ? 1 : 0;
            for (int j = 1; j <= 8 && j <= n; j++) v -= a[j] * y[n - j];
            y[n] = v;
            last = v + 0.86 * last;
            Assert.Equal(2 * last, h[n], 3);
        }
    }

    [Fact]
    public void EncodeDecodeRoundTripIsClose()
    {
        int n = 256 * 60;
        var pcm = new short[2][];
        for (int c = 0; c < 2; c++)
        {
            pcm[c] = new short[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / 32000.0;
                pcm[c][i] = (short)(9000 * Math.Sin(2 * Math.PI * (220 + 300 * c) * t) + 4000 * Math.Sin(2 * Math.PI * 1300 * t * (1 + t)));
            }
        }
        var enc = new FastAudioEncoder(2);
        var dec = new FastAudioDecoder(2);
        double err = 0, sig = 0;
        for (int off = 0; off < n; off += 1536)
        {
            var pkt = enc.EncodePacket(pcm, off, 1536);
            Assert.Equal(6 * 2 * FastAudio.SubFrameBytes, pkt.Length);
            var o = dec.DecodePacket(pkt);
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < 1536; i++)
                {
                    double d = FastAudio.ToPcm16(o[c][i]) - pcm[c][off + i];
                    err += d * d; sig += (double)pcm[c][off + i] * pcm[c][off + i];
                }
        }
        Assert.True(10 * Math.Log10(sig / err) > 15, $"SNR {10 * Math.Log10(sig / err):0.0} dB");
    }

    [Fact]
    public void MovieWithFastAudioParsesAndExports()
    {
        var frames = Enumerable.Range(0, 4).Select(i => new YuvFrame
        {
            Width = 32, Height = 32,
            Y = Enumerable.Repeat((byte)(60 + i * 20), 32 * 32).ToArray(),
            U = Enumerable.Repeat((byte)128, 16 * 16).ToArray(),
            V = Enumerable.Repeat((byte)128, 16 * 16).ToArray(),
        });
        var pcm = new short[1][] { Enumerable.Range(0, 5000).Select(i => (short)(8000 * Math.Sin(i * 0.07))).ToArray() };
        var bytes = MovieEncoder.Encode(frames, pcm, new MovieEncodeOptions
        {
            Width = 32, Height = 32, Audio = AudioFormat.FastAudio, SampleRate = 32000, Channels = 1,
        });
        var f = MoflexFile.Parse(bytes);
        var a = f.Streams.Single(s => s.Kind == StreamKind.Audio);
        Assert.Equal(0, a.CodecId);
        Assert.Equal("fastaudio", a.CodecName);
        var wav = new MemoryStream();
        MoflexExport.WriteWav(f, wav);
        Assert.Equal(44 + (5000 + 255) / 256 * 256 * 2, wav.Length);
    }
}
