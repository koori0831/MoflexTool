// MOFLEX FastAudio (audio codec 0). The bitstream syntax, the quantiser tables and the synthesis filter
// follow FFmpeg's libavcodec/fastaudio.c (LGPL-2.1-or-later; Florian Nouwt, Adib Surani, Paul B Mahol).
// The decoder is an independent C# rewrite; the encoder is original work.
namespace Moflex.Core.Audio;

/// <summary>
/// FastAudio: a low bit rate LPC codec with regular-pulse excitation. Every 256-sample sub-frame of a channel is
/// 40 bytes (ten little-endian 32-bit words read MSB first): eight reflection coefficients, then four 64-sample
/// groups with a gain, a grid phase and 21 three-bit pulses. See docs/FORMAT-MOFLEX.md.
/// </summary>
public static class FastAudio
{
    public const int SubFrameSamples = 256;
    public const int SubFrameBytes = 40;
    public const int Order = 8;
    public const int GroupSamples = 64;
    public const int Pulses = 21;
    public const float Deemphasis = 0.86f;

    /// <summary>Bits of the coefficient index for table i (table 5 is sent as one bit per group instead).</summary>
    internal static readonly int[] IndexBits = { 6, 6, 5, 5, 4, 0, 3, 3 };

    /// <summary>Quantiser tables: Tables[i] holds the values of reflection coefficient m[7 - i].</summary>
    internal static readonly float[][] Tables = BuildTables();

    private static readonly float GainUnit = (float)Math.Pow(2, 116);

    private static float[][] BuildTables()
    {
        var t = new float[8][];
        var t0 = new float[64];
        for (int i = 0; i < 8; i++) t0[i] = (i - 159.5f) / 160f;
        for (int i = 0; i < 11; i++) t0[i + 8] = (i - 37.5f) / 40f;
        for (int i = 0; i < 27; i++) t0[i + 19] = (i - 13f) / 20f;
        for (int i = 0; i < 11; i++) t0[i + 46] = (i + 27.5f) / 40f;
        for (int i = 0; i < 7; i++) t0[i + 57] = (i + 152.5f) / 160f;
        t[0] = t0;
        t[1] = (float[])t0.Clone();
        t[2] = new float[32];
        for (int i = 0; i < 7; i++) t[2][i] = (i - 33.5f) / 40f;
        for (int i = 0; i < 25; i++) t[2][i + 7] = (i - 13f) / 20f;
        t[3] = new float[32];
        for (int i = 0; i < 32; i++) t[3][i] = -t[2][31 - i];
        t[4] = new float[16];
        for (int i = 0; i < 16; i++) t[4][i] = i * 0.22f / 3f - 0.6f;
        t[5] = new float[16];
        for (int i = 0; i < 16; i++) t[5][i] = i * 0.20f / 3f - 0.3f;
        t[6] = new float[8];
        for (int i = 0; i < 8; i++) t[6][i] = i * 0.36f / 3f - 0.4f;
        t[7] = new float[8];
        for (int i = 0; i < 8; i++) t[7][i] = i * 0.34f / 3f - 0.2f;
        return t;
    }

    /// <summary>Pulse unit of gain index 0..63: the float with bit pattern (index + 1) &lt;&lt; 20, times 2^116.</summary>
    public static float Gain(int index) => BitConverter.Int32BitsToSingle((index + 1) << 20) * GainUnit;

    /// <summary>Decoder state of one channel (lattice filter memory and de-emphasis memory).</summary>
    public sealed class ChannelState
    {
        internal readonly float[] F = new float[Order];
        internal float Last;

        public ChannelState Clone()
        {
            var c = new ChannelState { Last = Last };
            Array.Copy(F, c.F, Order);
            return c;
        }
    }

    /// <summary>
    /// All-pole lattice synthesis followed by de-emphasis and a gain of 2. m[j] is the reflection coefficient of
    /// lattice stage 8 - j. Works in place on <paramref name="buf"/>.
    /// </summary>
    public static void Synthesize(ChannelState ch, ReadOnlySpan<float> m, Span<float> buf)
    {
        var f = ch.F;
        float last = ch.Last;
        for (int i = 0; i < buf.Length; i++)
        {
            float x = buf[i];
            for (int j = 0; j < Order; j++)
            {
                x -= m[j] * f[j];
                f[j] += m[j] * x;
            }
            f[0] = f[1]; f[1] = f[2]; f[2] = f[3]; f[3] = f[4]; f[4] = f[5]; f[5] = f[6]; f[6] = f[7];
            f[7] = x;
            last = x + last * Deemphasis;
            buf[i] = last * 2f;
        }
        ch.Last = last;
    }

    /// <summary>Converts a decoded float sample to 16-bit PCM (round to nearest, clipped).</summary>
    public static short ToPcm16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32768f, MidpointRounding.ToEven), -32768, 32767);
}

