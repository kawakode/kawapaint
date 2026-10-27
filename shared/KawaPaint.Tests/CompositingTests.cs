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
