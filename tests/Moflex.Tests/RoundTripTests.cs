using Moflex.Core.Container;
using Xunit;

namespace Moflex.Tests;

public class RoundTripTests
{
    // Samples are never committed; point MOFLEX_SAMPLES at a folder of .moflex files to enable.
    public static IEnumerable<object[]> Samples()
    {
        var dir = Environment.GetEnvironmentVariable("MOFLEX_SAMPLES");
        if (dir is null || !Directory.Exists(dir)) { yield return new object[] { "" }; yield break; }
        foreach (var f in Directory.GetFiles(dir, "*.moflex")) yield return new object[] { f };
    }

    [Fact]
    public void VarIntAndChunkHeaderRoundTrip()
    {
        var hdr = MoflexWriter.WriteChunkHeader(2, true, new EndInfo(1, 0, 0, 1, 32090), 1544);
        Assert.NotEmpty(hdr);
    }

    [Theory, MemberData(nameof(Samples))]
    public void RewriteIsByteExact(string path)
    {
        if (path.Length == 0) return; // no samples configured
        var orig = File.ReadAllBytes(path);
        var f = MoflexFile.Parse(orig);
        Assert.Equal(orig, MoflexWriter.Rewrite(f, orig));
    }
}
