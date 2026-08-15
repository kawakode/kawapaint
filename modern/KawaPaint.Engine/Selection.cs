// KawaPaint — a pixel selection mask (255 = selected). When inactive (the default), the whole
// image is editable. Rectangle / ellipse / polygon (lasso) shapes rasterize into the mask, and
// Clip() restores pixels outside the selection so edits and effects stay inside it.

namespace KawaPaint.Engine;

public sealed class Selection
{
    private readonly byte[] _mask;

    public int Width { get; }
    public int Height { get; }

    /// <summary>False = no active selection (whole image editable).</summary>
    public bool IsActive { get; private set; }

    public Selection(int width, int height)
    {
        Width = width;
        Height = height;
        _mask = new byte[width * height];
    }

    public void SelectNone()
    {
        if (IsActive) Array.Clear(_mask);
        IsActive = false;
    }

    public bool IsSelected(int x, int y)
        => !IsActive || ((uint)x < (uint)Width && (uint)y < (uint)Height && _mask[y * Width + x] != 0);

    /// <summary>Read-only view of the raw mask (255 = selected). Length = Width*Height.</summary>
    public ReadOnlySpan<byte> Mask => _mask;

    public void ReplaceWithRectangle(double x0, double y0, double x1, double y1)
    {
        int left = (int)Math.Round(Math.Min(x0, x1));
        int top = (int)Math.Round(Math.Min(y0, y1));
        int right = (int)Math.Round(Math.Max(x0, x1));
        int bottom = (int)Math.Round(Math.Max(y0, y1));
        left = Math.Clamp(left, 0, Width); top = Math.Clamp(top, 0, Height);
        right = Math.Clamp(right, 0, Width); bottom = Math.Clamp(bottom, 0, Height);

        Array.Clear(_mask);
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                _mask[y * Width + x] = 255;
        IsActive = right > left && bottom > top;
    }

    public void ReplaceWithEllipse(double x0, double y0, double x1, double y1)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        Array.Clear(_mask);
        if (rx < 0.5 || ry < 0.5) { IsActive = false; return; }

        int top = Math.Max(0, (int)(cy - ry)), bottom = Math.Min(Height - 1, (int)(cy + ry));
        for (int y = top; y <= bottom; y++)
        {
            double dy = (y - cy) / ry;
            double inside = 1 - dy * dy;
            if (inside < 0) continue;
            double halfSpan = rx * Math.Sqrt(inside);
            int left = Math.Max(0, (int)(cx - halfSpan)), right = Math.Min(Width - 1, (int)(cx + halfSpan));
            for (int x = left; x <= right; x++)
                _mask[y * Width + x] = 255;
        }
        IsActive = true;
    }

    public void ReplaceWithPolygon(IReadOnlyList<(double X, double Y)> points)
    {
        Array.Clear(_mask);
        if (points.Count < 3) { IsActive = false; return; }

        double minYd = double.MaxValue, maxYd = double.MinValue;
        foreach (var p in points) { minYd = Math.Min(minYd, p.Y); maxYd = Math.Max(maxYd, p.Y); }
        int minY = Math.Max(0, (int)Math.Floor(minYd));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(maxYd));

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
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int left = Math.Max(0, (int)Math.Round(xs[k]));
                int right = Math.Min(Width - 1, (int)Math.Round(xs[k + 1]));
                for (int x = left; x <= right; x++)
                    _mask[y * Width + x] = 255;
            }
        }
        IsActive = true;
    }

    /// <summary>Restores pixels outside the selection in <paramref name="edited"/> from <paramref name="original"/>.</summary>
    public unsafe void Clip(Surface edited, Surface original)
    {
        if (!IsActive) return;
        for (int y = 0; y < Height; y++)
        {
            ColorBgra* e = (ColorBgra*)edited.GetRowPointer(y);
            ColorBgra* o = (ColorBgra*)original.GetRowPointer(y);
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
                if (_mask[rowBase + x] == 0)
                    e[x] = o[x];
        }
    }
}
