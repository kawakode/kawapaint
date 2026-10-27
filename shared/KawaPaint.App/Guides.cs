// KawaPaint - guide lines dragged out of the rulers. A guide sits on a pixel boundary (an integer
// image coordinate) and is view state: it is not part of the image, is never saved into it, and
// is not an undo step. Pure logic, so snapping can be tested without a window.

using System;
using System.Collections.Generic;

namespace KawaPaint.App;

public sealed class Guide
{
    public Guide(bool horizontal, int position)
    {
        Horizontal = horizontal;
        Position = position;
    }

    /// <summary>True for a horizontal line (dragged from the top ruler): Position is a Y.</summary>
    public bool Horizontal { get; }

    /// <summary>Image coordinate of the line: a Y for a horizontal guide, an X for a vertical one.</summary>
    public int Position { get; set; }
}

public sealed class GuideSet
{
    private readonly List<Guide> _guides = new();

    public IReadOnlyList<Guide> All => _guides;
    public int Count => _guides.Count;

    public Guide Add(bool horizontal, int position)
    {
        var guide = new Guide(horizontal, position);
        _guides.Add(guide);
        return guide;
    }

    public bool Remove(Guide guide) => _guides.Remove(guide);

    public void Clear() => _guides.Clear();

    /// <summary>The guide whose line passes within <paramref name="tolerance"/> image pixels of
    /// (x, y), nearest first; null when none does.</summary>
    public Guide? HitTest(double x, double y, double tolerance)
    {
        Guide? best = null;
        double bestDistance = tolerance;
        foreach (Guide guide in _guides)
        {
            double distance = Math.Abs((guide.Horizontal ? y : x) - guide.Position);
            if (distance <= bestDistance) { best = guide; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>Moves (x, y) onto the nearest vertical guide within tolerance on X, and the
    /// nearest horizontal one on Y, each axis on its own.</summary>
    public (double X, double Y) Snap(double x, double y, double tolerance)
    {
        double snappedX = x, snappedY = y, bestX = tolerance, bestY = tolerance;
        foreach (Guide guide in _guides)
        {
            if (guide.Horizontal)
            {
                double distance = Math.Abs(y - guide.Position);
                if (distance <= bestY) { bestY = distance; snappedY = guide.Position; }
            }
            else
            {
                double distance = Math.Abs(x - guide.Position);
                if (distance <= bestX) { bestX = distance; snappedX = guide.Position; }
            }
        }
        return (snappedX, snappedY);
    }
}
