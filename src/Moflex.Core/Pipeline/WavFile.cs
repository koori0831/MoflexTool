namespace Moflex.Core.Pipeline;

/// <summary>Minimal 16-bit PCM WAV reader/writer.</summary>
public static class WavFile
{
    public static short[][] Read(string path, out int rate, out int channels)
    {
        var d = File.ReadAllBytes(path);
        int pos = 12; rate = 0; channels = 0; int dataOff = -1, dataLen = 0;
        while (pos + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int len = BitConverter.ToInt32(d, pos + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(d, pos + 10); rate = BitConverter.ToInt32(d, pos + 12); }
            if (id == "data") { dataOff = pos + 8; dataLen = Math.Min(len, d.Length - dataOff); break; }
            pos += 8 + len + (len & 1);
        }
        if (dataOff < 0 || channels <= 0) throw new InvalidDataException("Not a 16-bit PCM WAV file.");
        int n = dataLen / (2 * channels);
        var pcm = new short[channels][];
        for (int c = 0; c < channels; c++) pcm[c] = new short[n];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++) pcm[c][i] = BitConverter.ToInt16(d, dataOff + (i * channels + c) * 2);
        return pcm;
    }

    public static void Write(Stream o, ReadOnlySpan<byte> interleaved, int rate, int channels)
    {
        using var bw = new BinaryWriter(o, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8); bw.Write(36 + interleaved.Length); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1);
        bw.Write((short)channels); bw.Write(rate); bw.Write(rate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(interleaved.Length); bw.Write(interleaved);
    }
}
