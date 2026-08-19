// KawaPaint — engine-side brush rasterization. Kept in the engine (not the UI) so tools
// stay portable. Blend mode alpha-composites; Set mode overwrites (used by the eraser).

namespace KawaPaint.Engine;

public enum StampMode
{
    /// <summary>Alpha-composite the color onto existing pixels.</summary>
    Blend,
    /// <summary>Overwrite pixels outright (eraser uses this with a transparent color).</summary>
    Set
}

public static class BrushOps
{
    /// <summary>Stamps a filled disc of the given radius centered at (cx,cy). Antialias softens the edge.</summary>
    public static unsafe void FillDisc(Surface s, int cx, int cy, int radius, ColorBgra color,
                                       StampMode mode = StampMode.Blend, bool antialias = false)
    {
        if (radius <= 0)
        {
            if ((uint)cx < (uint)s.Width && (uint)cy < (uint)s.Height)
                Put((ColorBgra*)s.GetRowPointer(cy) + cx, color, mode);
            return;
        }

        if (antialias && mode == StampMode.Blend)
        {
            FillDiscAA(s, cx, cy, radius, color);
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
                Put(row + x, color, mode);
        }
    }

    private static unsafe void FillDiscAA(Surface s, int cx, int cy, int radius, ColorBgra color)
    {
        int minY = Math.Max(0, cy - radius - 1);
        int maxY = Math.Min(s.Height - 1, cy + radius + 1);
        int minX = Math.Max(0, cx - radius - 1);
        int maxX = Math.Min(s.Width - 1, cx + radius + 1);

        for (int y = minY; y <= maxY; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            double dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - cx;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double coverage = radius + 0.5 - dist;   // 1 inside, fades to 0 across the edge
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                ColorBgra c = coverage >= 1
                    ? color
                    : ColorBgra.FromBgra(color.B, color.G, color.R, (byte)(color.A * coverage));
                row[x] = ColorBgra.BlendOver(row[x], c);
            }
        }
    }

    /// <summary>Draws a thick line by stamping discs along the segment from (x0,y0) to (x1,y1).</summary>
    public static void DrawLine(Surface s, double x0, double y0, double x1, double y1, int radius,
                                ColorBgra color, StampMode mode = StampMode.Blend, bool antialias = false)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(0.5, radius * 0.5);
        int steps = Math.Max(1, (int)(dist / step));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            FillDisc(s, (int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), radius, color, mode, antialias);
        }
    }

    private static unsafe void Put(ColorBgra* p, ColorBgra color, StampMode mode)
    {
        *p = mode == StampMode.Set ? color : ColorBgra.BlendOver(*p, color);
    }

    /// <summary>Copies a disc-shaped patch from src (read at (x+offsetX, y+offsetY) for each
    /// destination pixel (x,y)) onto dst, centered at (cx,cy). The clone stamp tool's primitive:
    /// unlike FillDisc this paints varying per-pixel color, not one flat color.</summary>
    public static unsafe void CloneDisc(Surface dst, Surface src, int cx, int cy, int offsetX, int offsetY,
                                        int radius, bool antialias = false)
    {
        if (radius <= 0)
        {
            if ((uint)cx < (uint)dst.Width && (uint)cy < (uint)dst.Height)
            {
                int sx0 = cx + offsetX, sy0 = cy + offsetY;
                if ((uint)sx0 < (uint)src.Width && (uint)sy0 < (uint)src.Height)
                    *dst.GetPointPointer(cx, cy) = ColorBgra.BlendOver(*dst.GetPointPointer(cx, cy), src[sx0, sy0]);
            }
            return;
        }

        int pad = antialias ? 1 : 0;
        int minY = Math.Max(0, cy - radius - pad), maxY = Math.Min(dst.Height - 1, cy + radius + pad);
        int minX = Math.Max(0, cx - radius - pad), maxX = Math.Min(dst.Width - 1, cx + radius + pad);

        for (int y = minY; y <= maxY; y++)
        {
            ColorBgra* row = (ColorBgra*)dst.GetRowPointer(y);
            double dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - cx;
                double coverage = antialias ? radius + 0.5 - Math.Sqrt(dx * dx + dy * dy) : (dx * dx + dy * dy <= (double)radius * radius ? 1.0 : -1.0);
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                int sx = x + offsetX, sy = y + offsetY;
                if ((uint)sx >= (uint)src.Width || (uint)sy >= (uint)src.Height) continue;
                ColorBgra sample = src[sx, sy];
                ColorBgra c = coverage >= 1 ? sample : ColorBgra.FromBgra(sample.B, sample.G, sample.R, (byte)(sample.A * coverage));
                row[x] = ColorBgra.BlendOver(row[x], c);
            }
        }
    }

    /// <summary>Clone-stamps discs along the segment from (x0,y0) to (x1,y1), same offset throughout.</summary>
    public static void CloneLine(Surface dst, Surface src, double x0, double y0, double x1, double y1,
                                 int offsetX, int offsetY, int radius, bool antialias = false)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(0.5, radius * 0.5);
        int steps = Math.Max(1, (int)(dist / step));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            CloneDisc(dst, src, (int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), offsetX, offsetY, radius, antialias);
        }
    }

    /// <summary>
    /// Recolor tool's primitive: within a disc, replaces pixels close to <paramref name="from"/>
    /// (within a per-channel tolerance, same metric FloodFill uses) with <paramref name="to"/> —
    /// but rather than flattening to a flat color, adds the from→to offset onto each pixel's
    /// actual value, so shading/antialiasing at color boundaries carries through unchanged.
    /// </summary>
    public static unsafe void RecolorDisc(Surface s, int cx, int cy, int radius, ColorBgra from, ColorBgra to,
                                          int tolerance, bool antialias = false)
    {
        int dB = to.B - from.B, dG = to.G - from.G, dR = to.R - from.R, dA = to.A - from.A;
        if (dB == 0 && dG == 0 && dR == 0 && dA == 0) return;

        int pad = antialias ? 1 : 0;
        int minY = Math.Max(0, cy - radius - pad), maxY = Math.Min(s.Height - 1, cy + radius + pad);
        int minX = Math.Max(0, cx - radius - pad), maxX = Math.Min(s.Width - 1, cx + radius + pad);

        for (int y = minY; y <= maxY; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            double dy = y - cy;
            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - cx;
                double coverage = antialias ? radius + 0.5 - Math.Sqrt(dx * dx + dy * dy) : (dx * dx + dy * dy <= (double)radius * radius ? 1.0 : -1.0);
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                ColorBgra c = row[x];
                if (!WithinTolerance(c, from, tolerance)) continue;

                ColorBgra adjusted = ColorBgra.FromBgra(Clamp.B(c.B + dB), Clamp.B(c.G + dG), Clamp.B(c.R + dR), Clamp.B(c.A + dA));
                row[x] = coverage >= 1 ? adjusted : ColorBgra.Lerp(c, adjusted, coverage);
            }
        }
    }

    /// <summary>Recolors along the segment from (x0,y0) to (x1,y1) — see RecolorDisc.</summary>
    public static void RecolorLine(Surface s, double x0, double y0, double x1, double y1, int radius,
                                   ColorBgra from, ColorBgra to, int tolerance, bool antialias = false)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(0.5, radius * 0.5);
        int steps = Math.Max(1, (int)(dist / step));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            RecolorDisc(s, (int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), radius, from, to, tolerance, antialias);
        }
    }

    private static bool WithinTolerance(ColorBgra a, ColorBgra b, int tol)
    {
        if (tol <= 0) return a.Bgra == b.Bgra;
        return Math.Abs(a.B - b.B) <= tol && Math.Abs(a.G - b.G) <= tol
            && Math.Abs(a.R - b.R) <= tol && Math.Abs(a.A - b.A) <= tol;
    }
}
