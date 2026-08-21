// KawaPaint - Tier 2.1 effect catalogue, Noise/Stylize/Photo categories. See Effects.Distort.cs
// for the porting-approach note (same applies here).

namespace KawaPaint.Engine;

/// <summary>
/// Base for effects driven by a per-channel histogram of a circular neighbourhood around each
/// pixel (median, outline, ...). Mirrors paint.net's LocalHistogramEffect, minus its incremental
/// row-sliding optimization - this recomputes the histogram from scratch per pixel, which is
/// simpler and fine at the modest radii a paint app's UI exposes, but is O(radius^2) per pixel;
/// revisit with the sliding-window version if large radii turn out to matter.
/// </summary>
public abstract class LocalHistogramEffect : IEffect
{
    private readonly int _radius;
    protected LocalHistogramEffect(int radius) => _radius = Math.Max(1, radius);
    public abstract string Name { get; }

    protected abstract ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha);

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height, rad = _radius;
        int cutoff = ((rad * 2 + 1) * (rad * 2 + 1) + 2) / 4;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            var hb = new int[256];
            var hg = new int[256];
            var hr = new int[256];
            var ha = new int[256];
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);

            int top = Math.Max(0, y - rad), bottom = Math.Min(h - 1, y + rad);

            for (int x = 0; x < w; x++)
            {
                Array.Clear(hb); Array.Clear(hg); Array.Clear(hr); Array.Clear(ha);
                int area = 0;
                int left = Math.Max(0, x - rad), right = Math.Min(w - 1, x + rad);

                for (int v = top; v <= bottom; v++)
                {
                    int dy = v - y;
                    ColorBgra* row = (ColorBgra*)src.GetRowPointer(v);
                    for (int u = left; u <= right; u++)
                    {
                        int dx = u - x;
                        if (dx * dx + dy * dy > cutoff) continue;
                        ColorBgra c = row[u];
                        hb[c.B]++; hg[c.G]++; hr[c.R]++; ha[c.A]++;
                        area++;
                    }
                }

                dst[x] = Apply(src[x, y], area, hb, hg, hr, ha);
            }
        });
    }
}

/// <summary>Circular-neighborhood median filter (a strong denoiser that preserves edges better than blur).</summary>
public sealed class MedianEffect : LocalHistogramEffect
{
    private readonly int _percentile;
    public MedianEffect(int radius, int percentile = 50) : base(radius) => _percentile = Math.Clamp(percentile, 0, 100);
    public override string Name => "Median";

    protected override ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha)
    {
        int minCount = area * _percentile / 100;
        return ColorBgra.FromBgra(Scan(hb, minCount), Scan(hg, minCount), Scan(hr, minCount), Scan(ha, minCount));
    }

    private static byte Scan(int[] hist, int minCount)
    {
        int v = 0, count = 0;
        while (v < 255 && hist[v] == 0) v++;
        while (v < 255 && count < minCount) { count += hist[v]; v++; }
        return (byte)v;
    }
}

/// <summary>Highlights edges as a trimmed per-channel range within each pixel's neighborhood.</summary>
public sealed class OutlineEffect : LocalHistogramEffect
{
    private readonly int _intensity;
    public OutlineEffect(int thickness, int intensity) : base(thickness) => _intensity = Math.Clamp(intensity, 0, 100);
    public override string Name => "Outline";

    protected override ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha)
    {
        int minCount1 = area * (100 - _intensity) / 200;
        int minCount2 = area * (100 + _intensity) / 200;

        var (bLo, bHi) = Spread(hb, minCount1, minCount2);
        var (gLo, gHi) = Spread(hg, minCount1, minCount2);
        var (rLo, rHi) = Spread(hr, minCount1, minCount2);
        var (_, aHi) = Spread(ha, minCount1, minCount2);

        return ColorBgra.FromBgra((byte)(255 - (bHi - bLo)), (byte)(255 - (gHi - gLo)), (byte)(255 - (rHi - rLo)), (byte)aHi);
    }

    private static (int Lo, int Hi) Spread(int[] hist, int minCount1, int minCount2)
    {
        int v = 0;
        while (v < 255 && hist[v] == 0) v++;
        int count = 0;
        while (v < 255 && count < minCount1) { count += hist[v]; v++; }
        int lo = v;
        while (v < 255 && count < minCount2) { count += hist[v]; v++; }
        return (lo, v);
    }
}

/// <summary>Directional shading via a 3x3 weighted convolution (Chris Crosetto's "color difference" relief). Angle in degrees.</summary>
public sealed class ReliefEffect : IEffect
{
    private readonly double[][] _weights;

    public ReliefEffect(double angleDegrees)
    {
        double r = angleDegrees * Math.PI / 180.0;
        double dr = Math.PI / 4.0;
        _weights = [[Math.Cos(r + dr), Math.Cos(r + 2 * dr), Math.Cos(r + 3 * dr)],
                    [Math.Cos(r),      1.0,                  Math.Cos(r + 4 * dr)],
                    [Math.Cos(r - dr), Math.Cos(r - 2 * dr), Math.Cos(r - 3 * dr)]];
    }
    public string Name => "Relief";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        var weights = _weights;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                double rSum = 0, gSum = 0, bSum = 0;
                for (int fy = 0; fy < 3; fy++)
                {
                    ColorBgra* row = (ColorBgra*)src.GetRowPointer(Math.Clamp(y - 1 + fy, 0, h - 1));
                    for (int fx = 0; fx < 3; fx++)
                    {
                        ColorBgra c = row[Math.Clamp(x - 1 + fx, 0, w - 1)];
                        double wgt = weights[fy][fx];
                        rSum += wgt * c.R; gSum += wgt * c.G; bSum += wgt * c.B;
                    }
                }
                dst[x] = ColorBgra.FromBgra(Clamp.B(bSum), Clamp.B(gSum), Clamp.B(rSum), 255);
            }
        });
    }
}

/// <summary>Darkens toward the edges with a cosine falloff. Amount (density) in [0,1], Radius scale in [0.1,4.0].</summary>
public sealed class VignetteEffect : IEffect
{
    private readonly double _amount;
    private readonly double _radiusScale;

    public VignetteEffect(double amount, double radiusScale)
    {
        _amount = Math.Clamp(amount, 0, 1);
        _radiusScale = Math.Max(0.01, radiusScale);
    }
    public string Name => "Vignette";

    public unsafe void Apply(Surface s)
    {
        int w = s.Width, h = s.Height;
        double hw = w / 2.0, hh = h / 2.0;
        double radius = Math.Max(w, h) * 0.5 * _radiusScale;
        radius *= radius;
        double radiusR = Math.PI / (8 * radius);
        double amount1 = 1.0 - _amount;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            double iy2 = y - hh; iy2 *= iy2;

            for (int x = 0; x < w; x++)
            {
                double ix = x - hw;
                double d = (iy2 + ix * ix) * radiusR;
                double factor;

                if (d > Math.PI)
                {
                    factor = amount1;
                }
                else
                {
                    double f = Math.Cos(d);
                    if (f <= 0) factor = amount1;
                    else { f *= f; f *= f; factor = amount1 + _amount * f; }
                }

                ColorBgra c = row[x];
                row[x] = ColorBgra.FromBgra(Clamp.B(c.B * factor), Clamp.B(c.G * factor), Clamp.B(c.R * factor), c.A);
            }
        });
    }
}
