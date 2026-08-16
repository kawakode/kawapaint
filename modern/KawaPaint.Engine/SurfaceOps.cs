// KawaPaint — misc whole-surface operations.

namespace KawaPaint.Engine;

public static class SurfaceOps
{
    public static unsafe void FlipHorizontal(Surface s)
    {
        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width / 2; x++)
                (row[x], row[s.Width - 1 - x]) = (row[s.Width - 1 - x], row[x]);
        }
    }

    public static unsafe void FlipVertical(Surface s)
    {
        for (int y = 0; y < s.Height / 2; y++)
        {
            ColorBgra* a = (ColorBgra*)s.GetRowPointer(y);
            ColorBgra* b = (ColorBgra*)s.GetRowPointer(s.Height - 1 - y);
            for (int x = 0; x < s.Width; x++)
                (a[x], b[x]) = (b[x], a[x]);
        }
    }

    /// <summary>Returns a new surface rotated 90 degrees (clockwise or counter-clockwise); dimensions swap.</summary>
    public static unsafe Surface Rotate90(Surface s, bool clockwise)
    {
        var dst = new Surface(s.Height, s.Width);
        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width; x++)
            {
                if (clockwise) dst[s.Height - 1 - y, x] = row[x];
                else dst[y, s.Width - 1 - x] = row[x];
            }
        }
        return dst;
    }

    /// <summary>Clears <paramref name="dst"/> and copies <paramref name="src"/> into it shifted by (dx,dy).</summary>
    public static unsafe void ShiftInto(Surface dst, Surface src, int dx, int dy)
    {
        dst.Clear(ColorBgra.Transparent);
        int w = src.Width, h = src.Height;

        for (int sy = 0; sy < h; sy++)
        {
            int ty = sy + dy;
            if ((uint)ty >= (uint)dst.Height) continue;

            ColorBgra* srcRow = (ColorBgra*)src.GetRowPointer(sy);
            ColorBgra* dstRow = (ColorBgra*)dst.GetRowPointer(ty);
            for (int sx = 0; sx < w; sx++)
            {
                int tx = sx + dx;
                if ((uint)tx < (uint)dst.Width)
                    dstRow[tx] = srcRow[sx];
            }
        }
    }
}
