// KawaPaint — engine-side shape rasterization (outlines) built on BrushOps disc stamping.
// Endpoints are given as the drag start/end; corners are normalized internally.

namespace KawaPaint.Engine;

public static class ShapeOps
{
    public static void DrawRectangle(Surface s, double x0, double y0, double x1, double y1, int radius, ColorBgra color, bool antialias = false)
    {
        double left = Math.Min(x0, x1), right = Math.Max(x0, x1);
        double top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);

        BrushOps.DrawLine(s, left, top, right, top, radius, color, StampMode.Blend, antialias);
        BrushOps.DrawLine(s, right, top, right, bottom, radius, color, StampMode.Blend, antialias);
        BrushOps.DrawLine(s, right, bottom, left, bottom, radius, color, StampMode.Blend, antialias);
        BrushOps.DrawLine(s, left, bottom, left, top, radius, color, StampMode.Blend, antialias);
    }

    public static unsafe void FillRectangle(Surface s, double x0, double y0, double x1, double y1, ColorBgra color)
    {
        int left = Math.Max(0, (int)Math.Round(Math.Min(x0, x1)));
        int top = Math.Max(0, (int)Math.Round(Math.Min(y0, y1)));
        int right = Math.Min(s.Width - 1, (int)Math.Round(Math.Max(x0, x1)));
        int bottom = Math.Min(s.Height - 1, (int)Math.Round(Math.Max(y0, y1)));

        for (int y = top; y <= bottom; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = left; x <= right; x++)
                row[x] = ColorBgra.BlendOver(row[x], color);
        }
    }

    public static unsafe void FillEllipse(Surface s, double x0, double y0, double x1, double y1, ColorBgra color)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        if (rx < 0.5 || ry < 0.5) return;

        int top = Math.Max(0, (int)(cy - ry)), bottom = Math.Min(s.Height - 1, (int)(cy + ry));
        for (int y = top; y <= bottom; y++)
        {
            double dy = (y - cy) / ry;
            double inside = 1 - dy * dy;
            if (inside < 0) continue;
            double half = rx * Math.Sqrt(inside);
            int left = Math.Max(0, (int)(cx - half)), right = Math.Min(s.Width - 1, (int)(cx + half));
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = left; x <= right; x++)
                row[x] = ColorBgra.BlendOver(row[x], color);
        }
    }

    public static void DrawEllipse(Surface s, double x0, double y0, double x1, double y1, int radius, ColorBgra color, bool antialias = false)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        if (rx < 0.5 || ry < 0.5)
        {
            BrushOps.FillDisc(s, (int)Math.Round(cx), (int)Math.Round(cy), radius, color, StampMode.Blend, antialias);
            return;
        }

        // Step count scaled to the perimeter so stamps overlap.
        double perimeter = Math.PI * (3 * (rx + ry) - Math.Sqrt((3 * rx + ry) * (rx + 3 * ry)));
        int steps = Math.Max(24, (int)(perimeter / Math.Max(1, radius * 0.6)));

        double px = cx + rx, py = cy;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps * 2 * Math.PI;
            double nx = cx + rx * Math.Cos(t);
            double ny = cy + ry * Math.Sin(t);
            BrushOps.DrawLine(s, px, py, nx, ny, radius, color, StampMode.Blend, antialias);
            px = nx; py = ny;
        }
    }

    /// <summary>Corner radius clamped to at most half the shorter side — beyond that the shape is
    /// already a capsule/circle, same "no special-casing needed" trick as paint.net's original.</summary>
    private static double ClampCornerRadius(double cornerRadius, double w, double h)
        => Math.Max(0, Math.Min(cornerRadius, Math.Min(w, h) / 2));

    /// <summary>True if (x,y) lies inside a rounded rectangle: clamp the point into the "core"
    /// rect inset by the corner radius, then test distance from that clamped point — handles the
    /// straight edges (clamp is a no-op, distance 0) and the four rounded corners in one test.</summary>
    private static bool InsideRoundedRect(double x, double y, double left, double top, double right, double bottom, double r)
    {
        if (x < left || x > right || y < top || y > bottom) return false;
        double cx = Math.Clamp(x, left + r, right - r);
        double cy = Math.Clamp(y, top + r, bottom - r);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>Signed distance from (x,y) to a rounded rectangle's boundary (negative = inside),
    /// the standard "rounded box" SDF — lets DrawRoundedRectangle stroke a ring with the same
    /// antialiased-edge coverage math BrushOps uses for a round brush.</summary>
    private static double RoundedRectDistance(double x, double y, double left, double top, double right, double bottom, double r)
    {
        double cx = (left + right) / 2, cy = (top + bottom) / 2;
        double hx = (right - left) / 2 - r, hy = (bottom - top) / 2 - r;
        double qx = Math.Abs(x - cx) - hx, qy = Math.Abs(y - cy) - hy;
        double outsideDist = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        double insideDist = Math.Min(Math.Max(qx, qy), 0);
        return outsideDist + insideDist - r;
    }

    public static unsafe void FillRoundedRectangle(Surface s, double x0, double y0, double x1, double y1, double cornerRadius, ColorBgra color)
    {
        double left = Math.Min(x0, x1), right = Math.Max(x0, x1);
        double top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
        double r = ClampCornerRadius(cornerRadius, right - left, bottom - top);

        int iTop = Math.Max(0, (int)Math.Floor(top)), iBottom = Math.Min(s.Height - 1, (int)Math.Ceiling(bottom));
        int iLeft = Math.Max(0, (int)Math.Floor(left)), iRight = Math.Min(s.Width - 1, (int)Math.Ceiling(right));

        for (int y = iTop; y <= iBottom; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = iLeft; x <= iRight; x++)
                if (InsideRoundedRect(x + 0.5, y + 0.5, left, top, right, bottom, r))
                    row[x] = ColorBgra.BlendOver(row[x], color);
        }
    }

    /// <summary>radius here is a stroke *radius* (half-width), matching DrawRectangle/DrawEllipse's
    /// convention — callers already pass BrushWidth/2, so it isn't halved again internally.</summary>
    public static unsafe void DrawRoundedRectangle(Surface s, double x0, double y0, double x1, double y1, double cornerRadius,
                                                    int radius, ColorBgra color, bool antialias = false)
    {
        double left = Math.Min(x0, x1), right = Math.Max(x0, x1);
        double top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
        double r = ClampCornerRadius(cornerRadius, right - left, bottom - top);
        double half = Math.Max(0.5, (double)radius);

        int iTop = Math.Max(0, (int)Math.Floor(top - half - 1)), iBottom = Math.Min(s.Height - 1, (int)Math.Ceiling(bottom + half + 1));
        int iLeft = Math.Max(0, (int)Math.Floor(left - half - 1)), iRight = Math.Min(s.Width - 1, (int)Math.Ceiling(right + half + 1));

        for (int y = iTop; y <= iBottom; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = iLeft; x <= iRight; x++)
            {
                double dist = Math.Abs(RoundedRectDistance(x + 0.5, y + 0.5, left, top, right, bottom, r));
                double coverage = antialias ? half + 0.5 - dist : (dist <= half ? 1.0 : -1.0);
                if (coverage <= 0) continue;
                if (coverage > 1) coverage = 1;

                ColorBgra c = coverage >= 1 ? color : ColorBgra.FromBgra(color.B, color.G, color.R, (byte)(color.A * coverage));
                row[x] = ColorBgra.BlendOver(row[x], c);
            }
        }
    }

    /// <summary>Even-odd scanline fill of an arbitrary closed polygon (implicitly closed back to
    /// the first point) — same algorithm as Selection.ReplaceWithPolygon, filling a Surface
    /// instead of a mask.</summary>
    public static unsafe void FillPolygon(Surface s, IReadOnlyList<(double X, double Y)> points, ColorBgra color)
    {
        if (points.Count < 3) return;

        double minYd = double.MaxValue, maxYd = double.MinValue;
        foreach (var p in points) { minYd = Math.Min(minYd, p.Y); maxYd = Math.Max(maxYd, p.Y); }
        int minY = Math.Max(0, (int)Math.Floor(minYd));
        int maxY = Math.Min(s.Height - 1, (int)Math.Ceiling(maxYd));

        var xs = new List<double>();
        for (int y = minY; y <= maxY; y++)
        {
            xs.Clear();
            double sy = y + 0.5;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                double yi = points[i].Y, yj = points[j].Y;
                if ((yi <= sy && yj > sy) || (yj <= sy && yi > sy))
                {
                    double t = (sy - yi) / (yj - yi);
                    xs.Add(points[i].X + t * (points[j].X - points[i].X));
                }
            }
            xs.Sort();

            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int left = Math.Max(0, (int)Math.Round(xs[k]));
                int right = Math.Min(s.Width - 1, (int)Math.Round(xs[k + 1]));
                for (int x = left; x <= right; x++)
                    row[x] = ColorBgra.BlendOver(row[x], color);
            }
        }
    }

    /// <summary>Strokes a closed polygon's outline (implicitly closed back to the first point).</summary>
    public static void DrawPolygon(Surface s, IReadOnlyList<(double X, double Y)> points, int radius, ColorBgra color, bool antialias = false)
    {
        if (points.Count < 2) return;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            BrushOps.DrawLine(s, a.X, a.Y, b.X, b.Y, radius, color, StampMode.Blend, antialias);
        }
    }
}
