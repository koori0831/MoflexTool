using Moflex.Core.Audio;
using Moflex.Core.Container;
using Moflex.Core.Video;

namespace Moflex.Core.Pipeline;

/// <summary>Decodes the streams of a MOFLEX file to plain formats.</summary>
public static class MoflexExport
{
    /// <summary>Decodes the audio stream (IMA-ADPCM or PCM) to a 16-bit WAV file.</summary>
    public static void WriteWav(MoflexFile file, Stream output)
    {
        var a = file.Streams.FirstOrDefault(x => x.Kind == StreamKind.Audio) ?? throw new InvalidDataException("The file has no audio stream.");
        var pcm = new MemoryStream();
        foreach (var p in file.Packets.Where(p => p.StreamIndex == a.Index))
        {
            if (a.CodecId == 1)
            {
                var ch = ImaMoflex.DecodePacket(p.Data, a.Channels);
                for (int i = 0; i < ch[0].Length; i++)
                    for (int c = 0; c < a.Channels; c++) { pcm.WriteByte((byte)ch[c][i]); pcm.WriteByte((byte)(ch[c][i] >> 8)); }
            }
            else if (a.CodecId == 2) pcm.Write(p.Data);
            else throw new NotSupportedException($"Audio codec {a.CodecName} is not supported yet.");
        }
        WavFile.Write(output, pcm.GetBuffer().AsSpan(0, (int)pcm.Length), a.SampleRate, a.Channels);
    }

    /// <summary>Decodes the video stream to raw planar YUV 4:2:0 (all stored frames, both eyes for 3D files).</summary>
    public static int WriteYuv(MoflexFile file, Stream output, int maxFrames = int.MaxValue, Action<int, int>? progress = null)
    {
        var v = file.Streams.First(x => x.Kind == StreamKind.Video);
        var dec = new MobiclipDecoder(v.Width, v.Height);
        var packets = file.Packets.Where(p => p.StreamIndex == v.Index).ToList();
        int total = Math.Min(packets.Count, maxFrames), n = 0;
        foreach (var p in packets.Take(total))
        {
            var fr = dec.Decode(p.Data);
            output.Write(fr.Y); output.Write(fr.U); output.Write(fr.V);
            progress?.Invoke(++n, total);
        }
        return n;
    }

    /// <summary>Human-readable summary of a file.</summary>
    public static string Describe(MoflexFile f)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Block size : {f.Header.BlockSize}");
        sb.AppendLine($"Blocks     : {f.Blocks.Count}  (sync points: {f.Blocks.Count(b => b.HeaderLength > 0)})");
        foreach (var s in f.Streams)
        {
            var ps = f.Packets.Where(p => p.StreamIndex == s.Index).ToList();
            string detail = s.Kind switch
            {
                StreamKind.Video => $"{s.Width}x{s.Height}, frame {s.TimeBaseNum}/{s.TimeBaseDen} s ({(double)s.TimeBaseDen / Math.Max(1, s.TimeBaseNum):0.###} fps stored)"
                                    + (s.DescriptorType == 3 && s.Reserved.Length > 2 ? $", layout {s.Reserved[2] & 15}" + (s.Reserved[2] % 16 == 0 ? " (3D interleaved, left first)" : "") : ""),
                StreamKind.Audio => $"{s.SampleRate} Hz, {s.Channels} ch",
                _ => "seek table",
            };
            sb.AppendLine($"Stream #{s.Index}: {s.Kind,-5} {s.CodecName,-17} {detail}; packets={ps.Count}, bytes={ps.Sum(p => (long)p.Data.Length):N0}");
        }
        return sb.ToString();
    }
}
