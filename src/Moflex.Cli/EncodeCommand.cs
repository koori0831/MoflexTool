using Moflex.Core;
using Moflex.Core.Pipeline;

namespace Moflex.Cli;

/// <summary>Parses <c>moflex encode</c> arguments into an <see cref="EncodeJob"/>.</summary>
internal static class EncodeCommand
{
    public static int Run(string[] a)
    {
        var job = new EncodeJob { Input = a[1] };
        for (int i = 2; i < a.Length; i++)
            switch (a[i])
            {
                case "-o": job.Output = a[++i]; break;
                case "--like": job.Like = a[++i]; break;
                case "--size": { var t = a[++i].Split('x'); job.Width = int.Parse(t[0]); job.Height = int.Parse(t[1]); break; }
                case "--fps":
                {
                    var t = a[++i].Split('/');
                    job.FpsNum = long.Parse(t[0]); job.FpsDen = t.Length > 1 ? long.Parse(t[1]) : 1; job.FpsGiven = true;
                    break;
                }
                case "-q": job.Quantizer = int.Parse(a[++i]); break;
                case "--pq": job.PredictedQuantizer = int.Parse(a[++i]); break;
                case "--gop": job.MaxKeyInterval = int.Parse(a[++i]); break;
                case "--key-only": job.MaxKeyInterval = 1; break;
                case "--rate": job.SampleRate = int.Parse(a[++i]); break;
                case "--stereo": job.Stereo = Enum.Parse<StereoMode>(a[++i], true); break;
                case "--sbs": job.SideBySide = true; break;
                case "--audio": job.AudioWav = a[++i]; break;
                case "--no-audio": job.NoAudio = true; break;
                case "--pcm": job.Pcm16 = true; break;
                case "--fastaudio": job.FastAudio = true; break;
                case "--ffmpeg": job.Ffmpeg = a[++i]; break;
                case "--max-frames": job.MaxFrames = int.Parse(a[++i]); break;
                case "--threads": job.Threads = int.Parse(a[++i]); break;
                default: throw new ArgumentException($"Unknown encode option '{a[i]}'.");
            }
        int lastPct = -1;
        job.Log = Console.WriteLine;
        job.Progress = (n, total) =>
        {
            int pct = total > 0 ? (int)(n * 100 / total) : 0;
            if (pct != lastPct && pct % 5 == 0) { lastPct = pct; Console.Error.Write($"\r{pct,3}% ({n}/{total} frames)"); }
            if (n == total) Console.Error.WriteLine();
        };
        job.Run();
        return 0;
    }
}
