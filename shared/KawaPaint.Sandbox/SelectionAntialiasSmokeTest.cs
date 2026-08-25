using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

/// <summary>
/// Antialiased selection edges. Two things need proving, and they pull in opposite directions:
/// that the graded path actually produces correct coverage, and that the binary path callers have
/// always used is still byte-for-byte what it was. The area assertions are the real check on the
/// rasterizers - "it produced some values between 0 and 255" would pass for a completely wrong
/// shape, whereas summed coverage has to match the shape's actual area.
/// </summary>
internal static class SelectionAntialiasSmokeTest
{
    public static void RunAll()
    {
        GridAlignedRectangleIsIdenticalEitherWay();
        FractionalRectangleCoverageEqualsItsArea();
        EllipseCoverageEqualsItsArea();
        PolygonCoverageEqualsItsArea();
        ClipBlendsPartialCoverage();
        SetOperationsAreUnchangedOnBinaryMasks();
        SubtractIsIdempotentOnFeatheredEdges();
        InvertComplementsCoverage();
        Console.WriteLine("SELECTION AA SMOKE OK - area-exact rect/ellipse/polygon, blended clip, binary parity");
    }

    /// <summary>
    /// A rectangle whose edges land on pixel boundaries has no partial pixels, so asking for
    /// antialiasing must change nothing at all. This is the invariant that says the AA path is a
    /// generalization of the old one rather than a different shape that merely looks similar.
    /// </summary>
    private static void GridAlignedRectangleIsIdenticalEitherWay()
    {
        var hard = new Selection(64, 64);
        var soft = new Selection(64, 64);
        hard.ReplaceWithRectangle(10, 10, 50, 50);
        soft.ReplaceWithRectangle(10, 10, 50, 50, antialias: true);

        Assert(hard.Mask.SequenceEqual(soft.Mask), "grid-aligned AA rectangle differs from the binary one");
        Assert(soft.IsActive, "grid-aligned AA rectangle is inactive");
        foreach (byte b in soft.Mask)
            Assert(b is 0 or 255, "a grid-aligned rectangle produced a partial pixel");
    }

    private static void FractionalRectangleCoverageEqualsItsArea()
    {
        var selection = new Selection(100, 100);
        const double left = 10.25, top = 20.5, right = 60.75, bottom = 70.5;
        selection.ReplaceWithRectangle(left, top, right, bottom, antialias: true);

        double expected = (right - left) * (bottom - top);
        double actual = TotalCoverage(selection);
        // Every pixel rounds to a byte, so the error is bounded by half a step per touched pixel.
        Assert(Math.Abs(actual - expected) < 8,
            $"AA rectangle covers {actual:0.##} but its area is {expected:0.##}");
        Assert(HasPartialPixel(selection), "a fractional rectangle produced no antialiased edge");
    }

    private static void EllipseCoverageEqualsItsArea()
    {
        var selection = new Selection(100, 100);
        selection.ReplaceWithEllipse(20, 20, 80, 80, antialias: true);

        double expected = Math.PI * 30 * 30;
        double actual = TotalCoverage(selection);
        Assert(Math.Abs(actual - expected) / expected < 0.01,
            $"AA ellipse covers {actual:0.##}, expected about {expected:0.##}");

        Assert(selection.CoverageAt(50, 50) == 255, "the centre of an AA ellipse is not fully selected");
        Assert(selection.CoverageAt(0, 0) == 0, "a corner outside an AA ellipse is selected");
        Assert(HasPartialPixel(selection), "an AA ellipse produced no antialiased edge");
    }

    private static void PolygonCoverageEqualsItsArea()
    {
        var selection = new Selection(100, 100);
        // A triangle, so the expected area is exact and the edges are all off-grid diagonals.
        var points = new (double X, double Y)[] { (10, 10), (80, 25), (30, 90) };
        selection.ReplaceWithPolygon(points, antialias: true);

        double expected = Math.Abs(
            points[0].X * (points[1].Y - points[2].Y) +
            points[1].X * (points[2].Y - points[0].Y) +
            points[2].X * (points[0].Y - points[1].Y)) / 2;
        double actual = TotalCoverage(selection);
        Assert(Math.Abs(actual - expected) / expected < 0.01,
            $"AA polygon covers {actual:0.##}, expected about {expected:0.##}");
        Assert(HasPartialPixel(selection), "an AA polygon produced no antialiased edge");
    }

