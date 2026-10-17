using System.Diagnostics;
using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Core.Pipeline;

/// <summary>
/// End-to-end encode of an ordinary video file (or raw YUV 4:2:0) into a MOFLEX file.
/// FFmpeg, if needed, is only used as an external program to decode/scale the input.
/// </summary>
public sealed class EncodeJob
{
    public string Input { get; set; } = "";
    public string Output { get; set; } = "out.moflex";
    /// <summary>Existing MOFLEX whose size, frame rate, stereo layout and audio format are copied.</summary>
    public string? Like { get; set; }
    public string Ffmpeg { get; set; } = "ffmpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Input frames per second as a fraction (A/B). Ignored when <see cref="Like"/> is set unless given explicitly.</summary>
    public long FpsNum { get; set; } = 30000;
    public long FpsDen { get; set; } = 1001;
    public bool FpsGiven { get; set; }
    public int Quantizer { get; set; } = 30;
    public int PredictedQuantizer { get; set; } = 32;
    public int MaxKeyInterval { get; set; } = 240;
    public StereoMode? Stereo { get; set; }
    /// <summary>The input is side-by-side 3D (left half = left eye).</summary>
    public bool SideBySide { get; set; }
    /// <summary>
    /// When the reference is 3D and the input is twice as wide as one eye (as written by <see cref="VideoExport"/>),
    /// treat the input as side-by-side even if <see cref="SideBySide"/> is not set.
    /// </summary>
    public bool DetectSideBySide { get; set; } = true;
    public int SampleRate { get; set; } = 47864;
    public int Channels { get; set; } = 2;
    public string? AudioWav { get; set; }
    public bool NoAudio { get; set; }
    public bool Pcm16 { get; set; }
    /// <summary>Store audio as FastAudio (codec 0). Also chosen automatically when the Like file uses FastAudio.</summary>
    public bool FastAudio { get; set; }
    public int MaxFrames { get; set; } = int.MaxValue;
    /// <summary>Segments encoded in parallel (0 = one per CPU core).</summary>
    public int Threads { get; set; }

    /// <summary>Called with (frames encoded, total frames).</summary>
    public Action<long, long>? Progress { get; set; }
    public Action<string>? Log { get; set; }

    public long Run()
    {
        int w = Width, h = Height, rate = SampleRate, ch = Channels;
        long fpsA = FpsNum, fpsB = FpsDen;
        StereoMode stereo = Stereo ?? StereoMode.None;
        long storedNum = 0, storedDen = 0;
        if (Like != null)
        {
            var model = MoflexFile.Load(Like);
            var v = model.Streams.First(s => s.Kind == StreamKind.Video);
            w = v.Width; h = v.Height; storedNum = v.TimeBaseNum; storedDen = v.TimeBaseDen;
            if (Stereo == null) stereo = v.DescriptorType == 3 ? StereoMode.Duplicate : StereoMode.None;
            var au = model.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio);
            if (au != null) { rate = au.SampleRate; ch = au.Channels; if (au.CodecId == 0 && !Pcm16) FastAudio = true; }
        }
        bool sbs = SideBySide;
        if (!sbs && DetectSideBySide && Stereo == null && stereo == StereoMode.Duplicate && w > 0 && h > 0
            && !Input.EndsWith(".yuv", StringComparison.OrdinalIgnoreCase)
            && ProbeSize(Ffmpeg, Input) is { } size && LooksSideBySide(size.Width, size.Height, w, h))
        {
            sbs = true;
            Log?.Invoke($"The input is {size.Width}x{size.Height}, two {w}x{h} eyes side by side: encoding it as 3D.");
        }
        if (sbs) stereo = StereoMode.Interleaved;
        if (w == 0 || h == 0) throw new ArgumentException("A reference file (Like) or the frame size is required.");
        if (storedNum == 0)
        {
            long num = fpsB, den = fpsA * (stereo == StereoMode.Duplicate || sbs ? 2 : 1);
            long g = Gcd(num, den); num /= g; den /= g;
            while (num > 65535 || den > 65535) { num = (num + 1) / 2; den = (den + 1) / 2; }
            storedNum = num; storedDen = den;
        }
        if (Like != null && !FpsGiven)
        {
            // one input frame per time-stamp group
            fpsA = storedDen;
            fpsB = storedNum * (stereo == StereoMode.None || (stereo == StereoMode.Interleaved && !sbs) ? 1 : 2);
        }

