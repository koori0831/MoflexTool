using System.Diagnostics;
using Moflex.Core;
using Moflex.Core.Container;
using Moflex.Core.Pipeline;
using Moflex.Core.Video;
using Xunit;

namespace Moflex.Tests;

public class ExportTests
{
    private static YuvFrame Flat(int w, int h, byte y, byte u = 128, byte v = 128)
    {
        var f = new YuvFrame { Width = w, Height = h, Y = new byte[w * h], U = new byte[w * h / 4], V = new byte[w * h / 4] };
        Array.Fill(f.Y, y); Array.Fill(f.U, u); Array.Fill(f.V, v);
        for (int i = 0; i < f.Y.Length; i++) f.Y[i] = (byte)(y + (i % w) / 4); // some texture
        return f;
    }

    // 3D movie: 6 pictures, left eye dark, right eye bright, key frame every 4 stored frames
    private static MoflexFile Stereo(out List<YuvFrame> stored)
    {
        stored = new List<YuvFrame>();
        for (int i = 0; i < 6; i++) { stored.Add(Flat(64, 48, (byte)(40 + i * 4))); stored.Add(Flat(64, 48, (byte)(150 + i * 4))); }
        var bytes = MovieEncoder.Encode(stored, null, new MovieEncodeOptions
        {
            Width = 64, Height = 48, FrameDurNum = 1001, FrameDurDen = 60000, Stereo = StereoMode.Interleaved,
            Audio = AudioFormat.None, MaxKeyInterval = 4, Quantizer = 12, PredictedQuantizer = 12,
        });
        return MoflexFile.Parse(bytes);
    }

    private static List<YuvFrame> Sequential(MoflexFile f)
    {
        var dec = new MobiclipDecoder(64, 48);
        return f.Packets.Where(p => p.StreamIndex == 0).Select(p => dec.Decode(p.Data)).ToList();
    }

    [Fact]
    public void StereoPicturesAreSideBySideAtTheEyeRate()
    {
        var file = Stereo(out _);
        var r = new MoflexVideoReader(file);
        Assert.True(r.IsStereo);
        Assert.Equal((128, 48), (r.Width, r.Height));
        Assert.Equal(6, r.FrameCount);
        Assert.Equal((30000L, 1001L), (r.FpsNum, r.FpsDen));

        var dec = Sequential(file);
        for (int k = 0; k < 6; k++)
        {
            var p = r.Read()!;
            for (int y = 0; y < 48; y++)
            {
                Assert.Equal(dec[2 * k].Y.AsSpan(y * 64, 64).ToArray(), p.Y.AsSpan(y * 128, 64).ToArray());
                Assert.Equal(dec[2 * k + 1].Y.AsSpan(y * 64, 64).ToArray(), p.Y.AsSpan(y * 128 + 64, 64).ToArray());
            }
            for (int y = 0; y < 24; y++)
                Assert.Equal(dec[2 * k + 1].V.AsSpan(y * 32, 32).ToArray(), p.V.AsSpan(y * 64 + 32, 32).ToArray());
        }
        Assert.Null(r.Read());
    }

    [Fact]
    public void SeekGivesTheSamePictureAsPlayingThrough()
    {
        var file = Stereo(out _);
        var dec = Sequential(file);
        var r = new MoflexVideoReader(file, EyeLayout.Right);
        Assert.Equal(64, r.Width);
        foreach (int k in new[] { 5, 1, 3, 0, 4, 4, 2 })
        {
            r.Seek(k);
            Assert.Equal(k, r.Position);
            Assert.Equal(dec[2 * k + 1].Y, r.Read()!.Y);
        }
    }

    [Fact]
    public void BgraConversionUsesVideoRange()
    {
        var f = new YuvFrame { Width = 2, Height = 2, Y = new byte[] { 16, 235, 16, 235 }, U = new byte[] { 128 }, V = new byte[] { 128 } };
        var px = new byte[16];
        MoflexVideoReader.ToBgra(f, px);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, px[..8]);
    }

    [Fact]
    public void SideBySideIsRecognisedAtAnyScale()
    {
        Assert.True(EncodeJob.LooksSideBySide(800, 240, 400, 240));
        Assert.True(EncodeJob.LooksSideBySide(1600, 480, 400, 240));
        Assert.False(EncodeJob.LooksSideBySide(400, 240, 400, 240));
        Assert.False(EncodeJob.LooksSideBySide(1920, 1080, 400, 240));
    }

    // Needs FFmpeg: set MOFLEX_FFMPEG to its path to run.
    [Fact]
    public void ExportedFileIsLossless()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("MOFLEX_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg)) return;
        var file = Stereo(out _);
        var expected = new MemoryStream();
        var r = new MoflexVideoReader(file);
        while (r.Read() is { } p) { expected.Write(p.Y); expected.Write(p.U); expected.Write(p.V); }

        foreach (var (codec, ext) in new[] { ("utvideo", ".avi"), ("ffv1", ".mov") })
        {
            string tmp = Path.Combine(Path.GetTempPath(), "moflex_test_" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                Assert.Equal(6, new VideoExport { File = file, Output = tmp, Ffmpeg = ffmpeg, Codec = codec }.Run());
                var psi = new ProcessStartInfo(ffmpeg, $"-v error -i \"{tmp}\" -f rawvideo -pix_fmt yuv420p -") { RedirectStandardOutput = true, UseShellExecute = false };
                using var proc = Process.Start(psi)!;
                var back = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(back);
                proc.WaitForExit();
                Assert.Equal(expected.ToArray(), back.ToArray());
            }
            finally { File.Delete(tmp); }
        }
    }
}
