using KawaPaint.Engine;
using KawaPaint.Engine.Scripting;
using Xunit;

namespace KawaPaint.Tests;

public class SelectionModifyTests
{
    private static Selection Square(int size = 60, int x = 20, int y = 20, int side = 20)
    {
        var selection = new Selection(size, size);
        selection.ReplaceWithRectangle(x, y, x + side, y + side);
        return selection;
    }

    [Fact]
    public void GrowAddsARoundedRim()
    {
        Selection selection = Square();
        selection.Grow(5);
        Assert.Equal((15, 15, 30, 30), selection.GetBounds());
        Assert.Equal(255, selection.CoverageAt(15, 30));          // straight edge moved out 5
        Assert.Equal(0, selection.CoverageAt(14, 30));
        Assert.Equal(0, selection.CoverageAt(15, 15));            // the corner is rounded, not square
        Assert.Equal(255, selection.CoverageAt(17, 17));          // ~4.2 px from the old corner
        Assert.Equal(255, selection.CoverageAt(30, 30));          // the inside is untouched
    }

    [Fact]
    public void ShrinkPullsTheEdgeIn()
    {
        Selection selection = Square();
        selection.Shrink(5);
        Assert.Equal((25, 25, 10, 10), selection.GetBounds());
        Assert.Equal(255, selection.CoverageAt(25, 25));          // inner corners stay square
        Assert.Equal(0, selection.CoverageAt(24, 30));
        Assert.True(selection.IsActive);
    }

    [Fact]
    public void ShrinkingPastTheMiddleLeavesAnEmptyActiveSelection()
    {
        Selection selection = Square();
        selection.Shrink(15);
        Assert.True(selection.IsActive);
        Assert.Equal((0, 0, 0, 0), selection.GetBounds());
    }

    [Fact]
    public void ShrinkKeepsTheCanvasBorder()
    {
        var selection = new Selection(30, 30);
        selection.SelectAll();
        selection.Shrink(4);
        Assert.Equal(255, selection.CoverageAt(0, 0));
        Assert.Equal((0, 0, 30, 30), selection.GetBounds());
    }

    [Fact]
    public void GrowThenShrinkGivesBackTheSquare()
    {
        Selection selection = Square();
        byte[] original = selection.Mask.ToArray();
        selection.Grow(3);
        selection.Shrink(3);
        // Only the four corner pixels may come back slightly soft from the rounded rim.
        int differing = original.Zip(selection.Mask.ToArray()).Count(p => p.First != p.Second);
        Assert.InRange(differing, 0, 4);
        Assert.Equal((20, 20, 20, 20), selection.GetBounds());
    }

    [Fact]
    public void FeatherSoftensOnlyTheEdge()
    {
        Selection selection = Square();
        selection.Feather(6);
        Assert.Equal(255, selection.CoverageAt(30, 30));
        Assert.Equal(0, selection.CoverageAt(2, 2));
        byte edge = selection.CoverageAt(20, 30);
        Assert.InRange(edge, (byte)100, (byte)160);              // about half way on the old edge
        Assert.True(selection.CoverageAt(18, 30) is > 0 and < 128);
        Assert.True(selection.CoverageAt(22, 30) is > 128 and < 255);
        for (int x = 10; x < 30; x++)                            // and it ramps up monotonically
            Assert.True(selection.CoverageAt(x, 30) <= selection.CoverageAt(x + 1, 30));
    }

    [Fact]
    public void InactiveSelectionIsLeftAlone()
    {
        var selection = new Selection(10, 10);
        selection.Grow(3);
        selection.Feather(3);
        selection.Shrink(3);
        Assert.False(selection.IsActive);
    }

    [Fact]
    public void SnapshotUndoesAModification()
    {
        Selection selection = Square();
        var before = SelectionSnapshot.Capture(selection);
        selection.Feather(4);
        Assert.False(before.Matches(selection));
        before.RestoreTo(selection);
        Assert.True(before.Matches(selection));
    }

    private static Document Content()
    {
        var doc = new Document(50, 40);
        doc.AddLayer().Surface.ClearRect(10, 5, 8, 6, ColorBgra.FromBgra(0, 0, 255, 255));
        Layer hidden = doc.AddLayer();
        hidden.Visible = false;
        hidden.Surface[30, 20] = ColorBgra.FromBgra(255, 0, 0, 40);
        return doc;
    }

    [Fact]
    public void ContentBoundsCoverEveryLayer()
    {
        using Document doc = Content();
        Assert.Equal((10, 5, 21, 16), DocumentOps.ContentBounds(doc));
    }

    [Fact]
    public void ContentBoundsOfATransparentImageIsNull()
    {
        using var doc = new Document(8, 8);
        doc.AddLayer();
        Assert.Null(DocumentOps.ContentBounds(doc));
    }

    [Fact]
    public void ContentBoundsIncludeOtherFrames()
    {
        using var doc = new Document(20, 20);
        doc.AddLayer().Surface[2, 2] = ColorBgra.FromBgra(0, 0, 0, 255);
        var other = new Layer(new Surface(20, 20), "Frame 2");
        other.Surface[15, 12] = ColorBgra.FromBgra(0, 0, 0, 255);
        doc.AddFrame(new DocumentFrame(new[] { other }, "Frame 2", 100));
        Assert.Equal((2, 2, 14, 11), DocumentOps.ContentBounds(doc));
    }

    [Fact]
    public void AutoCropScriptStepTrimsTheDocument()
    {
        Assert.True(KawaPaint.App.Core.Scripting.ScriptRecorder.IsScriptable("image.autocrop"));
        var script = new ScriptFile();
        script.Steps.Add(new ScriptStep("image.autocrop"));
        Document doc = Content();
        var results = ScriptExecutor.Run(ref doc, script);
        Assert.All(results, r => Assert.Equal(ScriptStepOutcome.Applied, r.Outcome));
        Assert.Equal((21, 16), (doc.Width, doc.Height));
        Assert.Equal(255, doc.Layers[0].Surface[0, 0].A);
        doc.Dispose();
    }
}
