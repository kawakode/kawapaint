using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

public class StrokeTests
{
    private static readonly ColorBgra Red = ColorBgra.FromBgra(0, 0, 255, 255);
    private static readonly ColorBgra Blue = ColorBgra.FromBgra(255, 0, 0, 255);

    /// <summary>A 20x20 square at (20,20) on a 60x60 canvas.</summary>
    private static byte[] Square(int size = 60, int x = 20, int y = 20, int side = 20)
    {
        var selection = new Selection(size, size);
        selection.ReplaceWithRectangle(x, y, x + side, y + side);
        return selection.Mask.ToArray();
    }

    private static Surface Stroke(byte[] shape, StrokeOptions options, int size = 60)
    {
        var surface = new Surface(size, size);
        StrokeOps.Stroke(surface, shape, options);
        return surface;
    }

    [Fact]
    public void OutsideStrokeSurroundsTheShapeAtTheGivenWidth()
    {
        using var s = Stroke(Square(), new StrokeOptions { Width = 5, Position = StrokePosition.Outside, Color1 = Red });
        Assert.Equal(Red, s[15, 30]);            // 5 px out from the left edge
        Assert.Equal(Red, s[19, 30]);
        Assert.Equal(0, s[14, 30].A);            // beyond the width
        Assert.Equal(0, s[20, 30].A);            // the shape itself stays untouched
        Assert.Equal(0, s[30, 30].A);
        Assert.Equal(Red, s[30, 44]);            // all the way round
        Assert.Equal(0, s[45, 30].A);
        Assert.Equal(0, s[15, 15].A);            // the outer corner is rounded
    }

    [Fact]
    public void InsideStrokeStaysWithinTheShape()
    {
        using var s = Stroke(Square(), new StrokeOptions { Width = 3, Position = StrokePosition.Inside, Color1 = Red });
        Assert.Equal(Red, s[20, 30]);
        Assert.Equal(Red, s[22, 30]);
        Assert.Equal(0, s[23, 30].A);
        Assert.Equal(0, s[19, 30].A);
        Assert.Equal(Red, s[20, 20]);            // inner corners stay square
        Assert.Equal(0, s[30, 30].A);
    }

    [Fact]
    public void CenteredStrokeStraddlesTheEdge()
    {
        using var s = Stroke(Square(), new StrokeOptions { Width = 4, Position = StrokePosition.Center, Color1 = Red });
        Assert.Equal(Red, s[18, 30]);
        Assert.Equal(Red, s[21, 30]);
        Assert.Equal(0, s[17, 30].A);
        Assert.Equal(0, s[22, 30].A);
    }

    [Fact]
    public void InsideStrokeAlongTheCanvasBorderFramesTheImage()
    {
        var all = new Selection(40, 40);
        all.SelectAll();
        using var s = Stroke(all.Mask.ToArray(), new StrokeOptions { Width = 2, Position = StrokePosition.Inside, Color1 = Red }, 40);
        Assert.Equal(Red, s[0, 20]);
        Assert.Equal(Red, s[39, 20]);
        Assert.Equal(Red, s[20, 1]);
        Assert.Equal(0, s[20, 20].A);
    }

    [Fact]
    public void LayerAlphaIsAShapeToo()
    {
        using var layer = new Surface(60, 60);
        for (int y = 25; y < 35; y++)
            for (int x = 25; x < 35; x++)
                layer[x, y] = Blue;
        var shape = StrokeOps.AlphaCoverage(layer);
        StrokeOps.Stroke(layer, shape, new StrokeOptions { Width = 2, Color1 = Red });
        Assert.Equal(Red, layer[23, 30]);
        Assert.Equal(Blue, layer[25, 30]);       // the drawing is left as it was
        Assert.Equal(0, layer[22, 30].A);
    }

    [Fact]
    public void AcrossGradientRunsFromTheInnerToTheOuterEdge()
    {
        using var s = Stroke(Square(), new StrokeOptions
        {
            Width = 10, Position = StrokePosition.Outside, Fill = StrokeFill.Across, Color1 = Red, Color2 = Blue
        });
        ColorBgra inner = s[19, 30], outer = s[11, 30];
        Assert.True(inner.R > 200 && inner.B < 60, $"inner {inner}");
        Assert.True(outer.B > 200 && outer.R < 60, $"outer {outer}");
    }

    [Fact]
    public void HorizontalGradientRunsLeftToRight()
    {
        using var s = Stroke(Square(), new StrokeOptions
        {
            Width = 4, Fill = StrokeFill.Horizontal, Color1 = Red, Color2 = Blue
        });
        Assert.True(s[17, 30].R > s[17, 30].B);
        Assert.True(s[42, 30].B > s[42, 30].R);
    }

    [Fact]
    public void OpacityScalesTheStroke()
    {
        using var s = Stroke(Square(), new StrokeOptions { Width = 4, Color1 = Red, Opacity = 0.5 });
        Assert.InRange(s[17, 30].A, 120, 135);
    }

    [Fact]
    public void FieldIsReusedAcrossSettingsAndRendersOnlyNearTheShape()
    {
        var field = StrokeField.Build(Square(), 60, 60, 12);
        using var a = new Surface(60, 60);
        var dirty = field.Render(a, new StrokeOptions { Width = 3, Color1 = Red });
        Assert.Equal(new EffectBounds(17, 17, 26, 26), dirty);
        using var b = new Surface(60, 60);
        field.Render(b, new StrokeOptions { Width = 3, Position = StrokePosition.Inside, Color1 = Red });
        Assert.Equal(Red, b[20, 30]);
        Assert.Equal(0, b[17, 30].A);
    }

    [Fact]
    public void EmptyShapeDrawsNothing()
    {
        using var s = new Surface(30, 30);
        var dirty = StrokeOps.Stroke(s, new byte[30 * 30], new StrokeOptions { Width = 5, Color1 = Red });
        Assert.True(dirty.IsEmpty);
        Assert.Equal(0, s[15, 15].A);
    }

    [Fact]
    public void AntialiasedEdgeGivesPartialCoverage()
    {
        var selection = new Selection(60, 60);
        selection.ReplaceWithEllipse(15, 15, 45, 45, antialias: true);
        using var s = Stroke(selection.Mask.ToArray(), new StrokeOptions { Width = 3, Color1 = Red });
        int partial = 0;
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
                if (s[x, y].A is > 0 and < 255) partial++;
        Assert.True(partial > 20, $"only {partial} soft pixels");
        Assert.Equal(Red, s[30, 13]);            // ~2 px above the top of the ellipse
    }
}
