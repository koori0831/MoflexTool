using Moflex.Core.Video;
using Xunit;

namespace Moflex.Tests;

public class VideoTests
{
    private static YuvFrame Synthetic(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var f = new YuvFrame { Width = w, Height = h, Y = new byte[w * h], U = new byte[w * h / 4], V = new byte[w * h / 4] };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f.Y[y * w + x] = (byte)Math.Clamp(60 + x * 2 + y + 40 * Math.Sin(x * 0.2) + rnd.Next(-3, 4), 0, 255);
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w / 2; x++)
            {
                f.U[y * (w / 2) + x] = (byte)(100 + x); f.V[y * (w / 2) + x] = (byte)(150 - y);
            }
        return f;
    }

    [Fact]
    public void KeyFrameDecodesToEncoderReconstruction()
    {
        var enc = new MobiclipEncoder(64, 48, 28);
        var dec = new MobiclipDecoder(64, 48);
        for (int i = 0; i < 8; i++) // more than the 6-picture ring
        {
            var src = Synthetic(64, 48, i);
            var pkt = enc.EncodeKeyFrame(src, out var rec);
            var d = dec.Decode(pkt);
            Assert.Equal(rec.Y, d.Y); Assert.Equal(rec.U, d.U); Assert.Equal(rec.V, d.V);
            double se = 0;
            for (int k = 0; k < src.Y.Length; k++) { int e = src.Y[k] - d.Y[k]; se += e * e; }
            Assert.True(10 * Math.Log10(255.0 * 255 * src.Y.Length / Math.Max(se, 1)) > 30);
        }
    }

    [Fact]
    public void PredictedFramesDecodeToEncoderReconstruction()
    {
        var enc = new MobiclipEncoder(64, 48, 24);
        var dec = new MobiclipDecoder(64, 48);
        var big = Synthetic(96, 48, 1);
        for (int i = 0; i < 9; i++)
        {
            // pan the picture by 1.5 px per frame
            var f = new YuvFrame { Width = 64, Height = 48, Y = new byte[64 * 48], U = new byte[64 * 12], V = new byte[64 * 12] };
            for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++) f.Y[y * 64 + x] = big.Y[y * 96 + x + (i * 3) / 2];
            for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++) { f.U[y * 32 + x] = big.U[y * 48 + x + i / 2]; f.V[y * 32 + x] = big.V[y * 48 + x + i / 2]; }
            var pkt = i == 0 ? enc.EncodeKeyFrame(f, out var rec) : enc.EncodePredictedFrame(f, 26, out rec);
            var d = dec.Decode(pkt);
            Assert.Equal(rec.Y, d.Y); Assert.Equal(rec.U, d.U); Assert.Equal(rec.V, d.V);
        }
    }
}
