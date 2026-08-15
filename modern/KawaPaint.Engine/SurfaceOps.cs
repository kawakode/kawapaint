// KawaPaint — misc whole-surface operations.

namespace KawaPaint.Engine;

public static class SurfaceOps
{
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
