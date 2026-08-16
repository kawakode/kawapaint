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
}
