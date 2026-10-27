using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>The marching-ants outline: merged segments that trace exactly the selection's edge.</summary>
public class SelectionOutlineTests
{
    [Fact]
    public void RectangleIsFourSegments()
    {
        var selection = new Selection(4000, 3000);
        selection.ReplaceWithRectangle(100, 200, 3100, 2200);

        var segments = SelectionOutline.Trace(selection);

        Assert.Equal(4, segments.Count);
        var (x, y, w, h) = selection.GetBounds();
        Assert.Contains(new OutlineSegment(x, y, x + w, y), segments);
        Assert.Contains(new OutlineSegment(x, y + h, x + w, y + h), segments);
        Assert.Contains(new OutlineSegment(x, y, x, y + h), segments);
        Assert.Contains(new OutlineSegment(x + w, y, x + w, y + h), segments);
    }

    [Fact]
    public void SelectAllFollowsTheImageBorder()
    {
        var selection = new Selection(64, 48);
        selection.SelectAll();

        var segments = SelectionOutline.Trace(selection);

        Assert.Equal(4, segments.Count);
        Assert.Contains(new OutlineSegment(0, 0, 64, 0), segments);
        Assert.Contains(new OutlineSegment(64, 0, 64, 48), segments);
    }

    [Fact]
    public void InactiveSelectionHasNoOutlineAndOnePixelHasFour()
    {
        var selection = new Selection(16, 16);
        Assert.Empty(SelectionOutline.Trace(selection));

        selection.Select(3, 5);
        Assert.Equal(4, SelectionOutline.Trace(selection).Count);
    }

    /// <summary>On a noisy mask (holes, diagonals, antialiased coverage) the segments must cover
    /// every selected/unselected pixel edge exactly once and nothing else.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void SegmentsCoverExactlyTheBoundaryEdges(int seed)
    {
        const int W = 41, H = 37;
        var random = new Random(seed);
        var selection = new Selection(W, H);
        selection.ReplaceWithEllipse(2, 3, 38, 33, antialias: true);
        var holes = new Selection(W, H);
        for (int i = 0; i < 300; i++) holes.Select(random.Next(W), random.Next(H));
        selection.Combine(SelectionCombineMode.Subtract, holes);
        var specks = new Selection(W, H);
        for (int i = 0; i < 60; i++) specks.Select(random.Next(W), random.Next(H));
        selection.Combine(SelectionCombineMode.Add, specks);

        bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && selection.IsSelected(x, y);

        var expected = new HashSet<(int, int, bool)>();   // (x, y, horizontal) unit edges
        for (int y = 0; y <= H; y++)
            for (int x = 0; x <= W; x++)
            {
                if (In(x, y - 1) != In(x, y) && x < W) expected.Add((x, y, true));
                if (In(x - 1, y) != In(x, y) && y < H) expected.Add((x, y, false));
            }

        var actual = new List<(int, int, bool)>();
        foreach (OutlineSegment s in SelectionOutline.Trace(selection))
        {
            if (s.Y0 == s.Y1)
                for (int x = s.X0; x < s.X1; x++) actual.Add((x, s.Y0, true));
            else
            {
                Assert.Equal(s.X0, s.X1);
                for (int y = s.Y0; y < s.Y1; y++) actual.Add((s.X0, y, false));
            }
        }

        Assert.Equal(actual.Count, actual.Distinct().Count());
        Assert.True(expected.SetEquals(actual));
    }
}
