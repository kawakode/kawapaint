// KawaPaint — scanline flood fill for the paint-bucket tool. Replaces the contiguous
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
        var visited = new bool[w * h];
        var stack = new Stack<(int x, int y)>();
        stack.Push((seedX, seedY));

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();

            // Walk left to the run start.
            int xl = x;
            while (xl >= 0 && !visited[y * w + xl] && Match(((ColorBgra*)s.GetRowPointer(y))[xl], target, tolerance))
                xl--;
            xl++;

            bool spanAbove = false, spanBelow = false;
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);

            for (int xr = xl; xr < w; xr++)
            {
                if (visited[y * w + xr] || !Match(row[xr], target, tolerance)) break;

                row[xr] = fill;
                visited[y * w + xr] = true;

                if (y > 0)
                {
                    bool m = Match(((ColorBgra*)s.GetRowPointer(y - 1))[xr], target, tolerance) && !visited[(y - 1) * w + xr];
                    if (m && !spanAbove) { stack.Push((xr, y - 1)); spanAbove = true; }
                    else if (!m) spanAbove = false;
                }
                if (y < h - 1)
                {
                    bool m = Match(((ColorBgra*)s.GetRowPointer(y + 1))[xr], target, tolerance) && !visited[(y + 1) * w + xr];
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

    private static bool Match(ColorBgra a, ColorBgra b, int tol)
    {
        if (tol <= 0) return a.Bgra == b.Bgra;
        return Math.Abs(a.B - b.B) <= tol
            && Math.Abs(a.G - b.G) <= tol
            && Math.Abs(a.R - b.R) <= tol
            && Math.Abs(a.A - b.A) <= tol;
    }
}