/// <summary>Stateful FastAudio decoder (the filter memory carries over from packet to packet).</summary>
public sealed class FastAudioDecoder
{
    private readonly FastAudio.ChannelState[] _ch;

    public FastAudioDecoder(int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        _ch = Enumerable.Range(0, channels).Select(_ => new FastAudio.ChannelState()).ToArray();
    }

    public int Channels => _ch.Length;

    /// <summary>Number of samples per channel in a packet of the given size.</summary>
    public static int SamplesInPacket(int size, int channels) => size / (FastAudio.SubFrameBytes * channels) * FastAudio.SubFrameSamples;

    /// <summary>Decodes one packet to planar float samples (nominal range -1..1).</summary>
    public float[][] DecodePacket(ReadOnlySpan<byte> pkt)
    {
        int channels = _ch.Length;
        int subframes = pkt.Length / (FastAudio.SubFrameBytes * channels);
        if (subframes <= 0) throw new InvalidDataException("FastAudio packet is shorter than one sub-frame.");
        var outp = new float[channels][];
        for (int c = 0; c < channels; c++) outp[c] = new float[subframes * FastAudio.SubFrameSamples];
        Span<uint> words = stackalloc uint[10];
        Span<float> m = stackalloc float[8];
        Span<int> inds = stackalloc int[4];
        Span<int> pads = stackalloc int[4];
        int pos = 0;
        for (int sf = 0; sf < subframes; sf++)
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < 10; i++, pos += 4)
                    words[i] = (uint)(pkt[pos] | pkt[pos + 1] << 8 | pkt[pos + 2] << 16 | pkt[pos + 3] << 24);
                var r = new WordReader(words);
                for (int i = 0; i < 8; i++) m[7 - i] = FastAudio.Tables[i][r.Read(FastAudio.IndexBits[i])];
                for (int i = 0; i < 4; i++) inds[3 - i] = r.Read(6);
                for (int i = 0; i < 4; i++) pads[3 - i] = r.Read(2);
                var buf = outp[c].AsSpan(sf * FastAudio.SubFrameSamples, FastAudio.SubFrameSamples);
                int index5 = 0;
                for (int g = 0; g < 4; g++)
                {
                    float gain = FastAudio.Gain(inds[g]);
                    int tmp = 0;
                    for (int j = 0; j < FastAudio.Pulses; j++)
                    {
                        int v = j == 20 ? tmp >> 1 : r.Read(3);
                        buf[g * 64 + pads[g] + j * 3] = gain * (2 * v - 7);
                        if (j % 10 == 9) tmp = 4 * tmp + r.Read(2);
                    }
                    index5 = Math.Min(2 * index5 + (tmp & 1), 63);
                }
                m[2] = FastAudio.Tables[5][index5];
                FastAudio.Synthesize(_ch[c], m, buf);
            }
        return outp;
    }

    /// <summary>Reads fields MSB first from 32-bit words. Fields never cross a word boundary in valid streams.</summary>
    private ref struct WordReader
    {
        private readonly Span<uint> _w;
        private int _pos;
        public WordReader(Span<uint> w) { _w = w; _pos = 0; }

        public int Read(int bits)
        {
            if (bits == 0) return 0;
            _pos += bits;
            uint v = _w[(_pos - 1) / 32] >> ((-_pos) & 31);
            return (int)(v & ((1u << bits) - 1));
        }
    }
}

/// <summary>
/// FastAudio encoder: LPC analysis (autocorrelation + Levinson-Durbin, quantised reflection coefficients) and an
/// analysis-by-synthesis search of the pulse grid phase, gain and pulse levels for every 64-sample group, using
/// the decoder's own synthesis filter so encoder and decoder states never drift apart.
/// </summary>
public sealed class FastAudioEncoder
{
    private readonly FastAudio.ChannelState[] _ch;

    public FastAudioEncoder(int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        _ch = Enumerable.Range(0, channels).Select(_ => new FastAudio.ChannelState()).ToArray();
    }

    /// <summary>Encodes <paramref name="samples"/> (a multiple of 256) per channel starting at <paramref name="offset"/>.</summary>
    public byte[] EncodePacket(short[][] pcm, int offset, int samples)
    {
        int channels = _ch.Length;
        if (pcm.Length != channels) throw new ArgumentException("Channel count mismatch.");
        if (samples <= 0 || samples % FastAudio.SubFrameSamples != 0) throw new ArgumentException("Sample count must be a positive multiple of 256.");
        int subframes = samples / FastAudio.SubFrameSamples;
        var outp = new byte[subframes * channels * FastAudio.SubFrameBytes];
        int pos = 0;
        var words = new uint[10];
        for (int sf = 0; sf < subframes; sf++)
            for (int c = 0; c < channels; c++)
            {
                EncodeSubFrame(c, pcm[c], offset + sf * FastAudio.SubFrameSamples, words);
                foreach (uint w in words)
                {
                    outp[pos++] = (byte)w; outp[pos++] = (byte)(w >> 8); outp[pos++] = (byte)(w >> 16); outp[pos++] = (byte)(w >> 24);
                }
            }
        return outp;
    }

