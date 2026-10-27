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
}
