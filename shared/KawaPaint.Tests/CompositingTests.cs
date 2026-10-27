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
}
