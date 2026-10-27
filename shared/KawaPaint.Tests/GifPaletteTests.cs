using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Issue #29: GIF export used 5-bit bin centres as palette colours.</summary>
public class GifPaletteTests
{
    [Fact]
    public void FewColoursComeBackExactly()
    {
        // 200 colours, many of them sharing a 5-bit bin with a neighbour (e.g. 30,20,10 and 31,21,11).
        using var source = new Surface(40, 20);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
            {
                int i = (y / 2) * 20 + x / 2;
                source[x, y] = ColorBgra.FromBgra((byte)(10 + i % 3), (byte)(20 + i % 7), (byte)(30 + i), 255);
            }

        using Surface decoded = RoundTrip(source);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(source[x, y], decoded[x, y]);
    }

    [Fact]
    public void FlatAreaKeepsItsColourInAManyColourImage()
    {
        // Left half is the flat colour from the report, right half a noisy gradient with far more
        // than 256 colours, so the median cut runs.
        using var source = new Surface(128, 64);
        var random = new Random(7);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 128; x++)
                source[x, y] = x < 64
                    ? ColorBgra.FromBgra(10, 20, 30, 255)
                    : ColorBgra.FromBgra((byte)random.Next(256), (byte)(y * 4), (byte)(x * 2), 255);

        using Surface decoded = RoundTrip(source);
        double error = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                ColorBgra c = decoded[x, y];
                error += Math.Abs(c.R - 30) + Math.Abs(c.G - 20) + Math.Abs(c.B - 10);
            }
        error /= 64 * 64 * 3;
        Assert.True(error < 1, $"mean channel error {error:0.00}");
    }

    private static Surface RoundTrip(Surface source)
    {
        using var stream = new MemoryStream();
        new GifCodec().Encode(source, stream, EncodeOptions.Default);
        stream.Position = 0;
        return new GifCodec().Decode(stream);
    }
}
