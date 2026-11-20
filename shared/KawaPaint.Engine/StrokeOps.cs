// KawaPaint - Stroke: draws a contour of a chosen width around a shape (the selection, or the
// opaque pixels of a layer), in one colour or a two-colour gradient, inside, centred on or outside
// the shape's edge.
//
// The shape is turned once into a signed distance field (two exact Euclidean distance transforms,
// one to the inside and one to the outside), so the band is round-cornered, antialiased and the
// same width all the way round, and changing the width, position or colours only re-reads it.

using System;

namespace KawaPaint.Engine;

/// <summary>Where the contour sits relative to the shape's edge.</summary>
public enum StrokePosition { Inside, Center, Outside }

/// <summary>How the contour is coloured: <see cref="Solid"/> uses the first colour only; the
/// others run a gradient from the first colour to the second.</summary>
public enum StrokeFill
{
    Solid,
    /// <summary>Left to right across the contour.</summary>
    Horizontal,
    /// <summary>Top to bottom.</summary>
    Vertical,
    /// <summary>Top-left to bottom-right.</summary>
    Diagonal,
    /// <summary>From the centre of the shape outwards.</summary>
    Radial,
    /// <summary>Across the contour's own width: inner edge to outer edge.</summary>
    Across
}

public sealed record StrokeOptions
{
    /// <summary>Thickness in pixels, 1 to <see cref="StrokeOps.MaxWidth"/> (fractions are kept).</summary>
    public double Width { get; init; } = 4;
    public StrokePosition Position { get; init; } = StrokePosition.Outside;
    public StrokeFill Fill { get; init; } = StrokeFill.Solid;
    public ColorBgra Color1 { get; init; } = ColorBgra.Black;
    public ColorBgra Color2 { get; init; } = ColorBgra.White;
    /// <summary>0 to 1, on top of the colours' own alpha.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>How far past the edge, on the far side, the band reaches - what a
    /// <see cref="StrokeField"/> must cover.</summary>
    public double Reach => Position == StrokePosition.Center ? Width / 2 : Width;

    /// <summary>The band as signed distances from the edge: negative inside, positive outside.</summary>
    internal (double Inner, double Outer) Band => Position switch
    {
        StrokePosition.Inside => (-Width, 0),
        StrokePosition.Center => (-Width / 2, Width / 2),
        _ => (0, Width)
    };
}

public static class StrokeOps
{
    /// <summary>Widest stroke accepted, in pixels.</summary>
    public const int MaxWidth = 500;

    /// <summary>The alpha channel of <paramref name="surface"/> as a coverage mask, so a layer's
    /// opaque pixels can be stroked like a selection.</summary>
    public static unsafe byte[] AlphaCoverage(Surface surface)
    {
        var mask = new byte[surface.Width * surface.Height];
        for (int y = 0; y < surface.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            int offset = y * surface.Width;
            for (int x = 0; x < surface.Width; x++) mask[offset + x] = row[x].A;
        }
        return mask;
    }

    /// <summary>One-shot stroke: builds the field and draws onto <paramref name="target"/>.
    /// Returns the rectangle it touched (empty when the shape is empty).</summary>
    public static EffectBounds Stroke(Surface target, ReadOnlySpan<byte> shape, StrokeOptions options)
    {
        var field = StrokeField.Build(shape, target.Width, target.Height, options.Reach);
        return field.Render(target, options);
    }
}

/// <summary>
/// The signed distance field of a shape (a coverage mask, 0-255 per pixel), kept for a window
/// around the shape wide enough for strokes up to <see cref="Reach"/> pixels past the edge.
/// Outside the canvas counts as outside the shape, so a shape touching the canvas border (Select
/// All, an opaque layer) gets an inside stroke along that border too: a frame.
/// </summary>
public sealed class StrokeField
{
    private const float Far = 1e20f;

    private readonly float[] _distance;   // signed: negative inside, positive outside
    private readonly int _wx, _wy, _ww, _wh;
    private readonly (int X, int Y, int W, int H) _shape;

    public int CanvasWidth { get; }
    public int CanvasHeight { get; }
    /// <summary>Widest reach past the edge (on either side) this field can draw.</summary>
    public double Reach { get; }
    public bool IsEmpty => _shape.W == 0;

