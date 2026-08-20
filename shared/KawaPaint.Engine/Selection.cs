// KawaPaint — a pixel selection mask (255 = selected). When inactive (the default), the whole
// image is editable. Rectangle / ellipse / polygon (lasso) shapes rasterize into the mask, and
// Clip() restores pixels outside the selection so edits and effects stay inside it.

namespace KawaPaint.Engine;

/// <summary>How a newly drawn shape combines with whatever is already selected.</summary>
public enum SelectionCombineMode
{
    Replace,
    Add,
    Subtract,
    Intersect
}

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

    public void SelectAll()
    {
        Array.Fill(_mask, (byte)255);
        IsActive = true;
    }

    /// <summary>Inverts the current selection. If nothing is selected, selects everything.</summary>
    public void Invert()
    {
        if (!IsActive) { SelectAll(); return; }
        for (int i = 0; i < _mask.Length; i++)
            _mask[i] = _mask[i] == 0 ? (byte)255 : (byte)0;
    }

    public bool IsSelected(int x, int y)
        => !IsActive || ((uint)x < (uint)Width && (uint)y < (uint)Height && _mask[y * Width + x] != 0);

    /// <summary>Marks a single pixel selected. Used by algorithms that build a mask pixel-by-pixel
    /// (Magic Wand) rather than rasterizing a closed shape.</summary>
    public void Select(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _mask[y * Width + x] = 255;
        IsActive = true;
    }

    /// <summary>Read-only view of the raw mask (255 = selected). Length = Width*Height.</summary>
    public ReadOnlySpan<byte> Mask => _mask;

    public Selection Clone()
    {
        var copy = new Selection(Width, Height) { IsActive = IsActive };
        _mask.CopyTo(copy._mask, 0);
        return copy;
    }

    public void CopyFrom(Selection other)
    {
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("Source selection must match this selection's dimensions.", nameof(other));
        other._mask.CopyTo(_mask, 0);
        IsActive = other.IsActive;
    }

    /// <summary>
    /// Combines <paramref name="shape"/> into this selection per <paramref name="mode"/>. Used by
    /// the select tools to add/subtract/intersect a freshly drawn shape against whatever was
    /// already selected, rather than always replacing it outright.
    /// </summary>
    public void Combine(SelectionCombineMode mode, Selection shape)
    {
        if (shape.Width != Width || shape.Height != Height)
            throw new ArgumentException("Shape selection must match this selection's dimensions.", nameof(shape));

        switch (mode)
        {
            case SelectionCombineMode.Replace:
                CopyFrom(shape);
                return;
            case SelectionCombineMode.Add:
                // Add's base is deliberately read as a physically-empty mask even when inactive
                // (not "everything", despite IsSelected's convention) — union-with-everything would
                // just stay everything, which would make Add-mode useless for starting a fresh
                // selection from nothing. Producing exactly the shape is the useful behavior here.
                for (int i = 0; i < _mask.Length; i++)
                    if (shape._mask[i] != 0) _mask[i] = 255;
                break;
            case SelectionCombineMode.Subtract:
                // Unlike Add, Subtract/Intersect must honor IsSelected's "inactive = everything
                // selected" reading, or they silently no-op against the mask's actual (zeroed)
                // bytes instead of subtracting from/intersecting with the whole canvas.
                if (!IsActive) SelectAll();
                for (int i = 0; i < _mask.Length; i++)
                    if (shape._mask[i] != 0) _mask[i] = 0;
                break;
            case SelectionCombineMode.Intersect:
                if (!IsActive) SelectAll();   // see Subtract
                for (int i = 0; i < _mask.Length; i++)
                    if (shape._mask[i] == 0) _mask[i] = 0;
                break;
        }

        IsActive = false;
        foreach (byte b in _mask) { if (b != 0) { IsActive = true; break; } }
    }

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

        bool any = false;
        int top = Math.Max(0, (int)(cy - ry)), bottom = Math.Min(Height - 1, (int)(cy + ry));
        for (int y = top; y <= bottom; y++)
        {
            double dy = (y - cy) / ry;
            double inside = 1 - dy * dy;
            if (inside < 0) continue;
            double halfSpan = rx * Math.Sqrt(inside);
            int left = Math.Max(0, (int)(cx - halfSpan)), right = Math.Min(Width - 1, (int)(cx + halfSpan));
            for (int x = left; x <= right; x++)
            {
                _mask[y * Width + x] = 255;
                any = true;
            }
        }
        // See ReplaceWithPolygon for why this tracks what was actually written rather than assuming
        // a non-degenerate shape rasterizes to at least one pixel.
        IsActive = any;
    }

    public void ReplaceWithPolygon(IReadOnlyList<(double X, double Y)> points)
    {
        Array.Clear(_mask);
        if (points.Count < 3) { IsActive = false; return; }

        double minYd = double.MaxValue, maxYd = double.MinValue;
        foreach (var p in points) { minYd = Math.Min(minYd, p.Y); maxYd = Math.Max(maxYd, p.Y); }
        int minY = Math.Max(0, (int)Math.Floor(minYd));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(maxYd));

        bool any = false;
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
                {
                    _mask[y * Width + x] = 255;
                    any = true;
                }
            }
        }

        // Tracks what was actually written instead of asserting IsActive=true for any >=3-point
        // input, matching what ReplaceWithRectangle already does. A degenerate polygon (a sliver
        // whose per-row left>right after rounding, or one entirely off-canvas) rasterizes to
        // nothing, and "active over an all-zero mask" is the worst possible state to leave behind:
        // Clip() then restores every pixel of every subsequent edit, silently undoing each stroke
        // as it's drawn, while the marching-ants overlay has no boundary to draw and so shows the
        // user nothing to explain it. Reachable from a quick sub-pixel lasso flick in Replace mode,
        // which Combine's own emptiness recompute never sees — Replace returns early via CopyFrom.
        IsActive = any;
    }

    /// <summary>Bounding box of the selection (whole image if inactive).</summary>
    public (int X, int Y, int W, int H) GetBounds()
    {
        if (!IsActive) return (0, 0, Width, Height);

        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (_mask[y * Width + x] != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

        if (maxX < 0) return (0, 0, Width, Height);   // empty mask
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Restores pixels outside the selection in <paramref name="edited"/> from <paramref name="original"/>.</summary>
    public unsafe void Clip(Surface edited, Surface original)
    {
        if (!IsActive) return;
        if (edited.Width != Width || edited.Height != Height)
            throw new ArgumentException("Edited surface must match this selection's dimensions.", nameof(edited));
        if (original.Width != Width || original.Height != Height)
            throw new ArgumentException("Original surface must match this selection's dimensions.", nameof(original));

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
