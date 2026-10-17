using Moflex.Core.Audio;
using Xunit;

namespace Moflex.Tests;

public class AudioTests
{
    [Fact]
    public void EncodeDecodeSineIsClose()
    {
        int n = 1536 * 4;
        var pcm = new short[2][];
        for (int c = 0; c < 2; c++)
        {
            pcm[c] = new short[n];
            for (int i = 0; i < n; i++) pcm[c][i] = (short)(12000 * Math.Sin(i * 0.05 * (c + 1)));
        }
        var st = new ImaMoflex.State[2];
        double err = 0, sig = 0;
        for (int p = 0; p < 4; p++)
        {
            var pkt = ImaMoflex.EncodePacket(pcm, p * 1536, 1536, st, p == 0);
            Assert.Equal(8 + 1536, pkt.Length);
            var dec = ImaMoflex.DecodePacket(pkt, 2);
            for (int c = 0; c < 2; c++)
                for (int i = 0; i < 1536; i++)
                {
                    double d = dec[c][i] - pcm[c][p * 1536 + i];
                    err += d * d; sig += (double)pcm[c][p * 1536 + i] * pcm[c][p * 1536 + i];
                }
        }
        Assert.True(10 * Math.Log10(sig / err) > 25);
    }
}
