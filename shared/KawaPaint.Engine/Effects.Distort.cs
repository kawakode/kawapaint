// KawaPaint — Tier 2.1 effect catalogue, Distort category. Algorithms ported from paint.net
// 3.36's src/Effects/ (MIT-licensed, see origin/3.36pdn) onto KawaPaint's own IEffect shape —
// not a literal file port, since the originals are built on WinForms/PropertySystem plumbing
// that doesn't exist here. Anti-aliased supersampling (pdn's Utility.GetRgssOffsets) is dropped
// throughout for simplicity, matching this file's existing single-sample style.

namespace KawaPaint.Engine;

public enum WarpEdgeMode { Clamp, Wrap }

/// <summary>
/// Base for effects that resample each destination pixel from a transformed source location
/// (inverse mapping), centered on the image. Mirrors paint.net's WarpEffectBase without its
/// offset/quality/edge-behavior-choice knobs.
/// </summary>
public abstract class WarpEffect : IEffect
{
    public abstract string Name { get; }
    protected virtual WarpEdgeMode EdgeMode => WarpEdgeMode.Clamp;

    /// <summary>
    /// Given a destination pixel position relative to the image center, returns the
    /// center-relative source position to sample from.
    /// </summary>
    protected abstract (double X, double Y) InverseTransform(double x, double y, double halfWidth, double halfHeight, double maxRadius);

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        double hw = w / 2.0, hh = h / 2.0;
        double maxRadius = Math.Min(hw, hh);
        bool wrap = EdgeMode == WarpEdgeMode.Wrap;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            double ry = y - hh;
            for (int x = 0; x < w; x++)
            {
                double rx = x - hw;
                var (sx, sy) = InverseTransform(rx, ry, hw, hh, maxRadius);
                float px = (float)(sx + hw), py = (float)(sy + hh);
                dst[x] = wrap ? src.GetBilinearSampleWrapped(px, py) : src.GetBilinearSampleClamped(px, py);
            }
        });
    }
}

/// <summary>Radial "magnifying glass" distortion. Amount in [-200,100] (percent).</summary>
public sealed class BulgeEffect : WarpEffect
{
    private readonly double _amount;
    public BulgeEffect(double amount) => _amount = amount;
    public override string Name => "Bulge";

    protected override (double X, double Y) InverseTransform(double x, double y, double hw, double hh, double maxRadius)
    {
        double r = Math.Sqrt(x * x + y * y);
        double rscale1 = 1.0 - r / maxRadius;
        if (rscale1 <= 0) return (x, y);
        double rscale2 = 1 - (_amount / 100.0) * rscale1 * rscale1;
        return (x * rscale2, y * rscale2);
    }
}

/// <summary>Swirls pixels around the center. Amount in [-200,200], Size in [0.01,2.0].</summary>
public sealed class TwistEffect : WarpEffect
{
    private readonly double _amount;
    private readonly double _size;
    public TwistEffect(double amount, double size)
    {
        _amount = -amount;
        _size = 1.0 / Math.Max(0.01, size);
    }
    public override string Name => "Twist";

    protected override (double X, double Y) InverseTransform(double x, double y, double hw, double hh, double maxRadius)
    {
        double twist = _amount * _amount * Math.Sign(_amount);
        double invMaxRad = 1.0 / maxRadius;
        double rad = Math.Sqrt(x * x + y * y);
        double theta = Math.Atan2(y, x);
        double t = 1 - (rad * _size) * invMaxRad;
        t = t < 0 ? 0 : t * t * t;
        theta += (t * twist) / 100.0;
        return (rad * Math.Cos(theta), rad * Math.Sin(theta));
    }
}

/// <summary>Fisheye/anti-fisheye via radial distance inversion. Amount in [-4,4] (0 = no change).</summary>
public sealed class PolarInversionEffect : WarpEffect
{
    private readonly double _amount;
    public PolarInversionEffect(double amount) => _amount = amount;
    public override string Name => "Polar Inversion";
    protected override WarpEdgeMode EdgeMode => WarpEdgeMode.Wrap;

    protected override (double X, double Y) InverseTransform(double x, double y, double hw, double hh, double maxRadius)
    {
        double denom = x * x + y * y;
        if (denom == 0) return (x, y);
        double defaultRadius2 = maxRadius * maxRadius;
        double invertDistance = 1.0 + (defaultRadius2 / denom - 1.0) * _amount;
        return (x * invertDistance, y * invertDistance);
    }
}

