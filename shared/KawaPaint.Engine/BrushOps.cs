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

/// <summary>
/// One paintbrush stroke's accumulated coverage, one byte per canvas pixel.
///
/// The pencil stamps each dab straight onto the layer, which is fine for a hard edge but wrong for
/// a soft one: consecutive dabs overlap heavily, so their semi-transparent edges would blend over
/// each other and the stroke would darken wherever the pointer moved slowly. Instead every dab is
/// max-combined into this mask, and <see cref="Flush"/> re-composites the affected region from the
/// pre-stroke snapshot — so the stroke never exceeds the brush color's own alpha no matter how
/// many dabs land on a pixel, and re-running a region is idempotent.
/// </summary>
public sealed class SoftBrushStroke
{
    private readonly byte[] _mask;
    private readonly int _width, _height;

    // Region touched since the last Flush. Empty when _maxX < _minX.
    private int _minX, _minY, _maxX, _maxY;

    public SoftBrushStroke(int width, int height)
    {
        _width = width;
        _height = height;
        // A Surface of these dimensions already exists whenever a stroke starts, and that costs
        // four bytes per pixel to this mask's one, so the multiplication cannot overflow here.
        _mask = new byte[width * height];
        ResetDirty();
    }

    private void ResetDirty()
    {
        _minX = _width; _minY = _height; _maxX = -1; _maxY = -1;
    }

    /// <summary>
    /// Adds one round dab. <paramref name="hardness"/> runs 0 (coverage falls off across the whole
    /// radius) to 1 (solid to the rim, with a single antialiased pixel of edge — the same edge the
    /// hard brush's antialiased path draws, so the two tools agree at hardness 1).
    /// </summary>
    public void Dab(double cx, double cy, double radius, double hardness)
    {
        if (_mask.Length == 0) return;

        radius = Math.Max(0.5, radius);
        hardness = Math.Clamp(hardness, 0.0, 1.0);

        double outer = radius + 0.5;
        double falloff = Math.Max(0.5, radius * (1.0 - hardness));

        int x0 = Math.Max(0, (int)Math.Floor(cx - outer));
        int x1 = Math.Min(_width - 1, (int)Math.Ceiling(cx + outer));
        int y0 = Math.Max(0, (int)Math.Floor(cy - outer));
        int y1 = Math.Min(_height - 1, (int)Math.Ceiling(cy + outer));
        if (x0 > x1 || y0 > y1) return;

        for (int y = y0; y <= y1; y++)
        {
            double dy = y - cy;
            int row = y * _width;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx;
                double t = (outer - Math.Sqrt(dx * dx + dy * dy)) / falloff;
                if (t <= 0) continue;

                if (t >= 1) t = 1;
                else t = t * t * (3 - 2 * t);   // smoothstep, so the soft edge has no visible banding

                byte coverage = (byte)(t * 255 + 0.5);
                if (coverage <= _mask[row + x]) continue;

                _mask[row + x] = coverage;
                if (x < _minX) _minX = x;
                if (x > _maxX) _maxX = x;
                if (y < _minY) _minY = y;
                if (y > _maxY) _maxY = y;
            }
        }
    }

    /// <summary>Dabs along the segment from (x0,y0) to (x1,y1). Spacing is a quarter of the radius
    /// rather than the hard brush's half: a soft dab contributes much less at its rim, so sparser
    /// spacing shows as scalloping along the stroke.</summary>
    public void DabLine(double x0, double y0, double x1, double y1, double radius, double hardness)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(0.5, radius * 0.25);
        int steps = Math.Max(1, (int)(dist / step));

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            Dab(x0 + dx * t, y0 + dy * t, radius, hardness);
        }
    }

    /// <summary>Re-composites everything dabbed since the last call: for each pixel in the dirty
    /// region, <paramref name="color"/> at that pixel's coverage over <paramref name="preStroke"/>
    /// (the layer as it was at pointer-down), written into <paramref name="target"/>. Clears the
    /// dirty region afterwards.</summary>
    public unsafe void Flush(Surface target, Surface preStroke, ColorBgra color)
    {
        if (_maxX < _minX || _maxY < _minY) return;
        if (target.Width != _width || target.Height != _height ||
            preStroke.Width != _width || preStroke.Height != _height)
        {
            ResetDirty();
            return;
        }

        int minX = _minX, maxX = _maxX;
        for (int y = _minY; y <= _maxY; y++)
        {
            ColorBgra* dst = (ColorBgra*)target.GetRowPointer(y);
            ColorBgra* src = (ColorBgra*)preStroke.GetRowPointer(y);
            int row = y * _width;

            for (int x = minX; x <= maxX; x++)
            {
                ColorBgra baseline = src[x];
                int coverage = _mask[row + x];
                if (coverage == 0) { dst[x] = baseline; continue; }

                int alpha = color.A * coverage / 255;
                dst[x] = alpha == 0
                    ? baseline
                    : ColorBgra.BlendOver(baseline, ColorBgra.FromBgra(color.B, color.G, color.R, (byte)alpha));
            }
        }

        ResetDirty();
    }
}
