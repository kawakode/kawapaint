// KawaPaint - misc whole-surface operations.

using System.Runtime.InteropServices;

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
        const int TileSize = 32;
        ColorBgra* source = (ColorBgra*)s.Scan0;
        ColorBgra* destination = (ColorBgra*)dst.Scan0;
        int sourceStride = s.Stride / ColorBgra.SizeOf;
        int destinationStride = dst.Stride / ColorBgra.SizeOf;

        // A tiled transpose keeps both the source reads and stride-jumping destination writes in
        // cache. The old indexer also performed two bounds checks for every pixel.
        for (int tileY = 0; tileY < s.Height; tileY += TileSize)
        for (int tileX = 0; tileX < s.Width; tileX += TileSize)
        {
            int endY = Math.Min(tileY + TileSize, s.Height);
            int endX = Math.Min(tileX + TileSize, s.Width);
            for (int x = tileX; x < endX; x++)
            for (int y = tileY; y < endY; y++)
            {
                ColorBgra value = source[y * sourceStride + x];
                if (clockwise)
                    destination[x * destinationStride + s.Height - 1 - y] = value;
                else
                    destination[(s.Width - 1 - x) * destinationStride + y] = value;
            }
        }
        return dst;
    }

    /// <summary>Clears <paramref name="dst"/> and copies <paramref name="src"/> into it shifted by (dx,dy).</summary>
    public static unsafe void ShiftInto(Surface dst, Surface src, int dx, int dy)
    {
        dst.Clear(ColorBgra.Transparent);
        int srcX = Math.Max(0, -dx);
        int srcY = Math.Max(0, -dy);
        int dstX = Math.Max(0, dx);
        int dstY = Math.Max(0, dy);
        int copyWidth = Math.Min(src.Width - srcX, dst.Width - dstX);
        int copyHeight = Math.Min(src.Height - srcY, dst.Height - dstY);

        if (copyWidth <= 0 || copyHeight <= 0) return;

        nuint rowBytes = checked((nuint)copyWidth * ColorBgra.SizeOf);
        for (int row = 0; row < copyHeight; row++)
        {
            void* source = src.GetRowPointer(srcY + row) + (long)srcX * ColorBgra.SizeOf;
            void* destination = dst.GetRowPointer(dstY + row) + (long)dstX * ColorBgra.SizeOf;
            NativeMemory.Copy(source, destination, rowBytes);
        }
    }

    /// <summary>
    /// Alpha-composites <paramref name="src"/> onto <paramref name="dst"/> at (dx,dy), clipped to
    /// dst's bounds. Unlike <see cref="ShiftInto"/> this blends rather than overwrites, so a
    /// pasted image's transparent pixels don't blank out what's underneath.
    /// </summary>
    public static unsafe void CompositeOver(Surface dst, Surface src, int dx, int dy)
    {
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
                    dstRow[tx] = Blending.Composite(BlendMode.Normal, dstRow[tx], srcRow[sx], 255);
            }
        }
    }

    /// <summary>
    /// Writes into <paramref name="dst"/> the result of dragging the pixels <paramref name="selection"/>
    /// covers in <paramref name="original"/> by (dx,dy): the area they leave becomes transparent and
    /// they land, by their coverage, on top of whatever was there. <paramref name="selection"/> is
    /// the selection where the drag began, not the moved one.
    /// </summary>
    public static unsafe void MoveSelectedPixels(Surface dst, Surface original, Selection selection, int dx, int dy)
    {
        int w = original.Width, h = original.Height;
        if (dst.Width != w || dst.Height != h || selection.Width != w || selection.Height != h)
            throw new ArgumentException("Surfaces and selection must have the same size.");

        for (int y = 0; y < h; y++)
        {
            ColorBgra* src = (ColorBgra*)original.GetRowPointer(y);
            ColorBgra* outRow = (ColorBgra*)dst.GetRowPointer(y);
            int sy = y - dy;
            for (int x = 0; x < w; x++)
            {
                // What stays behind: the original, emptied where the selection lifted pixels out.
                ColorBgra under = src[x];
                byte lifted = selection.CoverageAt(x, y);
                if (lifted != 0)
                    under.A = (byte)((under.A * (255 - lifted) + 127) / 255);

                // What lands here: the selected pixel that was (dx,dy) away, by its coverage.
                int sx = x - dx;
                byte landed = (uint)sx < (uint)w && (uint)sy < (uint)h ? selection.CoverageAt(sx, sy) : (byte)0;
                outRow[x] = landed == 0
                    ? under
                    : Blending.Composite(BlendMode.Normal, under, *(ColorBgra*)original.GetPointPointer(sx, sy), landed);
            }
        }
    }
}