    /// <summary>
    /// The half that makes the feather visible. A partially covered pixel must come back as a
    /// genuine mix of the edit and the original - not restored outright (which is what the old
    /// binary Clip did to anything under 255) and not kept whole.
    /// </summary>
    private static void ClipBlendsPartialCoverage()
    {
        var selection = new Selection(32, 32);
        selection.ReplaceWithEllipse(4.5, 4.5, 27.5, 27.5, antialias: true);

        // Find a genuinely partial pixel to assert against rather than assuming where the edge fell.
        int px = -1, py = -1;
        for (int y = 0; y < 32 && px < 0; y++)
            for (int x = 0; x < 32; x++)
            {
                byte c = selection.CoverageAt(x, y);
                if (c is > 40 and < 215) { px = x; py = y; break; }
            }
        Assert(px >= 0, "no partially covered pixel to test Clip against");
        byte coverage = selection.CoverageAt(px, py);

        using var edited = new Surface(32, 32);
        using var original = new Surface(32, 32);
        edited.Clear(ColorBgra.FromBgra(0, 0, 255, 255));      // red edit
        original.Clear(ColorBgra.FromBgra(255, 0, 0, 255));    // blue original

        selection.Clip(edited, original);

        ColorBgra blended = ReadPixel(edited, px, py);
        Assert(blended.R is > 0 and < 255 && blended.B is > 0 and < 255,
            $"partial pixel came back as a hard {blended.R}/{blended.B} instead of a blend");

        int expectedRed = (int)Math.Round(255 * (coverage / 255.0));
        Assert(Math.Abs(blended.R - expectedRed) <= 2,
            $"blend at coverage {coverage} gave red {blended.R}, expected about {expectedRed}");

        // The extremes must still be absolute: fully outside restores, fully inside keeps.
        Assert(ReadPixel(edited, 0, 0).B == 255, "a fully unselected pixel was not restored");
        Assert(ReadPixel(edited, 16, 16).R == 255, "a fully selected pixel did not keep its edit");
    }

    /// <summary>
    /// Combine's three branches were rewritten as max / min-with-complement / min. On binary input
    /// they must produce exactly what the old `if` statements produced, or every existing selection
    /// workflow changes behaviour silently.
    /// </summary>
    private static void SetOperationsAreUnchangedOnBinaryMasks()
    {
        foreach (var mode in new[] { SelectionCombineMode.Add, SelectionCombineMode.Subtract,
                                     SelectionCombineMode.Intersect })
        {
            var actual = new Selection(40, 40);
            actual.ReplaceWithRectangle(5, 5, 25, 25);
            var shape = new Selection(40, 40);
            shape.ReplaceWithRectangle(15, 15, 35, 35);

            // Independent reference implementation of the pre-change semantics.
            var expected = new Selection(40, 40);
            expected.ReplaceWithRectangle(5, 5, 25, 25);
            var reference = new byte[40 * 40];
            expected.Mask.CopyTo(reference);
            for (int i = 0; i < reference.Length; i++)
            {
                bool inShape = shape.Mask[i] != 0;
                reference[i] = mode switch
                {
                    SelectionCombineMode.Add => inShape ? (byte)255 : reference[i],
                    SelectionCombineMode.Subtract => inShape ? (byte)0 : reference[i],
                    _ => inShape ? reference[i] : (byte)0
                };
            }

            actual.Combine(mode, shape);
            Assert(actual.Mask.SequenceEqual(reference), $"{mode} changed behaviour on a binary mask");
        }
    }

    /// <summary>
    /// Why min/max rather than the multiplicative fuzzy-set operators: subtracting the same shape a
    /// second time must be a no-op. Multiplying would keep eating into a feathered edge every time.
    /// </summary>
    private static void SubtractIsIdempotentOnFeatheredEdges()
    {
        var selection = new Selection(64, 64);
        selection.ReplaceWithRectangle(4, 4, 60, 60);
        var shape = new Selection(64, 64);
        shape.ReplaceWithEllipse(10.5, 10.5, 40.5, 40.5, antialias: true);

        selection.Combine(SelectionCombineMode.Subtract, shape);
        var once = selection.Mask.ToArray();
        selection.Combine(SelectionCombineMode.Subtract, shape);

        Assert(selection.Mask.SequenceEqual(once), "subtracting the same feathered shape twice kept eroding");
    }

    private static void InvertComplementsCoverage()
    {
        var selection = new Selection(64, 64);
        selection.ReplaceWithEllipse(8.5, 8.5, 50.5, 50.5, antialias: true);
        var before = selection.Mask.ToArray();

        selection.Invert();
        for (int i = 0; i < before.Length; i++)
            Assert(selection.Mask[i] == 255 - before[i], "Invert did not complement coverage");

        selection.Invert();
        Assert(selection.Mask.SequenceEqual(before), "inverting twice did not round-trip");
    }

    private static double TotalCoverage(Selection selection)
    {
        double total = 0;
        foreach (byte b in selection.Mask) total += b / 255.0;
        return total;
    }

    private static bool HasPartialPixel(Selection selection)
    {
        foreach (byte b in selection.Mask)
            if (b is not 0 and not 255) return true;
        return false;
    }

    private static unsafe ColorBgra ReadPixel(Surface surface, int x, int y)
        => *(ColorBgra*)surface.GetPointPointer(x, y);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