/// <summary>Kaleidoscope-style repeating tile warp. Rotation in degrees, SquareSize in pixels, Curvature in [-100,100].</summary>
public sealed class TileEffect : WarpEffect
{
    private readonly double _rotation, _squareSize, _curvature;
    public TileEffect(double rotationDegrees, double squareSize, double curvature)
    {
        _rotation = -rotationDegrees;
        _squareSize = Math.Max(1, squareSize);
        _curvature = curvature;
    }
    public override string Name => "Tile";
    protected override WarpEdgeMode EdgeMode => WarpEdgeMode.Wrap;

    protected override (double X, double Y) InverseTransform(double x, double y, double hw, double hh, double maxRadius)
    {
        double rad = _rotation * Math.PI / 180.0;
        double sin = Math.Sin(rad), cos = Math.Cos(rad);
        double scale = Math.PI / _squareSize;
        double intensity = _curvature * _curvature / 10.0 * Math.Sign(_curvature);

        double s1 = cos * x + sin * y;
        double t1 = -sin * x + cos * y;
        double s2 = s1 + intensity * Math.Tan(s1 * scale);
        double t2 = t1 + intensity * Math.Tan(t1 * scale);

        return (cos * s2 - sin * t2, sin * s2 + cos * t2);
    }
}

/// <summary>Random per-pixel scatter within an annulus, averaged over several samples.</summary>
public sealed class FrostedGlassEffect : IEffect
{
    private readonly double _minRadius, _maxRadius;
    private readonly int _samples;

    public FrostedGlassEffect(double minRadius, double maxRadius, int samples)
    {
        _minRadius = Math.Max(0, minRadius);
        _maxRadius = Math.Max(_minRadius, maxRadius);
        _samples = Math.Clamp(samples, 1, 8);
    }
    public string Name => "Frosted Glass";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        double minRadius = Math.Min(_minRadius, Math.Min(w, h) / 2.0);
        double delta = _maxRadius - minRadius;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int i = 0; i < _samples; i++)
                {
                    double sx, sy;
                    int guard = 0;
                    do
                    {
                        double angle = Random.Shared.NextDouble() * Math.PI * 2.0;
                        double dist = minRadius + Random.Shared.NextDouble() * delta;
                        sx = x + Math.Cos(angle) * dist;
                        sy = y + Math.Sin(angle) * dist;
                    } while ((sx < 0 || sx > w - 1 || sy < 0 || sy > h - 1) && ++guard < 32);

                    var c = src.GetBilinearSampleClamped((float)sx, (float)sy);
                    sumB += c.B; sumG += c.G; sumR += c.R; sumA += c.A;
                }
                dst[x] = ColorBgra.FromBgra((byte)(sumB / _samples), (byte)(sumG / _samples),
                                             (byte)(sumR / _samples), (byte)(sumA / _samples));
            }
        });
    }
}

/// <summary>Averages each CellSize×CellSize block to a single flat color.</summary>
public sealed class PixelateEffect : IEffect
{
    private readonly int _cellSize;
    public PixelateEffect(int cellSize) => _cellSize = Math.Max(1, cellSize);
    public string Name => "Pixelate";

    public unsafe void Apply(Surface s)
    {
        int w = s.Width, h = s.Height, cell = _cellSize;
        int rows = (h + cell - 1) / cell;

        System.Threading.Tasks.Parallel.For(0, rows, cellRow =>
        {
            int y0 = cellRow * cell;
            int y1 = Math.Min(y0 + cell, h);

            for (int x0 = 0; x0 < w; x0 += cell)
            {
                int x1 = Math.Min(x0 + cell, w);
                long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                int count = 0;

                for (int y = y0; y < y1; y++)
                {
                    ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
                    for (int x = x0; x < x1; x++)
                    {
                        var c = row[x];
                        sumB += c.B; sumG += c.G; sumR += c.R; sumA += c.A;
                        count++;
                    }
                }

                var avg = ColorBgra.FromBgra((byte)(sumB / count), (byte)(sumG / count),
                                              (byte)(sumR / count), (byte)(sumA / count));

                for (int y = y0; y < y1; y++)
                {
                    ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
                    for (int x = x0; x < x1; x++)
                        row[x] = avg;
                }
            }
        });
    }
}
