using Moflex.Core;
using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Cli;

/// <summary>
/// moflex encode &lt;input&gt; -o out.moflex (--like original.moflex | --size WxH [--fps A/B])
///   [-q N] [--pq N] [--gop N] [--stereo none|interleaved|duplicate] [--sbs] [--rate HZ] [--audio file.wav|--no-audio] [--pcm] [--ffmpeg exe]
/// </summary>
internal static class EncodeCommand
{
    public static int Run(string[] a)
    {
        string input = a[1], output = "out.moflex", ffmpeg = "ffmpeg";
        string? wav = null, like = null;
        int w = 0, h = 0, q = 30, pq = 32, gop = 240, rate = 47864, ch = 2;
        long fpsA = 30000, fpsB = 1001; // input frames per second = A / B
        int maxFrames = int.MaxValue;
        bool fpsGiven = false, noAudio = false, pcm16 = false, sbs = false;
        StereoMode? stereoOpt = null;
        for (int i = 2; i < a.Length; i++)
            switch (a[i])
            {
                case "-o": output = a[++i]; break;
                case "--like": like = a[++i]; break;
                case "--size": { var t = a[++i].Split('x'); w = int.Parse(t[0]); h = int.Parse(t[1]); break; }
                case "--fps": { var t = a[++i].Split('/'); fpsA = long.Parse(t[0]); fpsB = t.Length > 1 ? long.Parse(t[1]) : 1; fpsGiven = true; break; }
                case "-q": q = int.Parse(a[++i]); break;
                case "--pq": pq = int.Parse(a[++i]); break;
                case "--gop": gop = int.Parse(a[++i]); break;
                case "--rate": rate = int.Parse(a[++i]); break;
                case "--stereo": stereoOpt = Enum.Parse<StereoMode>(a[++i], true); break;
                case "--sbs": sbs = true; break;
                case "--audio": wav = a[++i]; break;
                case "--no-audio": noAudio = true; break;
                case "--pcm": pcm16 = true; break;
                case "--ffmpeg": ffmpeg = a[++i]; break;
                case "--max-frames": maxFrames = int.Parse(a[++i]); break;
                case "--key-only": gop = 1; break;
            }

        StereoMode stereo = stereoOpt ?? StereoMode.None;
        long storedNum = 0, storedDen = 0;
        if (like != null)
        {
            var model = MoflexFile.Load(like);
            var v = model.Streams.First(s => s.Kind == StreamKind.Video);
            w = v.Width; h = v.Height; storedNum = v.TimeBaseNum; storedDen = v.TimeBaseDen;
            if (stereoOpt == null) stereo = v.DescriptorType == 3 ? StereoMode.Duplicate : StereoMode.None;
            var au = model.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio);
            if (au != null) { rate = au.SampleRate; ch = au.Channels; }
        }
        if (sbs) stereo = StereoMode.Interleaved;
        if (w == 0 || h == 0) throw new ArgumentException("--like or --size WxH is required.");
        int group = stereo == StereoMode.None ? 1 : 2;
        if (storedNum == 0)
        {
            // stored frame duration from the input frame rate
            long num = fpsB, den = fpsA * (stereo == StereoMode.Duplicate || sbs ? 2 : 1);
            long g = Gcd(num, den); num /= g; den /= g;
            while (num > 65535 || den > 65535) { num = (num + 1) / 2; den = (den + 1) / 2; }
            storedNum = num; storedDen = den;
        }
        if (like != null && !fpsGiven)
        {
            // input rate that yields exactly one input frame per stamp group
            fpsA = storedDen; fpsB = storedNum * (stereo == StereoMode.None || (stereo == StereoMode.Interleaved && !sbs) ? 1 : 2);
        }

        string tmp = Path.Combine(Path.GetTempPath(), "moflex_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string yuv = input;
            int rw = sbs ? w * 2 : w;
            if (!input.EndsWith(".yuv", StringComparison.OrdinalIgnoreCase))
            {
                yuv = Path.Combine(tmp, "v.yuv");
                string vf = $"scale={rw}:{h}:force_original_aspect_ratio=decrease,pad={rw}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fpsA}/{fpsB}";
                Exec(ffmpeg, $"-v error -y -i \"{input}\" -an -vf \"{vf}\" -pix_fmt yuv420p -f rawvideo \"{yuv}\"");
                if (!noAudio && wav == null)
                {
                    var tmpWav = Path.Combine(tmp, "a.wav");
                    try { Exec(ffmpeg, $"-v error -y -i \"{input}\" -vn -ac {ch} -ar {rate} -c:a pcm_s16le \"{tmpWav}\""); } catch { }
                    if (File.Exists(tmpWav) && new FileInfo(tmpWav).Length > 100) wav = tmpWav;
                }
            }
            short[][]? pcm = null;
            if (!noAudio && wav != null) pcm = ReadWav(wav, out _, out ch);

            int fs = rw * h * 3 / 2;
            var raw = File.ReadAllBytes(yuv);
            IEnumerable<YuvFrame> Frames()
            {
                for (int i = 0, k = 0; i + fs <= raw.Length && k < maxFrames; i += fs, k++)
                {
                    if (!sbs)
                    {
                        yield return new YuvFrame { Width = w, Height = h, Y = raw[i..(i + w * h)], U = raw[(i + w * h)..(i + w * h * 5 / 4)], V = raw[(i + w * h * 5 / 4)..(i + fs)] };
                        continue;
                    }
                    for (int eye = 0; eye < 2; eye++)
                    {
                        var f = new YuvFrame { Width = w, Height = h, Y = new byte[w * h], U = new byte[w * h / 4], V = new byte[w * h / 4] };
                        int yBase = i, uBase = i + rw * h, vBase = uBase + rw * h / 4;
                        for (int y = 0; y < h; y++) Array.Copy(raw, yBase + y * rw + eye * w, f.Y, y * w, w);
                        for (int y = 0; y < h / 2; y++)
                        {
                            Array.Copy(raw, uBase + y * (rw / 2) + eye * (w / 2), f.U, y * (w / 2), w / 2);
                            Array.Copy(raw, vBase + y * (rw / 2) + eye * (w / 2), f.V, y * (w / 2), w / 2);
                        }
                        yield return f;
                    }
                }
            }
            var opt = new MovieEncodeOptions
            {
                Width = w, Height = h, FrameDurNum = (int)storedNum, FrameDurDen = (int)storedDen,
                Quantizer = q, PredictedQuantizer = pq, MaxKeyInterval = gop, Stereo = stereo,
                Audio = pcm == null ? AudioFormat.None : pcm16 ? AudioFormat.Pcm16 : AudioFormat.ImaAdpcm,
                SampleRate = rate, Channels = ch,
            };
            _ = group;
            File.WriteAllBytes(output, MovieEncoder.Encode(Frames(), pcm, opt));
            Console.WriteLine($"wrote {output} ({new FileInfo(output).Length} bytes)");
            return 0;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static long Gcd(long x, long y) { while (y != 0) (x, y) = (y, x % y); return x == 0 ? 1 : x; }

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
