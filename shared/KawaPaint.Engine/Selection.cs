// KawaPaint - a pixel selection mask. When inactive (the default), the whole image is editable.
// Rectangle / ellipse / polygon (lasso) shapes rasterize into the mask, and Clip() restores pixels
// outside the selection so edits and effects stay inside it.
//
// The mask holds per-pixel COVERAGE, not a boolean: 0 = untouched, 255 = fully selected, and
// anything between is a partially covered edge pixel that Clip blends rather than restores
// outright. The rasterizers only produce intermediate values when asked to antialias
// (`antialias: true`, wired to the toolbar's existing AA checkbox); every other caller gets the
// same hard 0/255 mask it always did, and all the set operations below are bit-identical on
// binary input, so nothing changes for them.

using System.Runtime.InteropServices;

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
    private bool _boundsValid;
    private (int X, int Y, int W, int H) _bounds;

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
        _boundsValid = true;
        _bounds = (0, 0, Width, Height);
    }

    public void SelectAll()
    {
        Array.Fill(_mask, (byte)255);
        IsActive = true;
        _boundsValid = true;
        _bounds = (0, 0, Width, Height);
    }

    /// <summary>Inverts the current selection. If nothing is selected, selects everything.</summary>
    public void Invert()
    {
        if (!IsActive) { SelectAll(); return; }
        // Complement rather than a 0/255 flip, so a feathered edge inverts into the matching
        // feather instead of collapsing to a hard one. Identical to the old flip on binary input.
        for (int i = 0; i < _mask.Length; i++)
            _mask[i] = (byte)(255 - _mask[i]);
        _boundsValid = false;
    }

    /// <summary>
    /// Whether a pixel is editable at all. Any non-zero coverage counts, so a partially covered
    /// antialiased edge pixel reads as selected here - the *degree* only matters to
    /// <see cref="Clip"/>, which blends by it. Callers doing hit-testing (Magic Wand, flood fill)
    /// want this all-or-nothing reading.
    /// </summary>
    public bool IsSelected(int x, int y)
        => !IsActive || ((uint)x < (uint)Width && (uint)y < (uint)Height && _mask[y * Width + x] != 0);

    /// <summary>Per-pixel coverage, 0-255. Out of bounds reads as fully covered when inactive.</summary>
    public byte CoverageAt(int x, int y)
    {
        if (!IsActive) return 255;
        return (uint)x < (uint)Width && (uint)y < (uint)Height ? _mask[y * Width + x] : (byte)0;
    }

    /// <summary>Marks a single pixel selected. Used by algorithms that build a mask pixel-by-pixel
    /// (Magic Wand) rather than rasterizing a closed shape.</summary>
    public void Select(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _mask[y * Width + x] = 255;
        IsActive = true;
        _boundsValid = false;
    }

    /// <summary>Read-only view of the raw mask (255 = selected). Length = Width*Height.</summary>
    public ReadOnlySpan<byte> Mask => _mask;

    public Selection Clone()
    {
        var copy = new Selection(Width, Height)
        {
            IsActive = IsActive,
            _boundsValid = _boundsValid,
            _bounds = _bounds
        };
        _mask.CopyTo(copy._mask, 0);
        return copy;
    }

    public void CopyFrom(Selection other)
    {
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("Source selection must match this selection's dimensions.", nameof(other));
        other._mask.CopyTo(_mask, 0);
        IsActive = other.IsActive;
        _boundsValid = other._boundsValid;
        _bounds = other._bounds;
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

        bool baseWasActive = IsActive;

        switch (mode)
        {
            case SelectionCombineMode.Replace:
                CopyFrom(shape);
                return;
            // The three set operations are the standard fuzzy-set ones - max / min-with-complement /
            // min - chosen over the multiplicative alternatives because they are idempotent, so
            // subtracting the same shape twice cannot keep eroding a feathered edge. Each reduces
            // exactly to the old branch on binary input: with shape 0 or 255 every one of these
            // produces byte-for-byte what the `if` it replaced produced.
            case SelectionCombineMode.Add:
                // Add's base is deliberately read as a physically-empty mask even when inactive
                // (not "everything", despite IsSelected's convention) - union-with-everything would
                // just stay everything, which would make Add-mode useless for starting a fresh
                // selection from nothing. Producing exactly the shape is the useful behavior here.
                for (int i = 0; i < _mask.Length; i++)
                    if (shape._mask[i] > _mask[i]) _mask[i] = shape._mask[i];
                break;
            case SelectionCombineMode.Subtract:
                // Unlike Add, Subtract/Intersect must honor IsSelected's "inactive = everything
                // selected" reading, or they silently no-op against the mask's actual (zeroed)
                // bytes instead of subtracting from/intersecting with the whole canvas.
                if (!IsActive) SelectAll();
                for (int i = 0; i < _mask.Length; i++)
                {
                    int keep = 255 - shape._mask[i];
                    if (keep < _mask[i]) _mask[i] = (byte)keep;
                }
                break;
            case SelectionCombineMode.Intersect:
                if (!IsActive) SelectAll();   // see Subtract
                for (int i = 0; i < _mask.Length; i++)
                    if (shape._mask[i] < _mask[i]) _mask[i] = shape._mask[i];
                break;
        }

        bool any = false;
        foreach (byte b in _mask) { if (b != 0) { any = true; break; } }

        // An explicit subtraction/intersection is still an active selection when it produces an
        // empty mask: no pixels should be editable. SelectNone() is the distinct user action that
        // returns to the inactive "whole image editable" state. Add preserves an already-active
        // empty base for the same reason.
        IsActive = any || mode is SelectionCombineMode.Subtract or SelectionCombineMode.Intersect ||
            (mode == SelectionCombineMode.Add && baseWasActive);
        _boundsValid = false;
    }

    public void ReplaceWithRectangle(double x0, double y0, double x1, double y1, bool antialias = false)
    {
        if (antialias) { ReplaceWithRectangleAntialiased(x0, y0, x1, y1); return; }

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
        _boundsValid = true;
        _bounds = IsActive ? (left, top, right - left, bottom - top) : (0, 0, Width, Height);
    }

    /// <summary>
    /// Exact box coverage: a pixel's value is the area of its 1x1 cell that falls inside the
    /// (unrounded) rectangle. No sampling needed - a rectangle's overlap with an axis-aligned cell
    /// is separable, so it is the product of the horizontal and vertical overlaps.
    /// </summary>
    private void ReplaceWithRectangleAntialiased(double x0, double y0, double x1, double y1)
    {
        Array.Clear(_mask);
        double left = Math.Clamp(Math.Min(x0, x1), 0, Width);
        double right = Math.Clamp(Math.Max(x0, x1), 0, Width);
        double top = Math.Clamp(Math.Min(y0, y1), 0, Height);
        double bottom = Math.Clamp(Math.Max(y0, y1), 0, Height);
        if (right <= left || bottom <= top)
        {
            IsActive = false;
            _boundsValid = true;
            _bounds = (0, 0, Width, Height);
            return;
        }

        int firstX = (int)Math.Floor(left), lastX = (int)Math.Ceiling(right) - 1;
        int firstY = (int)Math.Floor(top), lastY = (int)Math.Ceiling(bottom) - 1;
        lastX = Math.Min(lastX, Width - 1);
        lastY = Math.Min(lastY, Height - 1);

        // Column coverages are the same for every row, so compute them once.
        var columns = new double[lastX - firstX + 1];
        for (int x = firstX; x <= lastX; x++)
            columns[x - firstX] = Overlap(left, right, x);

        bool any = false;
        for (int y = firstY; y <= lastY; y++)
        {
            double rowCoverage = Overlap(top, bottom, y);
            if (rowCoverage <= 0) continue;
            int rowBase = y * Width;
            for (int x = firstX; x <= lastX; x++)
            {
                byte value = ToCoverage(columns[x - firstX] * rowCoverage);
                if (value == 0) continue;
                _mask[rowBase + x] = value;
                any = true;
            }
        }

        IsActive = any;
        _boundsValid = false;
    }

    /// <summary>How much of the unit cell starting at <paramref name="cell"/> lies in [lo, hi].</summary>
    private static double Overlap(double lo, double hi, int cell)
        => Math.Clamp(Math.Min(hi, cell + 1) - Math.Max(lo, cell), 0, 1);

    private static byte ToCoverage(double value)
        => value <= 0 ? (byte)0 : value >= 1 ? (byte)255 : (byte)(value * 255 + 0.5);

    /// <summary>
    /// Sub-scanlines per pixel row used by the antialiased ellipse/polygon rasterizers. Coverage is
    /// exact horizontally (a scanline's span is a real interval, so its overlap with a pixel is
    /// arithmetic, not sampled); only the vertical direction is sampled, which is why 4 is enough -
    /// it bounds the error to an eighth of a pixel on the near-horizontal parts of a curve, and
    /// those are exactly where a purely horizontal AA scheme looks worst.
    /// </summary>
    private const int SubScanlines = 4;

    public void ReplaceWithEllipse(double x0, double y0, double x1, double y1, bool antialias = false)
    {
        if (antialias) { ReplaceWithEllipseAntialiased(x0, y0, x1, y1); return; }

        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        Array.Clear(_mask);
        if (rx < 0.5 || ry < 0.5)
        {
            IsActive = false;
            _boundsValid = true;
            _bounds = (0, 0, Width, Height);
            return;
        }

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
        _boundsValid = false;
    }

    private void ReplaceWithEllipseAntialiased(double x0, double y0, double x1, double y1)
    {
        Array.Clear(_mask);
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        if (rx < 0.5 || ry < 0.5)
        {
            IsActive = false;
            _boundsValid = true;
            _bounds = (0, 0, Width, Height);
            return;
        }

        int firstY = Math.Max(0, (int)Math.Floor(cy - ry));
        int lastY = Math.Min(Height - 1, (int)Math.Ceiling(cy + ry));
        var spanLeft = new double[SubScanlines];
        var spanRight = new double[SubScanlines];
        bool any = false;

        for (int y = firstY; y <= lastY; y++)
        {
            // Widest and narrowest span across this row's sub-scanlines. The narrowest bounds the
            // fully-covered core; the widest bounds the pixels that need coverage at all.
            double minLeft = double.MaxValue, maxLeft = double.MinValue;
            double minRight = double.MaxValue, maxRight = double.MinValue;
            bool anySpan = false;

            for (int s = 0; s < SubScanlines; s++)
            {
                double sampleY = y + (s + 0.5) / SubScanlines;
                double normalized = (sampleY - cy) / ry;
                double inside = 1 - normalized * normalized;
                if (inside <= 0) { spanLeft[s] = 0; spanRight[s] = -1; continue; }

                double half = rx * Math.Sqrt(inside);
                spanLeft[s] = cx - half;
                spanRight[s] = cx + half;
                anySpan = true;
                minLeft = Math.Min(minLeft, spanLeft[s]); maxLeft = Math.Max(maxLeft, spanLeft[s]);
                minRight = Math.Min(minRight, spanRight[s]); maxRight = Math.Max(maxRight, spanRight[s]);
            }
            if (!anySpan) continue;

            // A sub-scanline that missed this row contributes nothing, so it must not be allowed to
            // widen the core - treat the core as empty unless every sub-scanline produced a span.
            bool everySubScanlineHit = true;
            for (int s = 0; s < SubScanlines; s++)
                if (spanRight[s] < spanLeft[s]) { everySubScanlineHit = false; break; }

            int lo = Math.Max(0, (int)Math.Floor(minLeft));
            int hi = Math.Min(Width - 1, (int)Math.Ceiling(maxRight) - 1);
            if (hi < lo) continue;

            int coreStart = lo, coreEnd = lo - 1;
            if (everySubScanlineHit)
            {
                coreStart = Math.Max(lo, (int)Math.Ceiling(maxLeft));
                coreEnd = Math.Min(hi, (int)Math.Floor(minRight) - 1);
            }

            int rowBase = y * Width;
            for (int x = lo; x < coreStart; x++)
                any |= WriteCoverage(rowBase + x, spanLeft, spanRight, x);

            if (coreEnd >= coreStart)
            {
                Array.Fill(_mask, (byte)255, rowBase + coreStart, coreEnd - coreStart + 1);
                any = true;
            }

            for (int x = Math.Max(coreEnd + 1, coreStart); x <= hi; x++)
                any |= WriteCoverage(rowBase + x, spanLeft, spanRight, x);
        }

        IsActive = any;
        _boundsValid = false;
    }

    /// <summary>Averages one pixel's exact overlap with each sub-scanline span into the mask.</summary>
    private bool WriteCoverage(int index, double[] spanLeft, double[] spanRight, int x)
    {
        double sum = 0;
        for (int s = 0; s < spanLeft.Length; s++)
        {
            if (spanRight[s] < spanLeft[s]) continue;   // this sub-scanline missed the shape
            sum += Overlap(spanLeft[s], spanRight[s], x);
        }

        byte value = ToCoverage(sum / spanLeft.Length);
        if (value <= _mask[index]) return _mask[index] != 0;
        _mask[index] = value;
        return true;
    }

    public void ReplaceWithPolygon(IReadOnlyList<(double X, double Y)> points, bool antialias = false)
    {
        if (antialias) { ReplaceWithPolygonAntialiased(points); return; }

        Array.Clear(_mask);
        if (points.Count < 3)
        {
            IsActive = false;
            _boundsValid = true;
            _bounds = (0, 0, Width, Height);
            return;
        }

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
                int left = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
                int right = Math.Min(Width, (int)Math.Ceiling(xs[k + 1] - 0.5));
                for (int x = left; x < right; x++)
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
        // which Combine's own emptiness recompute never sees - Replace returns early via CopyFrom.
        IsActive = any;
        _boundsValid = false;
    }

    /// <summary>
    /// Even-odd scanline fill, but each pixel row is resolved from <see cref="SubScanlines"/>
    /// sub-scanlines and each span contributes its exact horizontal overlap rather than a
    /// centre-inside test. Coverage accumulates per row before being written, so two spans meeting
    /// inside one pixel (a thin waist, or a vertex) sum to the right value instead of one
    /// overwriting the other.
    /// </summary>
    private void ReplaceWithPolygonAntialiased(IReadOnlyList<(double X, double Y)> points)
    {
        Array.Clear(_mask);
        if (points.Count < 3)
        {
            IsActive = false;
            _boundsValid = true;
            _bounds = (0, 0, Width, Height);
            return;
        }

        double minYd = double.MaxValue, maxYd = double.MinValue;
        foreach (var p in points) { minYd = Math.Min(minYd, p.Y); maxYd = Math.Max(maxYd, p.Y); }
        int minY = Math.Max(0, (int)Math.Floor(minYd));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(maxYd));

        var rowCoverage = new double[Width];
        var xs = new List<double>();
        bool any = false;

        for (int y = minY; y <= maxY; y++)
        {
            int touchedLo = Width, touchedHi = -1;

            for (int s = 0; s < SubScanlines; s++)
            {
                double sampleY = y + (s + 0.5) / SubScanlines;
                xs.Clear();
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    double yi = points[i].Y, yj = points[j].Y;
                    if ((yi <= sampleY && yj > sampleY) || (yj <= sampleY && yi > sampleY))
                    {
                        double t = (sampleY - yi) / (yj - yi);
                        xs.Add(points[i].X + t * (points[j].X - points[i].X));
                    }
                }
                xs.Sort();

                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    double left = Math.Max(xs[k], 0), right = Math.Min(xs[k + 1], Width);
                    if (right <= left) continue;

                    int lo = Math.Max(0, (int)Math.Floor(left));
                    int hi = Math.Min(Width - 1, (int)Math.Ceiling(right) - 1);
                    for (int x = lo; x <= hi; x++) rowCoverage[x] += Overlap(left, right, x);
                    if (lo < touchedLo) touchedLo = lo;
                    if (hi > touchedHi) touchedHi = hi;
                }
            }

            int rowBase = y * Width;
            for (int x = touchedLo; x <= touchedHi; x++)
            {
                byte value = ToCoverage(rowCoverage[x] / SubScanlines);
                rowCoverage[x] = 0;             // reset as we go, so the next row starts clean
                if (value == 0) continue;
                _mask[rowBase + x] = value;
                any = true;
            }
        }

        IsActive = any;
        _boundsValid = false;
    }

    /// <summary>XOR-rasterizes a polygon into the current mask and returns its clipped bounds.
    /// Adding a freehand polygon vertex can therefore update only the fan triangle formed by the
    /// first, previous and new points instead of rasterizing the entire growing point list.
    ///
    /// Deliberately stays binary even when the selection is antialiased: the XOR fan trick relies on
    /// parity, which graded coverage has no equivalent of. The lasso uses this for its live preview
    /// during the drag and re-rasterizes the finished point list through
    /// ReplaceWithPolygon(antialias: true) on pointer-up.</summary>
    public (int X, int Y, int W, int H) TogglePolygon(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) return (0, 0, 0, 0);
        double minXd = double.MaxValue, minYd = double.MaxValue;
        double maxXd = double.MinValue, maxYd = double.MinValue;
        foreach (var point in points)
        {
            minXd = Math.Min(minXd, point.X); minYd = Math.Min(minYd, point.Y);
            maxXd = Math.Max(maxXd, point.X); maxYd = Math.Max(maxYd, point.Y);
        }
        int minY = Math.Max(0, (int)Math.Floor(minYd));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(maxYd));
        var intersections = new List<double>();
        bool changed = false;

        for (int y = minY; y <= maxY; y++)
        {
            intersections.Clear();
            double sampleY = y + 0.5;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                double yi = points[i].Y, yj = points[j].Y;
                if ((yi <= sampleY && yj > sampleY) || (yj <= sampleY && yi > sampleY))
                {
                    double amount = (sampleY - yi) / (yj - yi);
                    intersections.Add(points[i].X + amount * (points[j].X - points[i].X));
                }
            }
            intersections.Sort();
            for (int pair = 0; pair + 1 < intersections.Count; pair += 2)
            {
                int left = Math.Max(0, (int)Math.Ceiling(intersections[pair] - 0.5));
                int right = Math.Min(Width, (int)Math.Ceiling(intersections[pair + 1] - 0.5));
                int row = y * Width;
                for (int x = left; x < right; x++) _mask[row + x] ^= 255;
                changed |= right > left;
            }
        }

        if (changed) IsActive = true;
        _boundsValid = false;
        int minX = Math.Max(0, (int)Math.Floor(minXd));
        int maxX = Math.Min(Width - 1, (int)Math.Ceiling(maxXd));
        return maxX < minX || maxY < minY ? (0, 0, 0, 0)
            : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Repaints a mask rectangle from a baseline using one source-over per selected pixel.</summary>
    public unsafe void PaintMask(Surface target, Surface baseline, ColorBgra color,
        int x, int y, int width, int height)
    {
        if (target.Width != Width || target.Height != Height ||
            baseline.Width != Width || baseline.Height != Height)
            throw new ArgumentException("Surfaces must match this selection's dimensions.");
        int left = Math.Clamp(x, 0, Width), top = Math.Clamp(y, 0, Height);
        int right = (int)Math.Clamp((long)x + width, 0, Width);
        int bottom = (int)Math.Clamp((long)y + height, 0, Height);
        for (int row = top; row < bottom; row++)
        {
            ColorBgra* destination = (ColorBgra*)target.GetRowPointer(row);
            ColorBgra* source = (ColorBgra*)baseline.GetRowPointer(row);
            int offset = row * Width;
            for (int column = left; column < right; column++)
            {
                byte coverage = _mask[offset + column];
                if (coverage == 0) { destination[column] = source[column]; continue; }
                // Scale the paint's own alpha by coverage, so a feathered edge fades out rather
                // than stopping abruptly. Identical to a plain BlendOver at full coverage.
                ColorBgra paint = coverage == 255
                    ? color
                    : ColorBgra.FromBgra(color.B, color.G, color.R, (byte)(color.A * coverage / 255));
                destination[column] = ColorBgra.BlendOver(source[column], paint);
            }
        }
    }

    /// <summary>Bounding box of the selection (whole image if inactive).</summary>
    public (int X, int Y, int W, int H) GetBounds()
    {
        if (!IsActive) return (0, 0, Width, Height);
        if (_boundsValid) return _bounds;

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

        _bounds = maxX < 0
            ? (0, 0, 0, 0)           // explicitly empty active selection
            : (minX, minY, maxX - minX + 1, maxY - minY + 1);
        _boundsValid = true;
        return _bounds;
    }

    /// <summary>Restores pixels outside the selection in <paramref name="edited"/> from <paramref name="original"/>.</summary>
    public unsafe void Clip(Surface edited, Surface original)
    {
        if (!IsActive) return;
        if (edited.Width != Width || edited.Height != Height)
            throw new ArgumentException("Edited surface must match this selection's dimensions.", nameof(edited));
        if (original.Width != Width || original.Height != Height)
            throw new ArgumentException("Original surface must match this selection's dimensions.", nameof(original));

        var (boundsX, boundsY, boundsWidth, boundsHeight) = GetBounds();
        int boundsBottom = boundsY + boundsHeight;

        for (int y = 0; y < Height; y++)
        {
            ColorBgra* e = (ColorBgra*)edited.GetRowPointer(y);
            ColorBgra* o = (ColorBgra*)original.GetRowPointer(y);

            if (y < boundsY || y >= boundsBottom)
            {
                NativeMemory.Copy(o, e, checked((nuint)Width * ColorBgra.SizeOf));
                continue;
            }

            if (boundsX > 0)
                NativeMemory.Copy(o, e, checked((nuint)boundsX * ColorBgra.SizeOf));

            int suffixX = boundsX + boundsWidth;
            if (suffixX < Width)
                NativeMemory.Copy(o + suffixX, e + suffixX,
                    checked((nuint)(Width - suffixX) * ColorBgra.SizeOf));

            int rowBase = y * Width;
            for (int x = boundsX; x < suffixX; x++)
            {
                byte coverage = _mask[rowBase + x];
                if (coverage == 255) continue;                       // fully selected: keep the edit
                e[x] = coverage == 0
                    ? o[x]                                           // outside: restore
                    : ColorBgra.Lerp(o[x], e[x], coverage / 255.0);  // partial: blend the edge
            }
        }
    }

    /// <summary>Restores unselected pixels only inside a clipped dirty rectangle.</summary>
    public unsafe void Clip(Surface edited, Surface original, int x, int y, int width, int height)
    {
        if (!IsActive) return;
        if (edited.Width != Width || edited.Height != Height ||
            original.Width != Width || original.Height != Height)
            throw new ArgumentException("Surfaces must match this selection's dimensions.");

        int left = Math.Clamp(x, 0, Width);
        int top = Math.Clamp(y, 0, Height);
        int right = (int)Math.Clamp((long)x + width, 0, Width);
        int bottom = (int)Math.Clamp((long)y + height, 0, Height);
        for (int row = top; row < bottom; row++)
        {
            ColorBgra* destination = (ColorBgra*)edited.GetRowPointer(row);
            ColorBgra* source = (ColorBgra*)original.GetRowPointer(row);
            int maskOffset = row * Width;
            for (int column = left; column < right; column++)
            {
                byte coverage = _mask[maskOffset + column];
                if (coverage == 255) continue;                       // see the full-surface Clip
                destination[column] = coverage == 0
                    ? source[column]
                    : ColorBgra.Lerp(source[column], destination[column], coverage / 255.0);
            }
        }
    }
}
