using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>
/// JPEG XL and JPEG 2000 go through system libraries. CI installs the stock Ubuntu packages
/// (versioned sonames only, no -dev symlinks) and sets KAWAPAINT_REQUIRE_NATIVE_CODECS, so there
/// a missing codec is a failure (issues #27, #28); elsewhere the round trips skip.
/// </summary>
public class NativeCodecTests
{
    public static TheoryData<string, bool> Cases => new()
    {
        { "jxl", true }, { "jxl", false }, { "jp2", true }, { "jp2", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip(string codecId, bool lossless)
    {
        IImageCodec codec = codecId == "jxl" ? new JxlCodec() : new Jp2Codec();
        if (Environment.GetEnvironmentVariable("KAWAPAINT_REQUIRE_NATIVE_CODECS") is { Length: > 0 })
            Assert.True(codec.IsAvailable, $"{codec.DisplayName} library not found");
        Assert.SkipUnless(codec.IsAvailable, $"{codec.DisplayName} library not installed");

        using var source = new Surface(64, 48);
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
                source[x, y] = ColorBgra.FromBgra((byte)(x * 4), (byte)(y * 5), 90, (byte)(x < 32 ? 255 : 128));

        using var stream = new MemoryStream();
        codec.Encode(source, stream, new EncodeOptions { Lossless = lossless, Quality = 90 });
        stream.Position = 0;
        using Surface decoded = codec.Decode(stream);

        Assert.Equal((64, 48), (decoded.Width, decoded.Height));
        int worst = 0;
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                ColorBgra a = source[x, y], b = decoded[x, y];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)),
                    Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        Assert.True(lossless ? worst == 0 : worst <= 24, $"largest channel error {worst}");
    }

    // A 64x48 JP2 written by libopenjp2 as sYCC with 4:2:0 chroma (components 1 and 2 are
    // 32x24), losslessly. Its RGB is (40 + 6*bx, 30 + 8*by, 200 - 3*bx) per 2x2 block (bx, by),
    // so chroma subsampling loses nothing and only the YCbCr rounding remains. Issue #30.
    private const string SubsampledSycc = "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAMAAAAEAAAwcHAAAAAAAPY29scgEA"
        + "AAAAABIAAAMnanAyY/9P/1EALwAAAAAAQAAAADAAAAAAAAAAAAAAAEAAAAAwAAAAAAAAAAAAAwcBAQcCAgcCAv9SAAwAAAAB"
        + "AAUEBAAB/1wAE0BASEhQSEhQSEhQSEhQSEhQ/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS4w/5AACgAA"
        + "AAACoAAB/5PPtBQHq1Ml/MfUBAK/wPhBBcD5AcA6FACEDPiLCL8Kw+oBh9QFAHQQBQXfAcPqAofUBABfCX/AfCJB84aAOiAb"
        + "7ET4DL8uuREPEb8298Hzg4B0KAEIDgmXA38DwfOEgHQgDPcGLwF/wDpcD5B0AdLANpulBOiW+C6h9Z9Aje62ROEi8ngqzKU/"
        + "vzG6v4b0B0f9bHJEwPkDQPkCwB0QHPbqj/O/Dj6YiwEFCJfPwPkCwfOIgBGAG+1z7r8UqguzEbNPvwLTgsA69AfDQwAuiBTP"
        + "XJMzAEMUhD3Qvx7KEZAnvpVs7yaPqA42az5RNYykKVxryuJ7zXkNc7IT/T5Ig9ml6n3YIOtnk1ThTQgkfxQ6GKZbSqDWiE+u"
        + "1WO5icY9/Wh89LPkwgwzrrGES79mpH/AfCXAfCfACoA18PWGCy/bTAoHYEHlrkwpDaAWLYsF1BZJfwK7+Mptr9ZLwPkHQPkH"
        + "wB00gtrx4hKRUVksfQLUy39Btjr1BA2/je8PzxQ803cX4O4mXuiRJY/OQb13wBdXAOmgEMz3ytTmMpC1hjdcMQPOAj4Vdqn5"
        + "43nhs4jFYTZjLTvqNt9XdUIMHZVfGRFQVK/+HUH1inL9i6/AOtwDq8ALZF77mHkvDuRQO+AExXPK45piCYrYZsncMOJtf4yO"
        + "qfsGDDtk9ccAX0ceAph0ZGbjV933EXhPaPCR3NNg0kKZWdkirq7G06C5sg/OGcB8NXAfDSQAtsBfrrUNQPxe8j57yjAcPil3"
        + "lVkrxEJbACzEcJiFMiNBjOIPNelnwrYxAv9/mmVQAd5gHZ4669/Kaw0uicNCDNT1wwLA7wfGXaBT0Ms97zCHCXGhmuGUwEWB"
        + "XbRzrzXcT9PMpupHy9NkBTkd/9k=";

