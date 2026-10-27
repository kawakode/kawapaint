using KawaPaint.App;
using Xunit;

namespace KawaPaint.Tests;

public class GuideTests
{
    [Fact]
    public void SnapMovesEachAxisToItsNearestGuide()
    {
        var guides = new GuideSet();
        guides.Add(horizontal: true, 50);
        guides.Add(horizontal: true, 54);
        guides.Add(horizontal: false, 100);
        Assert.Equal((100.0, 54.0), guides.Snap(97, 53, tolerance: 4));
        Assert.Equal((90.0, 50.0), guides.Snap(90, 51, tolerance: 4));   // X too far to snap
    }

    [Fact]
    public void SnapLeavesFarPointsAlone() =>
        Assert.Equal((10.5, 20.5), new GuideSet().Snap(10.5, 20.5, 6));

    [Fact]
    public void HitTestOnlyLooksAcrossTheLine()
    {
        var guides = new GuideSet();
        Guide horizontal = guides.Add(horizontal: true, 30);
        Guide vertical = guides.Add(horizontal: false, 80);
        Assert.Same(horizontal, guides.HitTest(500, 32, 3));
        Assert.Same(vertical, guides.HitTest(79, 500, 3));
        Assert.Null(guides.HitTest(10, 10, 3));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var guides = new GuideSet();
        Guide first = guides.Add(true, 1);
        guides.Add(false, 2);
        Assert.True(guides.Remove(first));
        Assert.Equal(1, guides.Count);
        guides.Clear();
        Assert.Equal(0, guides.Count);
    }
}