    private StrokeField(int canvasWidth, int canvasHeight, double reach, (int, int, int, int) shape,
        float[] distance, int wx, int wy, int ww, int wh)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Reach = reach;
        _shape = shape;
        _distance = distance;
        (_wx, _wy, _ww, _wh) = (wx, wy, ww, wh);
    }

    /// <summary>Computes the field of <paramref name="shape"/> (row-major, width x height).</summary>
    public static StrokeField Build(ReadOnlySpan<byte> shape, int width, int height, double reach)
    {
        if (shape.Length != width * height) throw new ArgumentException("Mask size does not match the canvas", nameof(shape));
        reach = Math.Clamp(reach, 0, StrokeOps.MaxWidth);

        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            var row = shape.Slice(y * width, width);
            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
                if (row[x] >= 128) { if (first < 0) first = x; last = x; }
            if (first < 0) continue;
            minX = Math.Min(minX, first); maxX = Math.Max(maxX, last);
            minY = Math.Min(minY, y); maxY = y;
        }
        if (maxX < 0) return new StrokeField(width, height, reach, (0, 0, 0, 0), Array.Empty<float>(), 0, 0, 0, 0);

        // The window covers everything within reach of the shape, plus one ring so the outside
        // always has seeds - reaching past the canvas, where the shape never is.
        int pad = (int)Math.Ceiling(reach) + 2;
        int wx = minX - pad, wy = minY - pad;
        int ww = maxX - minX + 1 + 2 * pad, wh = maxY - minY + 1 + 2 * pad;

        var toInside = new float[ww * wh];
        var toOutside = new float[ww * wh];
        for (int y = 0; y < wh; y++)
        {
            int cy = wy + y;
            for (int x = 0; x < ww; x++)
            {
                int cx = wx + x;
                bool inside = cx >= 0 && cy >= 0 && cx < width && cy < height && shape[cy * width + cx] >= 128;
                toInside[y * ww + x] = inside ? 0 : Far;
                toOutside[y * ww + x] = inside ? Far : 0;
            }
        }
        Transform(toInside, ww, wh);
        Transform(toOutside, ww, wh);

        var distance = toInside;   // reused in place
        for (int y = 0; y < wh; y++)
        {
            int cy = wy + y;
            for (int x = 0; x < ww; x++)
            {
                int i = y * ww + x;
                int cx = wx + x;
                // Distances run centre to centre; the edge lies half a pixel from a boundary
                // pixel's centre.
                float d = toInside[i] == 0
                    ? 0.5f - MathF.Sqrt(toOutside[i])
                    : MathF.Sqrt(toInside[i]) - 0.5f;
                // A boundary pixel's partial coverage places the edge within it more finely, so
                // an antialiased selection gives a smooth contour rather than a stair-stepped one.
                if (MathF.Abs(d) <= 0.5f && cx >= 0 && cy >= 0 && cx < width && cy < height)
                    d = 0.5f - shape[cy * width + cx] / 255f;
                distance[i] = d;
            }
        }

        return new StrokeField(width, height, reach, (minX, minY, maxX - minX + 1, maxY - minY + 1),
            distance, wx, wy, ww, wh);
    }

    private static void Transform(float[] data, int w, int h)
    {
        int n = Math.Max(w, h);
        var line = new float[n];
        var hull = new int[n];
        var cuts = new float[n + 1];
        for (int x = 0; x < w; x++) Selection.SquaredDistanceLine(data, x, w, h, line, hull, cuts);
        for (int y = 0; y < h; y++) Selection.SquaredDistanceLine(data, y * w, 1, w, line, hull, cuts);
    }

    /// <summary>
    /// Composites the stroke over <paramref name="target"/> (same size as the canvas) and returns
    /// the rectangle that may have changed. A width beyond <see cref="Reach"/> is drawn only as far
    /// as the field reaches - rebuild the field for wider strokes.
    /// </summary>
    public unsafe EffectBounds Render(Surface target, StrokeOptions options)
    {
        if (target.Width != CanvasWidth || target.Height != CanvasHeight)
            throw new ArgumentException("Target size does not match the field", nameof(target));
        if (IsEmpty || options.Width <= 0 || options.Opacity <= 0) return default;

        var (inner, outer) = options.Band;
        double width = outer - inner;
        double opacity = Math.Clamp(options.Opacity, 0, 1);

        // The area the band covers bounds the gradient, so it spans the contour itself.
        double outerReach = Math.Max(0, outer);
        double gx0 = _shape.X - outerReach, gy0 = _shape.Y - outerReach;
        double gw = _shape.W + 2 * outerReach, gh = _shape.H + 2 * outerReach;
        double cx = gx0 + gw / 2, cy = gy0 + gh / 2;
        double radius = Math.Sqrt(gw * gw + gh * gh) / 2;

        var bounds = new EffectBounds(_wx, _wy, _ww, _wh).Clip(target);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = bounds.Y; y < bounds.Bottom; y++)
        {
            ColorBgra* row = (ColorBgra*)target.GetRowPointer(y);
            int fieldRow = (y - _wy) * _ww - _wx;
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                double d = _distance[fieldRow + x];
                // The band's overlap with this pixel's unit width.
                double cover = Math.Min(Math.Min(d - inner, outer - d) + 0.5, Math.Min(width, 1));
                if (cover <= 0) continue;

                double t = options.Fill switch
                {
                    StrokeFill.Horizontal => (x + 0.5 - gx0) / gw,
                    StrokeFill.Vertical => (y + 0.5 - gy0) / gh,
                    StrokeFill.Diagonal => ((x + 0.5 - gx0) + (y + 0.5 - gy0)) / (gw + gh),
                    StrokeFill.Radial => Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) / radius,
                    StrokeFill.Across => (d - inner) / width,
                    _ => 0
                };
                ColorBgra color = options.Fill == StrokeFill.Solid
                    ? options.Color1
                    : ColorBgra.Lerp(options.Color1, options.Color2, Math.Clamp(t, 0, 1));
                int alpha = (int)(color.A * cover * opacity + 0.5);
                if (alpha <= 0) continue;
                color.A = (byte)Math.Min(255, alpha);
                row[x] = ColorBgra.BlendOver(row[x], color);

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        return maxX < 0 ? default : new EffectBounds(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
