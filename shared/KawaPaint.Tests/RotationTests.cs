using KawaPaint.Engine;
using KawaPaint.Engine.MailMerge;
using KawaPaint.Engine.Scripting;
using Xunit;

namespace KawaPaint.Tests;

public class RotationTests
{
    private static Document Sample(int width = 40, int height = 20)
    {
        var doc = new Document(width, height);
        Layer layer = doc.AddLayer();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                layer.Surface[x, y] = ColorBgra.FromBgra((byte)(x * 6), (byte)(y * 12), 128, 255);
        return doc;
    }

    private static int LargestDifference(Surface a, Surface b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int worst = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                ColorBgra p = a[x, y], q = b[x, y];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)),
                    Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A))));
            }
        return worst;
    }

    [Theory]
    [InlineData(90, 20, 40)]
    [InlineData(180, 40, 20)]
    [InlineData(-90, 20, 40)]
    [InlineData(45, 43, 43)]
    public void BoundsHoldTheTurnedImage(double degrees, int width, int height) =>
        Assert.Equal((width, height), Surface.RotatedBounds(40, 20, degrees));

    [Fact]
    public void QuarterTurnMatchesRotate90()
    {
        using Document doc = Sample();
        using Document exact = DocumentOps.Rotate90(doc, clockwise: true);
        using Document free = DocumentOps.Rotate(doc, 90, expandCanvas: true);
        Assert.True(LargestDifference(exact.Layers[0].Surface, free.Layers[0].Surface) <= 1);
    }

    [Fact]
    public void HalfTurnIsTwoQuarterTurns()
    {
        using Document doc = Sample();
        using Document once = DocumentOps.Rotate90(doc, true);
        using Document twice = DocumentOps.Rotate90(once, true);
        DocumentOps.Rotate180(doc);
        Assert.Equal(0, LargestDifference(twice.Layers[0].Surface, doc.Layers[0].Surface));
    }

    [Fact]
    public void FreeTurnGrowsCanvasAndLeavesCornersTransparent()
    {
        using Document doc = Sample(40, 40);
        using Document turned = DocumentOps.Rotate(doc, 45, expandCanvas: true);
        Surface s = turned.Layers[0].Surface;
        Assert.Equal((57, 57), (s.Width, s.Height));
        Assert.Equal(0, s[0, 0].A);
        Assert.Equal(255, s[28, 28].A);
        // The centre stays the centre.
        ColorBgra centre = doc.Layers[0].Surface[20, 20];
        Assert.True(Math.Abs(s[28, 28].B - centre.B) <= 8 && Math.Abs(s[28, 28].G - centre.G) <= 16);

        using Document kept = DocumentOps.Rotate(doc, 45, expandCanvas: false);
        Assert.Equal((40, 40), (kept.Width, kept.Height));
        Assert.Equal(0, kept.Layers[0].Surface[0, 0].A);
    }

    [Fact]
    public void TextZonesFollowTheirCentre()
    {
        using Document doc = Sample(100, 50);
        doc.DynamicTextZones.Add(new DynamicTextZone { X = 0, Y = 0, Width = 20, Height = 10 });
        using Document turned = DocumentOps.Rotate(doc, 90, expandCanvas: true);
        DynamicTextZone zone = Assert.Single(turned.DynamicTextZones);
        // Top-left corner content ends at the top-right after a clockwise quarter turn.
        Assert.Equal((35, 5, 20, 10), (zone.X, zone.Y, zone.Width, zone.Height));

        DocumentOps.Rotate180(doc);
        Assert.Equal((80, 40), (doc.DynamicTextZones[0].X, doc.DynamicTextZones[0].Y));
    }

    [Fact]
    public void RotateZoomIdentityKeepsTheLayer()
    {
        using Document doc = Sample();
        using Surface before = doc.Layers[0].Surface.Clone();
        new RotateZoomEffect(0, 1).Apply(doc.Layers[0].Surface);
        Assert.Equal(0, LargestDifference(before, doc.Layers[0].Surface));
    }

    [Fact]
    public void RotateZoomScalesAndPans()
    {
        using Document doc = Sample(40, 40);
        Surface s = doc.Layers[0].Surface;
        new RotateZoomEffect(0, 0.5).Apply(s);
        Assert.Equal(0, s[2, 2].A);
        Assert.Equal(255, s[20, 20].A);

        using Document panned = Sample(40, 40);
        new RotateZoomEffect(0, 1, panXPercent: 50).Apply(panned.Layers[0].Surface);
        Assert.Equal(0, panned.Layers[0].Surface[10, 10].A);
        Assert.Equal(255, panned.Layers[0].Surface[30, 10].A);
    }

    [Fact]
    public void ScriptsReplayRotations()
    {
        Document doc = Sample();
        var script = new ScriptFile();
        script.Steps.Add(new ScriptStep("image.rotate", new double[] { 90, 1 }));
        script.Steps.Add(new ScriptStep("image.rotate180"));
        script.Steps.Add(new ScriptStep("effect.rotatezoom", new double[] { 10, 0.8, 0, 0 }));
        var results = ScriptExecutor.Run(ref doc, script);
        Assert.All(results, r => Assert.Equal(ScriptStepOutcome.Applied, r.Outcome));
        Assert.Equal((20, 40), (doc.Width, doc.Height));
        doc.Dispose();
    }
}
