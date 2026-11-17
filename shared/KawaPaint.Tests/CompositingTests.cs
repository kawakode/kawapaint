using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>The fast compositing paths must give exactly what the reference formulas give.</summary>
public class CompositingTests
{
    public static TheoryData<BlendMode> Modes()
    {
        var data = new TheoryData<BlendMode>();
        foreach (BlendMode mode in Enum.GetValues<BlendMode>()) data.Add(mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public unsafe void SpanMatchesPerPixelComposite(BlendMode mode)
    {
        const int Count = 20000;
        var random = new Random((int)mode + 1);
        var source = new ColorBgra[Count];
        var destination = new ColorBgra[Count];
        for (int i = 0; i < Count; i++)
        {
            source[i] = Random(random, random.Next(4) == 0 ? (byte)255 : (byte)random.Next(256));
            // Mostly opaque backdrops (the fast path), with translucent ones mixed in.
            destination[i] = Random(random, random.Next(3) == 0 ? (byte)random.Next(256) : (byte)255);
        }

        foreach (int opacity in new[] { 255, 200, 77, 1 })
        {
            var actual = (ColorBgra[])destination.Clone();
            fixed (ColorBgra* d = actual)
            fixed (ColorBgra* s = source)
                Blending.CompositeSpan(mode, d, s, Count, opacity);

            for (int i = 0; i < Count; i++)
            {
                ColorBgra expected = Blending.Composite(mode, destination[i], source[i], opacity);
                Assert.True(expected == actual[i],
                    $"{mode} opacity {opacity}: {source[i]} over {destination[i]} gave {actual[i]}, expected {expected}");
            }
        }
    }

    private static ColorBgra Random(Random random, byte alpha) =>
        ColorBgra.FromBgra((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), alpha);

    [Fact]
    public void UndoReportsTheAreaItChanged()
    {
        var history = new HistoryStack();
        using var layer = new Layer(500, 400);
        using (Surface before = layer.Surface.Clone())
        {
            layer.Surface.ClearRect(130, 70, 10, 200, ColorBgra.FromBgra(0, 0, 255, 255));
            history.Push(TileDeltaMemento.Create(layer, before, "Stroke"));
        }

        history.Undo();
        Assert.Equal((128, 64, 64, 256), history.LastChangedArea);
        history.Redo();
        Assert.Equal((128, 64, 64, 256), history.LastChangedArea);

        history.Push(new DelegateMemento("Rename", () => { }, () => { }));
        history.Undo();
        Assert.Null(history.LastChangedArea);
        history.JumpTo(0);
        Assert.Equal((128, 64, 64, 256), history.LastChangedArea);
        history.JumpTo(2);
        Assert.Null(history.LastChangedArea);   // crossed a step that can change anything
    }
}

/// <summary>Compositing over a pre-flattened backdrop of the bottom layers equals a full composite.</summary>
public class BackdropCompositingTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]    // every layer below hidden: the backdrop draws nothing
    public void BackdropMatchesFullComposite(int firstLayer, bool hideBelow)
    {
        using var document = new Document(120, 90);
        var random = new Random(firstLayer);
        BlendMode[] modes = { BlendMode.Normal, BlendMode.Multiply, BlendMode.Normal, BlendMode.Screen };
        for (int i = 0; i < modes.Length; i++)
        {
            Layer layer = document.AddLayer();
            layer.BlendMode = modes[i];
            layer.Opacity = i == 2 ? (byte)180 : (byte)255;
            for (int y = 0; y < 90; y++)
                for (int x = 0; x < 120; x++)
                    if (random.Next(5) > 0)
                        layer.Surface[x, y] = ColorBgra.FromBgra((byte)random.Next(256), (byte)random.Next(256),
                            (byte)random.Next(256), (byte)random.Next(256));
            if (hideBelow && i < firstLayer) layer.Visible = false;
        }

        using Surface expected = document.Flatten();
        using var backdrop = new Surface(120, 90);
        bool drew = document.RenderBottomLayersTo(backdrop, firstLayer);
        Assert.Equal(!hideBelow, drew);

        using var actual = new Surface(120, 90);
        document.RenderTo(actual, 0, 0, 120, 90, drew ? backdrop : null, firstLayer);
        for (int y = 0; y < 90; y++)
            for (int x = 0; x < 120; x++)
                Assert.Equal(expected[x, y], actual[x, y]);

        // A partial rectangle over the same backdrop leaves the rest of dest alone.
        using var partial = new Surface(120, 90);
        document.RenderTo(partial, 30, 20, 40, 30, drew ? backdrop : null, firstLayer);
        Assert.Equal(expected[45, 35], partial[45, 35]);
        Assert.Equal(default, partial[5, 5]);
    }

    /// <summary>
    /// The document render composites every layer row by row in parallel bands. It must give
    /// exactly what blending each layer over the whole canvas in turn gives, for a canvas large
    /// enough to be split into many bands, a partial rectangle, and hidden or transparent layers.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void BandedRenderMatchesLayerByLayerReference(bool firstLayerCopied)
    {
        const int W = 700, H = 450;
        var random = new Random(firstLayerCopied ? 7 : 11);
        using var document = new Document(W, H);
        BlendMode[] modes = Enum.GetValues<BlendMode>();
        for (int i = 0; i < 9; i++)
        {
            Layer layer = document.AddLayer();
            layer.BlendMode = i == 0 && firstLayerCopied ? BlendMode.Normal : modes[random.Next(modes.Length)];
            layer.Opacity = i == 0 && firstLayerCopied ? (byte)255 : (byte)random.Next(1, 256);
            if (i == 4) layer.Visible = false;
            if (i == 6) layer.Opacity = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (random.Next(6) > 0)
                        layer.Surface[x, y] = Random(random, random.Next(4) == 0 ? (byte)random.Next(256) : (byte)255);
        }

        using var expected = new Surface(W, H);
        foreach (Layer layer in document.Layers)
        {
            if (!layer.Visible || layer.Opacity == 0) continue;
            for (int y = 0; y < H; y++)
                Blending.CompositeSpan(layer.BlendMode, (ColorBgra*)expected.GetRowPointer(y),
                    (ColorBgra*)layer.Surface.GetRowPointer(y), W, layer.Opacity);
        }

        using Surface actual = document.Flatten();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                Assert.Equal(expected[x, y], actual[x, y]);

        // A partial render touches only its rectangle and matches there.
        using var partial = new Surface(W, H);
        partial.Clear(ColorBgra.FromBgra(1, 2, 3, 4));
        document.RenderTo(partial, 101, 37, 333, 201);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool inside = x >= 101 && x < 434 && y >= 37 && y < 238;
                Assert.Equal(inside ? expected[x, y] : ColorBgra.FromBgra(1, 2, 3, 4), partial[x, y]);
            }
    }
}
