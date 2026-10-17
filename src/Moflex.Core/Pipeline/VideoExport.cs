using System.Diagnostics;
using System.Text;
using Moflex.Core.Container;

namespace Moflex.Core.Pipeline;

/// <summary>
/// Exports a MOFLEX file as one ordinary, editable video file (.avi or .mov) with the picture and sound stored
/// losslessly. The built-in decoders produce the frames and samples; FFmpeg only packs them into the container.
/// </summary>
public sealed class VideoExport
{
    /// <summary>Lossless video codecs that FFmpeg can write and read back bit for bit (yuv420p).</summary>
    public static readonly IReadOnlyList<string> Codecs = new[] { "utvideo", "ffv1", "magicyuv", "rawvideo" };

    public MoflexFile File { get; set; } = null!;
    public string Output { get; set; } = "out.avi";
    public string Ffmpeg { get; set; } = "ffmpeg";
    /// <summary>
    /// 3D movies only: both eyes side by side or one eye. Null (default) decides from the picture: side by side for
    /// real 3D, the left eye alone when both eyes show the same picture (see <see cref="MoflexVideoReader.AnalyzeStereo"/>).
    /// </summary>
    public EyeLayout? Eyes { get; set; }
    public string Codec { get; set; } = "utvideo";
    public bool NoAudio { get; set; }
    public int MaxFrames { get; set; } = int.MaxValue;
    /// <summary>Called with (pictures written, total pictures).</summary>
    public Action<long, long>? Progress { get; set; }
    public Action<string>? Log { get; set; }

    /// <summary>Writes the file and returns the number of pictures.</summary>
    public int Run()
    {
        string ext = Path.GetExtension(Output).ToLowerInvariant();
        if (ext is not (".avi" or ".mov" or ".mkv"))
            throw new ArgumentException($"Unsupported output type '{ext}': use .avi or .mov (or .yuv for raw frames).");
        if (!Codecs.Contains(Codec)) throw new ArgumentException($"Unknown codec '{Codec}' (use {string.Join(", ", Codecs)}).");
        if (Codec == "rawvideo" && ext == ".mov") throw new ArgumentException("rawvideo yuv420p cannot be stored in .mov; use .avi or another codec.");

        var eyes = Eyes ?? EyeLayout.SideBySide;
        if (Eyes == null && MoflexVideoReader.AnalyzeStereo(File) is { } st)
        {
            Log?.Invoke("3D: " + st + ".");
            if (!st.IsReal3D) { eyes = EyeLayout.Left; Log?.Invoke("Exporting one eye as ordinary 2D video."); }
        }
        var reader = new MoflexVideoReader(File, eyes);
        int total = Math.Min(reader.FrameCount, MaxFrames);
        bool audio = !NoAudio && File.Streams.Any(s => s.Kind == StreamKind.Audio);
        string? wav = null;
        try
        {
            var args = new StringBuilder($"-hide_banner -v error -y -f rawvideo -pix_fmt yuv420p -s {reader.Width}x{reader.Height} -r {reader.FpsNum}/{reader.FpsDen} -i -");
            if (audio)
            {
                wav = Path.Combine(Path.GetTempPath(), "moflex_" + Guid.NewGuid().ToString("N") + ".wav");
                Log?.Invoke("Decoding the audio...");
                using (var o = System.IO.File.Create(wav)) MoflexExport.WriteWav(File, o);
                args.Append($" -i \"{wav}\" -map 0:v -map 1:a -c:a pcm_s16le");
            }
            args.Append($" -c:v {Codec} -pix_fmt yuv420p");
            if (Codec == "ffv1") args.Append(" -level 3 -g 1");
            if (reader.IsStereo && eyes == EyeLayout.SideBySide) args.Append(" -metadata:s:v:0 stereo_mode=left_right");
            args.Append($" \"{Output}\"");

            Log?.Invoke($"Writing {total} pictures ({reader.Width}x{reader.Height}, {reader.FpsNum}/{reader.FpsDen} fps, {Codec}"
                        + (reader.IsStereo ? $", 3D {(eyes == EyeLayout.SideBySide ? "side-by-side" : eyes + " eye")}" : "") + ")...");
            var psi = new ProcessStartInfo(Ffmpeg, args.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
            };
            using var p = StartFfmpeg(psi);
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            int n = 0;
            try
            {
                var stdin = p.StandardInput.BaseStream;
                for (; n < total; n++)
                {
                    var f = reader.Read();
                    if (f == null) break;
                    stdin.Write(f.Y); stdin.Write(f.U); stdin.Write(f.V);
                    Progress?.Invoke(n + 1, total);
                }
                stdin.Close();
            }
            catch (IOException) { } // FFmpeg stopped early: its message is reported below
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg failed (exit code {p.ExitCode}): {err.ToString().Trim()}");
            Log?.Invoke($"Wrote {Output}.");
            return n;
        }
        finally
        {
            if (wav != null) try { System.IO.File.Delete(wav); } catch { }
        }
    }

    internal static Process StartFfmpeg(ProcessStartInfo psi)
    {
        try { return Process.Start(psi) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start FFmpeg ('{psi.FileName}'). Put ffmpeg on PATH or give its path.", ex);
        }
    }
}
