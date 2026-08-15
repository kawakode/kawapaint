// KawaPaint — engine-side brush rasterization. Kept in the engine (not the UI) so tools
// stay portable. Colors are alpha-composited onto the Surface via ColorBgra.BlendOver.

namespace KawaPaint.Engine;

public static class BrushOps
{
    /// <summary>Stamps a filled, hard-edged disc of the given radius centered at (cx,cy).</summary>
    public static unsafe void FillDisc(Surface s, int cx, int cy, int radius, ColorBgra color)
    {
        if (radius <= 0)
        {
            if ((uint)cx < (uint)s.Width && (uint)cy < (uint)s.Height)
            {
                s[cx, cy] = ColorBgra.BlendOver(s[cx, cy], color);
            }
            return;
        }

        int r2 = radius * radius;
        int minY = Math.Max(0, cy - radius);
        int maxY = Math.Min(s.Height - 1, cy + radius);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            int span = (int)Math.Sqrt(r2 - (double)dy * dy);
            int minX = Math.Max(0, cx - span);
            int maxX = Math.Min(s.Width - 1, cx + span);

            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = minX; x <= maxX; x++)
            {
                row[x] = ColorBgra.BlendOver(row[x], color);
            }
        }
    }

    /// <summary>Draws a thick line by stamping discs along the segment from (x0,y0) to (x1,y1).</summary>
    public static void DrawLine(Surface s, double x0, double y0, double x1, double y1, int radius, ColorBgra color)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // Step finely enough that consecutive stamps overlap.
        double step = Math.Max(0.5, radius * 0.5);
        int steps = Math.Max(1, (int)(dist / step));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            int px = (int)Math.Round(x0 + dx * t);
            int py = (int)Math.Round(y0 + dy * t);
            FillDisc(s, px, py, radius, color);
        }
    }
}
