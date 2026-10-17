using Moflex.Core;
using Moflex.Core.Video;

namespace Moflex.Cli;

/// <summary>moflex encode &lt;input&gt; -o out.moflex --size WxH [--fps N/D] [-q N] [--audio file.wav|--no-audio] [--pcm] [--ffmpeg exe]</summary>
internal static class EncodeCommand
{
    public static int Run(string[] a)
    {
        string input = a[1], output = "out.moflex", ffmpeg = "ffmpeg";
        string? wav = null;
        int w = 0, h = 0, q = 30, fn = 1001, fd = 30000;
        bool noAudio = false, pcm16 = false;
        int pq = 32, gop = 240;
        for (int i = 2; i < a.Length; i++)
            switch (a[i])
            {
                case "-o": output = a[++i]; break;
                case "--size": { var t = a[++i].Split('x'); w = int.Parse(t[0]); h = int.Parse(t[1]); break; }
                case "--fps": { var t = a[++i].Split('/'); fd = int.Parse(t[0]); fn = t.Length > 1 ? int.Parse(t[1]) : 1; break; }
                case "-q": q = int.Parse(a[++i]); break;
                case "--audio": wav = a[++i]; break;
                case "--no-audio": noAudio = true; break;
                case "--pcm": pcm16 = true; break;
                case "--pq": pq = int.Parse(a[++i]); break;
                case "--gop": gop = int.Parse(a[++i]); break;
                case "--ffmpeg": ffmpeg = a[++i]; break;
            }
        if (w == 0 || h == 0) throw new ArgumentException("--size WxH is required.");
        string tmp = Path.Combine(Path.GetTempPath(), "moflex_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string yuv = input;
            int rate = 32000, ch = 2;
            if (!input.EndsWith(".yuv", StringComparison.OrdinalIgnoreCase))
            {
                yuv = Path.Combine(tmp, "v.yuv");
                Exec(ffmpeg, $"-v error -y -i \"{input}\" -an -vf scale={w}:{h},fps={fd}/{fn} -pix_fmt yuv420p -f rawvideo \"{yuv}\"");
                if (!noAudio && wav == null)
                {
                    var tmpWav = Path.Combine(tmp, "a.wav");
                    try { Exec(ffmpeg, $"-v error -y -i \"{input}\" -vn -ac 2 -ar {rate} -c:a pcm_s16le \"{tmpWav}\""); } catch { }
                    if (File.Exists(tmpWav) && new FileInfo(tmpWav).Length > 100) wav = tmpWav;
                }
            }
            short[][]? pcm = null;
            if (!noAudio && wav != null) pcm = ReadWav(wav, out rate, out ch);
            int fs = w * h * 3 / 2;
            var raw = File.ReadAllBytes(yuv);
            IEnumerable<YuvFrame> Frames()
            {
                for (int i = 0; i + fs <= raw.Length; i += fs)
                    yield return new YuvFrame
                    {
                        Width = w, Height = h, Y = raw[i..(i + w * h)], U = raw[(i + w * h)..(i + w * h * 5 / 4)], V = raw[(i + w * h * 5 / 4)..(i + fs)],
                    };
            }
            var opt = new MovieEncodeOptions
            {
                Width = w, Height = h, FrameDurNum = fn, FrameDurDen = fd, Quantizer = q, PredictedQuantizer = pq, MaxKeyInterval = gop,
                Audio = pcm == null ? AudioFormat.None : pcm16 ? AudioFormat.Pcm16 : AudioFormat.ImaAdpcm,
                SampleRate = rate, Channels = ch,
            };
            File.WriteAllBytes(output, MovieEncoder.Encode(Frames(), pcm, opt));
            Console.WriteLine($"wrote {output} ({new FileInfo(output).Length} bytes)");
            return 0;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static void Exec(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed ({p.ExitCode}).");
    }

    private static short[][] ReadWav(string path, out int rate, out int channels)
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
        if (dataOff < 0) throw new InvalidDataException("WAV has no data chunk.");
        int n = dataLen / (2 * channels);
        var pcm = new short[channels][];
        for (int c = 0; c < channels; c++) pcm[c] = new short[n];
        for (int i = 0; i < n; i++) for (int c = 0; c < channels; c++) pcm[c][i] = BitConverter.ToInt16(d, dataOff + (i * channels + c) * 2);
        return pcm;
    }
}
