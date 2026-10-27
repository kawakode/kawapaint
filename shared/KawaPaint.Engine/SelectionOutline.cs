// KawaPaint - traces the outline of a selection mask for the marching ants.
//
// The outline is the set of pixel edges that separate a selected pixel (any non-zero coverage)
// from an unselected one, with collinear neighbours merged into one segment. A rectangle comes
// out as four segments however large it is, and the scan stops at the selection's bounding box,
// so redrawing the ants costs a handful of lines instead of one shape per edge pixel.

namespace KawaPaint.Engine;

/// <summary>One straight run of the outline, in image pixel coordinates (pixel corners).</summary>
public readonly record struct OutlineSegment(int X0, int Y0, int X1, int Y1);

public static class SelectionOutline
{
    /// <summary>The merged edge segments of <paramref name="selection"/>; empty when inactive.</summary>
    public static List<OutlineSegment> Trace(Selection selection)
    {
        var segments = new List<OutlineSegment>();
        if (!selection.IsActive) return segments;

        var (bx, by, bw, bh) = selection.GetBounds();
        if (bw == 0 || bh == 0) return segments;

        ReadOnlySpan<byte> mask = selection.Mask;
        int width = selection.Width;

        // Each row is reduced to its transitions: the x positions where coverage switches between
        // zero and non-zero, found with vectorised searches. Those are also the vertical edges in
        // that row. A horizontal edge lies wherever the rows on either side disagree, and on sorted
        // transition lists that is their symmetric difference, which equally marks where vertical
        // runs start or stop. So the only per-pixel work is the search itself.
        var previous = new List<int>();     // the row above the bounds has no transitions
        var current = new List<int>();
        var changed = new List<int>();
        var runStart = new int[bw + 1];     // per column line: row its vertical run began on, or -1
        Array.Fill(runStart, -1);

        for (int y = by; y <= by + bh; y++)
        {
            current.Clear();
            if (y < by + bh) FindTransitions(mask.Slice(y * width + bx, bw), bx, current);

            SymmetricDifference(previous, current, changed);
            for (int i = 0; i + 1 < changed.Count; i += 2)
                segments.Add(new OutlineSegment(changed[i], y, changed[i + 1], y));
            foreach (int x in changed)
            {
                ref int y0 = ref runStart[x - bx];
                if (y0 < 0) y0 = y;
                else { segments.Add(new OutlineSegment(x, y0, x, y)); y0 = -1; }
            }

            (previous, current) = (current, previous);
        }
        return segments;
    }

    /// <summary>Appends to <paramref name="into"/> the x of every zero/non-zero switch in
    /// <paramref name="row"/>, treating both ends as bordered by unselected pixels.</summary>
    private static void FindTransitions(ReadOnlySpan<byte> row, int offset, List<int> into)
    {
        int x = 0;
        while (x < row.Length)
        {
            int start = row[x..].IndexOfAnyExcept((byte)0);
            if (start < 0) return;
            x += start;
            into.Add(offset + x);
            int length = row[x..].IndexOf((byte)0);
            if (length < 0) { into.Add(offset + row.Length); return; }
            x += length;
            into.Add(offset + x);
        }
    }

    /// <summary>Writes the values in exactly one of two sorted lists to <paramref name="into"/>, sorted.</summary>
    private static void SymmetricDifference(List<int> a, List<int> b, List<int> into)
    {
        into.Clear();
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (j == b.Count || (i < a.Count && a[i] < b[j])) into.Add(a[i++]);
            else if (i == a.Count || b[j] < a[i]) into.Add(b[j++]);
            else { i++; j++; }
        }
    }
}