    [Fact]
    public void Jp2SubsampledSyccDecodesToRgb()
    {
        var codec = new Jp2Codec();
        if (Environment.GetEnvironmentVariable("KAWAPAINT_REQUIRE_NATIVE_CODECS") is { Length: > 0 })
            Assert.True(codec.IsAvailable, $"{codec.DisplayName} library not found");
        Assert.SkipUnless(codec.IsAvailable, $"{codec.DisplayName} library not installed");

        using var stream = new MemoryStream(Convert.FromBase64String(SubsampledSycc));
        using Surface decoded = codec.Decode(stream);

        Assert.Equal((64, 48), (decoded.Width, decoded.Height));
        int worst = 0;
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
            {
                int bx = x / 2, by = y / 2;
                ColorBgra c = decoded[x, y];
                worst = Math.Max(worst, Math.Max(Math.Abs(c.R - (40 + 6 * bx)),
                    Math.Max(Math.Abs(c.G - Math.Min(255, 30 + 8 * by)), Math.Abs(c.B - (200 - 3 * bx)))));
                Assert.Equal(255, c.A);
            }
        Assert.True(worst <= 3, $"largest channel error {worst}");
    }

    // Issue #31. A lossless 4x2 RGB image stored with orientation 6 (a quarter turn clockwise):
    // pixel (x, y) is (60x, 200y, 7), so it is shown 2 wide and 4 tall.
    private const string OrientedJxl = "/woIcAvEBAgCAQB8AEsYixXCCWybcNsfYC8GACazz/esZc9CWr14cWNiWQA=";

    // Issue #31. A lossless 3x2 animation of two frames: all red (200, 10, 10), then all green.
    private const string AnimatedJxl = "/woIQEETAIgJCAIBADgASxiLFcJJQuUBxAgA8wEIAgUAOABLGIsVwklC5QGcCMwHAA==";

    [Fact]
    public void JxlOrientationIsApplied()
    {
        using Surface decoded = DecodeJxl(OrientedJxl);
        Assert.Equal((2, 4), (decoded.Width, decoded.Height));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 2; x++)
            {
                int storedX = y, storedY = 1 - x;
                Assert.Equal(ColorBgra.FromBgra(7, (byte)(storedY * 200), (byte)(storedX * 60), 255), decoded[x, y]);
            }
    }

    [Fact]
    public void JxlAnimationOpensOnItsFirstFrame()
    {
        using Surface decoded = DecodeJxl(AnimatedJxl);
        Assert.Equal((3, 2), (decoded.Width, decoded.Height));
        Assert.Equal(ColorBgra.FromBgra(10, 10, 200, 255), decoded[2, 1]);
    }

    private static Surface DecodeJxl(string base64)
    {
        var codec = new JxlCodec();
        if (Environment.GetEnvironmentVariable("KAWAPAINT_REQUIRE_NATIVE_CODECS") is { Length: > 0 })
            Assert.True(codec.IsAvailable, $"{codec.DisplayName} library not found");
        Assert.SkipUnless(codec.IsAvailable, $"{codec.DisplayName} library not installed");
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        return codec.Decode(stream);
    }
}
