// KawaPaint - Tier 2.1 effect catalogue, Blurs category. See Effects.Distort.cs for the
// porting-approach note (same applies here): algorithms transcribed from paint.net 3.36's
// src/Effects/, reimplemented on KawaPaint's own IEffect shape, no AA supersampling.

namespace KawaPaint.Engine;

/// <summary>Blurs along a line through each pixel, simulating camera/subject motion. Angle in
/// degrees, Distance in pixels.</summary>
public sealed class MotionBlurEffect : IEffect
{
    private readonly double _angleDeg;
    private readonly int _distance;

    public MotionBlurEffect(double angleDegrees, int distance)
    {
        _angleDeg = angleDegrees;
        _distance = Math.Max(1, distance);
    }
    public string Name => "Motion Blur";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;

        double theta = ((_angleDeg + 180) * 2 * Math.PI) / 360.0;
        double ex = _distance * Math.Cos(theta), ey = -(_distance * Math.Sin(theta));
        double sx0 = -ex / 2, sy0 = -ey / 2, ex1 = ex / 2, ey1 = ey / 2;

        int n = _distance;
        var pts = new (double X, double Y)[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0 : (double)i / n;
            pts[i] = (sx0 + (ex1 - sx0) * t, sy0 + (ey1 - sy0) * t);
        }

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0, count = 0;
                foreach (var (dx, dy) in pts)
                {
                    double px = x + dx, py = y + dy;
                    if (px < 0 || py < 0 || px > w - 1 || py > h - 1) continue;
                    ColorBgra c = src.GetBilinearSampleClamped((float)px, (float)py);
                    sumB += c.B; sumG += c.G; sumR += c.R; sumA += c.A;
                    count++;
                }
                dst[x] = count > 0
                    ? ColorBgra.FromBgra((byte)(sumB / count), (byte)(sumG / count), (byte)(sumR / count), (byte)(sumA / count))
                    : src[x, y];
            }
        });
    }
}

/// <summary>Blurs each pixel along an arc around the image center, simulating rotation. Angle is
/// the total sweep in degrees.</summary>
public sealed class RadialBlurEffect : IEffect
{
    private readonly double _angleDeg;
    private readonly int _quality;

    public RadialBlurEffect(double angleDegrees, int quality = 2)
    {
        _angleDeg = angleDegrees;
        _quality = Math.Clamp(quality, 1, 5);
    }
    public string Name => "Radial Blur";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        double cx = w / 2.0, cy = h / 2.0;
        int n = _quality * _quality * 8 + 8;
        double totalRad = _angleDeg * Math.PI / 180.0;
        var rotations = new (double Cos, double Sin)[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double offset = ((double)i / n - 0.5) * totalRad;
            rotations[i] = (Math.Cos(offset), Math.Sin(offset));
        }

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx, dy = y - cy;

                double sr = 0, sg = 0, sb = 0, sa = 0;
                int sc = 0;
                for (int i = 0; i <= n; i++)
                {
                    var (cos, sin) = rotations[i];
                    double sx = cx + dx * cos - dy * sin;
                    double sy = cy + dx * sin + dy * cos;
                    const double edgeEpsilon = 1e-9;
                    if (sx < -edgeEpsilon || sy < -edgeEpsilon ||
                        sx > w - 1 + edgeEpsilon || sy > h - 1 + edgeEpsilon) continue;
                    sx = Math.Clamp(sx, 0, w - 1);
                    sy = Math.Clamp(sy, 0, h - 1);
                    ColorBgra c = src.GetBilinearSampleClamped((float)sx, (float)sy);
                    sr += c.R * c.A; sg += c.G * c.A; sb += c.B * c.A; sa += c.A;
                    sc++;
                }

                dst[x] = sa > 0
                    ? ColorBgra.FromBgra(Clamp.B(sb / sa), Clamp.B(sg / sa), Clamp.B(sr / sa), Clamp.B(sa / sc))
                    : ColorBgra.Transparent;
            }
        });
    }
}

/// <summary>Blurs each pixel along a line toward/away from the image center, simulating a camera
/// zoom. Amount in [0,100].</summary>
public sealed class ZoomBlurEffect : IEffect
{
    private readonly int _amount;
    public ZoomBlurEffect(int amount) => _amount = Math.Clamp(amount, 0, 100);
    public string Name => "Zoom Blur";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        double cx = w / 2.0, cy = h / 2.0;
        const int n = 32;
        double zoomFactor = 1.0 - _amount / 400.0;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                double fx = x - cx, fy = y - cy;
                ColorBgra c0 = src[x, y];
                double sr = c0.R * c0.A, sg = c0.G * c0.A, sb = c0.B * c0.A, sa = c0.A;
                int sc = 1;

