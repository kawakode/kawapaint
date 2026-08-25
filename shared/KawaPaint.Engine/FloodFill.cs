// KawaPaint - scanline flood fill for the paint-bucket tool. Replaces the contiguous
// region matching the seed pixel (within a per-channel tolerance) with the fill color.

namespace KawaPaint.Engine;

public static class FloodFill
{
    public static unsafe void Fill(Surface s, int seedX, int seedY, ColorBgra fill, int tolerance = 0)
    {
        if ((uint)seedX >= (uint)s.Width || (uint)seedY >= (uint)s.Height) return;

        ColorBgra target = s[seedX, seedY];
        if (target == fill && tolerance == 0) return;

        int w = s.Width, h = s.Height;
        var visited = new uint[(checked(w * h) + 31) / 32];
        var stack = new Stack<(int x, int y)>();
        stack.Push((seedX, seedY));

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int rowBase = y * w;
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);

            // Walk left to the run start.
            int xl = x;
            while (xl >= 0 && !IsVisited(visited, rowBase + xl) && Match(row[xl], target, tolerance))
                xl--;
            xl++;

            bool spanAbove = false, spanBelow = false;
            int aboveBase = rowBase - w, belowBase = rowBase + w;
            ColorBgra* above = y > 0 ? (ColorBgra*)s.GetRowPointer(y - 1) : null;
            ColorBgra* below = y < h - 1 ? (ColorBgra*)s.GetRowPointer(y + 1) : null;

            for (int xr = xl; xr < w; xr++)
            {
                if (IsVisited(visited, rowBase + xr) || !Match(row[xr], target, tolerance)) break;

                row[xr] = fill;
                MarkVisited(visited, rowBase + xr);

                if (above != null)
                {
                    bool m = Match(above[xr], target, tolerance) && !IsVisited(visited, aboveBase + xr);
                    if (m && !spanAbove) { stack.Push((xr, y - 1)); spanAbove = true; }
                    else if (!m) spanAbove = false;
                }
                if (below != null)
                {
                    bool m = Match(below[xr], target, tolerance) && !IsVisited(visited, belowBase + xr);
                    if (m && !spanBelow) { stack.Push((xr, y + 1)); spanBelow = true; }
                    else if (!m) spanBelow = false;
                }
            }
        }
    }

    /// <summary>Replaces every pixel matching the seed color (within tolerance) anywhere on the surface.</summary>
    public static unsafe void FillGlobal(Surface s, int seedX, int seedY, ColorBgra fill, int tolerance = 0)
    {
        if ((uint)seedX >= (uint)s.Width || (uint)seedY >= (uint)s.Height) return;
        ColorBgra target = s[seedX, seedY];

        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width; x++)
                if (Match(row[x], target, tolerance))
                    row[x] = fill;
        }
    }

    /// <summary>Selects the contiguous region matching the seed pixel (within tolerance) - the
    /// Magic Wand tool. Shares the scanline walk with <see cref="Fill"/>, marking the selection
    /// mask instead of writing pixel colors.</summary>
    public static unsafe void Select(Surface s, int seedX, int seedY, Selection selection, int tolerance = 0)
    {
        if ((uint)seedX >= (uint)s.Width || (uint)seedY >= (uint)s.Height) return;

        ColorBgra target = s[seedX, seedY];
        int w = s.Width, h = s.Height;
        var visited = new uint[(checked(w * h) + 31) / 32];
        var stack = new Stack<(int x, int y)>();
        stack.Push((seedX, seedY));

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int rowBase = y * w;
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);

            int xl = x;
            while (xl >= 0 && !IsVisited(visited, rowBase + xl) && Match(row[xl], target, tolerance))
                xl--;
            xl++;

            bool spanAbove = false, spanBelow = false;
            int aboveBase = rowBase - w, belowBase = rowBase + w;
            ColorBgra* above = y > 0 ? (ColorBgra*)s.GetRowPointer(y - 1) : null;
            ColorBgra* below = y < h - 1 ? (ColorBgra*)s.GetRowPointer(y + 1) : null;

            for (int xr = xl; xr < w; xr++)
            {
                if (IsVisited(visited, rowBase + xr) || !Match(row[xr], target, tolerance)) break;

                selection.Select(xr, y);
                MarkVisited(visited, rowBase + xr);

                if (above != null)
                {
                    bool m = Match(above[xr], target, tolerance) && !IsVisited(visited, aboveBase + xr);
                    if (m && !spanAbove) { stack.Push((xr, y - 1)); spanAbove = true; }
                    else if (!m) spanAbove = false;
                }
                if (below != null)
                {
                    bool m = Match(below[xr], target, tolerance) && !IsVisited(visited, belowBase + xr);
                    if (m && !spanBelow) { stack.Push((xr, y + 1)); spanBelow = true; }
                    else if (!m) spanBelow = false;
                }
            }
        }
    }

    /// <summary>Selects every pixel matching the seed color anywhere on the surface, ignoring contiguity.</summary>
    public static void SelectGlobal(Surface s, int seedX, int seedY, Selection selection, int tolerance = 0)
    {
        if ((uint)seedX >= (uint)s.Width || (uint)seedY >= (uint)s.Height) return;
        ColorBgra target = s[seedX, seedY];

        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (Match(s[x, y], target, tolerance))
                    selection.Select(x, y);
    }

    private static bool Match(ColorBgra a, ColorBgra b, int tol)
    {
        if (tol <= 0) return a.Bgra == b.Bgra;
        return Math.Abs(a.B - b.B) <= tol
            && Math.Abs(a.G - b.G) <= tol
            && Math.Abs(a.R - b.R) <= tol
            && Math.Abs(a.A - b.A) <= tol;
    }

    private static bool IsVisited(uint[] bits, int index) =>
        (bits[index >> 5] & (1u << (index & 31))) != 0;

    private static void MarkVisited(uint[] bits, int index) =>
        bits[index >> 5] |= 1u << (index & 31);
}