    private const int WindowLength = 384; // 64 samples of history, the sub-frame, 64 samples of look-ahead

    private void EncodeSubFrame(int c, short[] src, int start, uint[] words)
    {
        float S(int i) => i >= 0 && i < src.Length ? src[i] / 32768f : 0f;

        // --- LPC analysis on the pre-emphasised signal (the decoder de-emphasises with 0.86) ---
        var win = new double[WindowLength];
        int w0 = start - 64;
        for (int i = 0; i < WindowLength; i++)
        {
            double pre = S(w0 + i) - FastAudio.Deemphasis * S(w0 + i - 1);
            win[i] = pre * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / WindowLength));
        }
        var r = new double[FastAudio.Order + 1];
        for (int k = 0; k <= FastAudio.Order; k++)
        {
            double s = 0;
            for (int i = k; i < WindowLength; i++) s += win[i] * win[i - k];
            r[k] = s;
        }
        r[0] = r[0] * 1.003 + 1e-9; // noise floor: keeps quantised filters of pure tones from ringing
        for (int k = 1; k <= FastAudio.Order; k++) r[k] *= Math.Exp(-0.5 * Math.Pow(2 * Math.PI * 0.004 * k, 2)); // lag window
        var refl = Levinson(r);

        // reflection coefficient k_i (i = 1..8) is m[8 - i] = Tables[i - 1]
        var idx = new int[8];
        Span<float> m = stackalloc float[8];
        for (int i = 0; i < 8; i++)
        {
            idx[i] = Nearest(FastAudio.Tables[i], refl[i]);
            m[7 - i] = FastAudio.Tables[i][idx[i]];
        }
        int index5 = idx[5];

        // impulse response of the synthesis filter (zero state)
        Span<float> h = stackalloc float[FastAudio.GroupSamples];
        h.Clear(); h[0] = 1;
        FastAudio.Synthesize(new FastAudio.ChannelState(), m, h);

        // --- excitation search per 64-sample group ---
        var gains = new int[4];
        var pads = new int[4];
        var levels = new int[4][];
        var st = _ch[c];
        Span<float> zir = stackalloc float[FastAudio.GroupSamples];
        Span<float> exc = stackalloc float[FastAudio.GroupSamples];
        var target = new double[FastAudio.GroupSamples];
        for (int g = 0; g < 4; g++)
        {
            zir.Clear();
            FastAudio.Synthesize(st.Clone(), m, zir);
            for (int i = 0; i < FastAudio.GroupSamples; i++) target[i] = S(start + g * 64 + i) - zir[i];
            SearchGroup(h, target, out pads[g], out gains[g], out levels[g]);

            exc.Clear();
            float gain = FastAudio.Gain(gains[g]);
            for (int j = 0; j < FastAudio.Pulses; j++) exc[pads[g] + 3 * j] = gain * (2 * levels[g][j] - 7);
            FastAudio.Synthesize(st, m, exc);
        }

        // --- bit packing (MSB first inside each 32-bit word) ---
        Array.Clear(words);
        int pos = 0;
        void Put(int bits, int v)
        {
            if (bits == 0) return;
            words[pos / 32] |= (uint)(v & ((1 << bits) - 1)) << (32 - pos % 32 - bits);
            pos += bits;
        }
        for (int i = 0; i < 8; i++) Put(FastAudio.IndexBits[i], idx[i]);
        for (int g = 3; g >= 0; g--) Put(6, gains[g]);
        for (int g = 3; g >= 0; g--) Put(2, pads[g]);
        for (int g = 0; g < 4; g++)
        {
            var q = levels[g];
            for (int j = 0; j < 20; j++)
            {
                Put(3, q[j]);
                if (j == 9) Put(2, q[20] >> 1);
                if (j == 19) Put(2, (q[20] & 1) << 1 | (index5 >> (3 - g) & 1));
            }
        }
    }

    /// <summary>Levinson-Durbin; returns reflection coefficients k_1..k_8 for A(z) = 1 + sum a_i z^-i.</summary>
    public static double[] Levinson(double[] r)
    {
        int p = r.Length - 1;
        var a = new double[p + 1];
        var tmp = new double[p + 1];
        var k = new double[p];
        double err = r[0];
        for (int i = 1; i <= p; i++)
        {
            double acc = r[i];
            for (int j = 1; j < i; j++) acc += a[j] * r[i - j];
            double ki = err > 0 ? -acc / err : 0;
            ki = Math.Clamp(ki, -0.999, 0.999);
            k[i - 1] = ki;
            Array.Copy(a, tmp, p + 1);
            for (int j = 1; j < i; j++) a[j] = tmp[j] + ki * tmp[i - j];
            a[i] = ki;
            err *= 1 - ki * ki;
        }
        return k;
    }

    private static int Nearest(float[] table, double v)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            double d = Math.Abs(table[i] - v);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private static readonly float[] GainTable = Enumerable.Range(0, 64).Select(FastAudio.Gain).ToArray();

    /// <summary>
    /// Finds grid phase, gain index and 21 pulse levels (0..7, value = gain * (2 level - 7)) that minimise the squared
    /// error between <paramref name="t"/> and the filtered excitation.
    /// </summary>
    private static void SearchGroup(ReadOnlySpan<float> h, double[] t, out int bestPad, out int bestGain, out int[] bestLevels)
    {
        const int P = FastAudio.Pulses, L = FastAudio.GroupSamples;
        bestPad = 0; bestGain = 0; bestLevels = Enumerable.Repeat(4, P).ToArray();
        double bestErr = double.MaxValue;
        var G = new double[P, P];
        var b = new double[P];
        var amp = new double[P];
        var q = new int[P];
        var gq = new double[P];
        for (int pad = 0; pad < 4; pad++)
        {
            // G = C^T C, b = C^T t, column j = h delayed to position pad + 3j
            for (int j = 0; j < P; j++)
            {
                int pj = pad + 3 * j;
                double s = 0;
                for (int n = pj; n < L; n++) s += h[n - pj] * t[n];
                b[j] = s;
                for (int l = j; l < P; l++)
                {
                    int pl = pad + 3 * l;
                    double g = 0;
                    for (int n = pl; n < L; n++) g += h[n - pj] * h[n - pl];
                    G[j, l] = G[l, j] = g;
                }
            }
            // unconstrained least squares (small ridge for the short tail columns)
            SolveSpd(G, b, amp, 1e-9);
            double peak = 0;
            for (int j = 0; j < P; j++) peak = Math.Max(peak, Math.Abs(amp[j]));
            int g0 = 0;
            while (g0 < 63 && GainTable[g0] * 7 < peak) g0++;
            for (int gi = Math.Max(0, g0 - 4); gi <= Math.Min(63, g0 + 3); gi++)
            {
                double gain = GainTable[gi];
                for (int j = 0; j < P; j++) q[j] = Math.Clamp((int)Math.Round((amp[j] / gain + 7) / 2), 0, 7);
                // coordinate descent on the quantised levels: e(q) = -2 g u.b + g^2 u^T G u with u = 2q - 7
                for (int j = 0; j < P; j++)
                {
                    double s = 0;
                    for (int l = 0; l < P; l++) s += G[j, l] * (2 * q[l] - 7);
                    gq[j] = s;
                }
                for (int pass = 0; pass < 3; pass++)
                    for (int j = 0; j < P; j++)
                    {
                        int u0 = 2 * q[j] - 7, bestU = u0;
                        double bestD = 0;
                        for (int u = -7; u <= 7; u += 2)
                        {
                            int d = u - u0;
                            if (d == 0) continue;
                            double delta = -2 * gain * d * b[j] + gain * gain * (2 * d * gq[j] + d * d * G[j, j]);
                            if (delta < bestD) { bestD = delta; bestU = u; }
                        }
                        if (bestU != u0)
                        {
                            int d = bestU - u0;
                            for (int l = 0; l < P; l++) gq[l] += G[l, j] * d;
                            q[j] = (bestU + 7) / 2;
                        }
                    }
                double e = 0;
                for (int j = 0; j < P; j++) e += -2 * gain * (2 * q[j] - 7) * b[j] + gain * gain * (2 * q[j] - 7) * gq[j];
                if (e < bestErr)
                {
                    bestErr = e; bestPad = pad; bestGain = gi;
                    bestLevels = (int[])q.Clone();
                }
            }
        }
    }

    /// <summary>Solves (A + ridge * trace/n * I) x = b by Cholesky decomposition.</summary>
    private static void SolveSpd(double[,] A, double[] b, double[] x, double ridge)
    {
        int n = b.Length;
        double tr = 0;
        for (int i = 0; i < n; i++) tr += A[i, i];
        double eps = ridge * tr / n + 1e-30;
        var Lm = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = A[i, j] + (i == j ? eps : 0);
                for (int k = 0; k < j; k++) s -= Lm[i, k] * Lm[j, k];
                Lm[i, j] = i == j ? Math.Sqrt(Math.Max(s, 1e-300)) : s / Lm[j, j];
            }
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= Lm[i, k] * y[k];
            y[i] = s / Lm[i, i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++) s -= Lm[k, i] * x[k];
            x[i] = s / Lm[i, i];
        }
    }
}