                for (int i = 0; i < n; i++)
                {
                    fx *= zoomFactor; fy *= zoomFactor;
                    double sx = cx + fx, sy = cy + fy;
                    if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1) continue;
                    ColorBgra c = src.GetBilinearSampleClamped((float)sx, (float)sy);
                    sr += c.R * c.A; sg += c.G * c.A; sb += c.B * c.A; sa += c.A;
                    sc++;
                }

                dst[x] = sa > 0
                    ? ColorBgra.FromBgra(Clamp.B(sb / sa), Clamp.B(sg / sa), Clamp.B(sr / sa), Clamp.B(sa / sc))
                    : ColorBgra.Transparent;
            }
        });
    }
}

/// <summary>Offsets several copies of the image outward in evenly-spaced directions and averages
/// them - a "ghosting"/kaleidoscope-fragment look. Fragments in [2,50], Distance in pixels.</summary>
public sealed class FragmentEffect : IEffect
{
    private readonly int _fragments, _distance;
    private readonly double _rotationDeg;

    public FragmentEffect(int fragments, double rotationDegrees, int distance)
    {
        _fragments = Math.Clamp(fragments, 2, 50);
        _rotationDeg = rotationDegrees;
        _distance = Math.Max(0, distance);
    }
    public string Name => "Fragment";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;

        double pointStep = 2 * Math.PI / _fragments;
        double rotationRad = (_rotationDeg - 90.0) * Math.PI / 180.0;
        var offsets = new (int Dx, int Dy)[_fragments];
        for (int i = 0; i < _fragments; i++)
        {
            double a = rotationRad + pointStep * i;
            offsets[i] = ((int)Math.Round(_distance * -Math.Sin(a), MidpointRounding.AwayFromZero),
                          (int)Math.Round(_distance * -Math.Cos(a), MidpointRounding.AwayFromZero));
        }

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0, count = 0;
                foreach (var (dx, dy) in offsets)
                {
                    int u = x - dx, v = y - dy;
                    if ((uint)u >= (uint)w || (uint)v >= (uint)h) continue;
                    ColorBgra c = src[u, v];
                    sumB += c.B; sumG += c.G; sumR += c.R; sumA += c.A;
                    count++;
                }
                dst[x] = count > 0
                    ? ColorBgra.FromBgra((byte)(sumB / count), (byte)(sumG / count), (byte)(sumR / count), (byte)(sumA / count))
                    : ColorBgra.Transparent;
            }
        });
    }
}

/// <summary>Edge-preserving blur: each pixel only blends with neighbors whose value is close to
/// its own, via a triangular falloff. Threshold controls how close "close" means.</summary>
public sealed class SurfaceBlurEffect : LocalHistogramEffect
{
    private readonly int[] _intensityFn = new int[256];

    public SurfaceBlurEffect(int radius, int threshold) : base(radius)
    {
        threshold = Math.Clamp(threshold, 1, 100);
        double slope = 96.0 / threshold;
        for (int i = 0; i < 256; i++)
            _intensityFn[i] = Math.Max(0, (int)Math.Round(255 - i * slope, MidpointRounding.AwayFromZero));
    }
    public override string Name => "Surface Blur";

    protected override ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha)
        => ColorBgra.FromBgra((byte)BlurChannel(src.B, hb), (byte)BlurChannel(src.G, hg), (byte)BlurChannel(src.R, hr), src.A);

    private int BlurChannel(int current, int[] hist)
    {
        long sum = 0, divisor = 0;
        for (int bin = 0; bin < 256; bin++)
        {
            int count = hist[bin];
            if (count <= 0) continue;
            int intensity = _intensityFn[Math.Abs(bin - current)];
            if (intensity <= 0) continue;
            long t = (long)count * intensity;
            sum += t * bin;
            divisor += t;
        }
        return divisor > 0 ? (int)((sum + divisor / 2) / divisor) : current;
    }
}

/// <summary>True circular-kernel uniform blur - a rounder, softer look than the square/separable
/// Gaussian Blur menu entry, most visible at hard silhouette edges.</summary>
public sealed class UnfocusEffect : LocalHistogramEffect
{
    public UnfocusEffect(int radius) : base(radius) { }
    public override string Name => "Unfocus";

    protected override ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha)
    {
        if (area <= 0) return src;
        long sb = 0, sg = 0, sr = 0, sa = 0;
        for (int i = 0; i < 256; i++)
        {
            sb += (long)i * hb[i]; sg += (long)i * hg[i]; sr += (long)i * hr[i]; sa += (long)i * ha[i];
        }
        return ColorBgra.FromBgra((byte)(sb / area), (byte)(sg / area), (byte)(sr / area), (byte)(sa / area));
    }
}