        string tmp = Path.Combine(Path.GetTempPath(), "moflex_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string yuv = Input;
            string? wav = AudioWav;
            int rw = sbs ? w * 2 : w;
            if (!Input.EndsWith(".yuv", StringComparison.OrdinalIgnoreCase))
            {
                yuv = Path.Combine(tmp, "v.yuv");
                Log?.Invoke("Decoding the input with FFmpeg...");
                string vf = $"scale={rw}:{h}:force_original_aspect_ratio=decrease,pad={rw}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fpsA}/{fpsB}";
                Exec(Ffmpeg, $"-v error -y -i \"{Input}\" -an -vf \"{vf}\" -pix_fmt yuv420p -f rawvideo \"{yuv}\"");
                if (!NoAudio && wav == null)
                {
                    var tmpWav = Path.Combine(tmp, "a.wav");
                    try { Exec(Ffmpeg, $"-v error -y -i \"{Input}\" -vn -ac {ch} -ar {rate} -c:a pcm_s16le \"{tmpWav}\""); } catch { }
                    if (File.Exists(tmpWav) && new FileInfo(tmpWav).Length > 100) wav = tmpWav;
                }
            }
            short[][]? pcm = null;
            if (!NoAudio && wav != null) pcm = WavFile.Read(wav, out _, out ch);

            int fs = rw * h * 3 / 2;
            var raw = File.ReadAllBytes(yuv);
            long inputFrames = Math.Min(raw.Length / fs, MaxFrames);
            long total = inputFrames * (stereo == StereoMode.None || (stereo == StereoMode.Interleaved && !sbs) ? 1 : 2);
            var opt = new MovieEncodeOptions
            {
                Width = w, Height = h, FrameDurNum = (int)storedNum, FrameDurDen = (int)storedDen,
                Quantizer = Quantizer, PredictedQuantizer = PredictedQuantizer, MaxKeyInterval = MaxKeyInterval, Stereo = stereo, Threads = Threads,
                Audio = pcm == null ? AudioFormat.None : Pcm16 ? AudioFormat.Pcm16 : FastAudio ? AudioFormat.FastAudio : AudioFormat.ImaAdpcm,
                SampleRate = rate, Channels = ch,
                Progress = n => Progress?.Invoke(n, total),
            };
            Log?.Invoke($"Encoding {total} frames ({w}x{h}, {stereo}{(sbs ? ", side-by-side input" : "")})...");
            var bytes = MovieEncoder.Encode(Frames(raw, fs, rw, w, h, sbs, inputFrames), pcm, opt);
            File.WriteAllBytes(Output, bytes);
            Log?.Invoke($"Wrote {Output} ({bytes.Length:N0} bytes).");
            return bytes.Length;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static IEnumerable<YuvFrame> Frames(byte[] raw, int fs, int rw, int w, int h, bool sbs, long count)
    {
        for (long k = 0; k < count; k++)
        {
            int i = (int)(k * fs);
            if (!sbs)
            {
                yield return new YuvFrame { Width = w, Height = h, Y = raw[i..(i + w * h)], U = raw[(i + w * h)..(i + w * h * 5 / 4)], V = raw[(i + w * h * 5 / 4)..(i + fs)] };
                continue;
            }
            for (int eye = 0; eye < 2; eye++)
            {
                var f = new YuvFrame { Width = w, Height = h, Y = new byte[w * h], U = new byte[w * h / 4], V = new byte[w * h / 4] };
                int uBase = i + rw * h, vBase = uBase + rw * h / 4;
                for (int y = 0; y < h; y++) Array.Copy(raw, i + y * rw + eye * w, f.Y, y * w, w);
                for (int y = 0; y < h / 2; y++)
                {
                    Array.Copy(raw, uBase + y * (rw / 2) + eye * (w / 2), f.U, y * (w / 2), w / 2);
                    Array.Copy(raw, vBase + y * (rw / 2) + eye * (w / 2), f.V, y * (w / 2), w / 2);
                }
                yield return f;
            }
        }
    }

    /// <summary>True when a picture of the given size holds two eyes of eyeW x eyeH side by side (any scale, 1 % tolerance).</summary>
    public static bool LooksSideBySide(int width, int height, int eyeW, int eyeH) =>
        width > 0 && height > 0 && Math.Abs((double)width * eyeH - 2.0 * eyeW * height) <= 0.01 * width * eyeH;

    /// <summary>Size of the first video stream of a file, read from FFmpeg's description of it (null if unknown).</summary>
    public static (int Width, int Height)? ProbeSize(string ffmpeg, string input)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -i \"{input}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string text = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var m = System.Text.RegularExpressions.Regex.Match(text, @"Stream #.*?Video:.*?[, ](\d{2,5})x(\d{2,5})");
            return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
        }
        catch { return null; }
    }

    private static long Gcd(long x, long y) { while (y != 0) (x, y) = (y, x % y); return x == 0 ? 1 : x; }

    private static void Exec(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed (exit code {p.ExitCode}).");
    }
}
