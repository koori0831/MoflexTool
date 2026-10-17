namespace Moflex.Core.Audio;

/// <summary>
/// IMA ADPCM as used by MOFLEX audio packets: per channel a 4-byte header (step index LE16, predictor LE16),
/// then 256-sample sub-frames (128 bytes per channel, low nibble first), channels in turn within a sub-frame.
/// The ADPCM algorithm itself is the public IMA/DVI standard.
/// </summary>
public static class ImaMoflex
{
    public const int SubFrameSamples = 256;

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };
    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };

    public struct State { public int StepIndex; public int Predictor; }

    private static int Decode(ref State s, int nibble)
    {
        int step = StepTable[s.StepIndex];
        int diff = ((2 * (nibble & 7) + 1) * step) >> 3;
        int p = (nibble & 8) != 0 ? s.Predictor - diff : s.Predictor + diff;
        s.Predictor = Math.Clamp(p, -32768, 32767);
        s.StepIndex = Math.Clamp(s.StepIndex + IndexTable[nibble & 7], 0, 88);
        return s.Predictor;
    }

    private static int Encode(ref State s, int sample)
    {
        int step = StepTable[s.StepIndex];
        int delta = sample - s.Predictor;
        int nibble = 0;
        if (delta < 0) { nibble = 8; delta = -delta; }
        int mag = Math.Min(7, delta * 4 / step);
        // choose the magnitude whose reconstruction is closest
        int best = mag, bestErr = int.MaxValue;
        for (int m = Math.Max(0, mag - 1); m <= Math.Min(7, mag + 1); m++)
        {
            int d = ((2 * m + 1) * step) >> 3;
            int err = Math.Abs(delta - d);
            if (err < bestErr) { bestErr = err; best = m; }
        }
        nibble |= best;
        Decode(ref s, nibble);
        return nibble;
    }

    /// <summary>Decodes one packet into per-channel PCM. Sample count is derived from the packet size.</summary>
    public static short[][] DecodePacket(ReadOnlySpan<byte> pkt, int channels)
    {
        int payload = pkt.Length - 4 * channels;
        int sub = payload / (channels * SubFrameSamples / 2);
        var st = new State[channels];
        var pcm = new short[channels][];
        int pos = 0;
        for (int c = 0; c < channels; c++)
        {
            st[c].StepIndex = (short)(pkt[pos] | (pkt[pos + 1] << 8));
            st[c].Predictor = (short)(pkt[pos + 2] | (pkt[pos + 3] << 8));
            pos += 4;
            if ((uint)st[c].StepIndex > 88) throw new InvalidDataException("Bad ADPCM step index.");
            pcm[c] = new short[sub * SubFrameSamples];
        }
        for (int sf = 0; sf < sub; sf++)
            for (int c = 0; c < channels; c++)
                for (int n = 0; n < SubFrameSamples; n += 2)
                {
                    int v = pkt[pos++];
                    pcm[c][sf * SubFrameSamples + n] = (short)Decode(ref st[c], v & 15);
                    pcm[c][sf * SubFrameSamples + n + 1] = (short)Decode(ref st[c], v >> 4);
                }
        return pcm;
    }

    /// <summary>Encodes whole sub-frames (sample count must be a multiple of 256). State carries over between packets.</summary>
    public static byte[] EncodePacket(short[][] pcm, int offset, int samples, State[] state, bool first)
    {
        int channels = pcm.Length;
        if (samples % SubFrameSamples != 0) throw new ArgumentException("Sample count must be a multiple of 256.");
        var ms = new MemoryStream();
        for (int c = 0; c < channels; c++)
        {
            if (first) { state[c].Predictor = pcm[c][offset]; }
            ms.WriteByte((byte)state[c].StepIndex); ms.WriteByte((byte)(state[c].StepIndex >> 8));
            ms.WriteByte((byte)state[c].Predictor); ms.WriteByte((byte)(state[c].Predictor >> 8));
        }
        for (int sf = 0; sf < samples / SubFrameSamples; sf++)
            for (int c = 0; c < channels; c++)
                for (int n = 0; n < SubFrameSamples; n += 2)
                {
                    int i = offset + sf * SubFrameSamples + n;
                    int lo = Encode(ref state[c], pcm[c][i]);
                    int hi = Encode(ref state[c], pcm[c][i + 1]);
                    ms.WriteByte((byte)(lo | (hi << 4)));
                }
        return ms.ToArray();
    }
}
