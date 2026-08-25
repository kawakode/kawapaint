// KawaPaint - Tier 2.1 effect catalogue, Render category. See Effects.Distort.cs for the
// porting-approach note (same applies here). Unlike every other effect in this catalogue, these
// three are generators: they overwrite every pixel from pure math, ignoring the surface's
// existing content entirely (matching pdn's own CloudsEffect/JuliaFractalEffect/
// MandelbrotFractalEffect, which render straight into DstArgs).

namespace KawaPaint.Engine;

/// <summary>Fills the surface with a Perlin-noise cloud pattern, gradiented between two colors.
/// Scale controls cloud size, Power controls how detailed/turbulent it looks.</summary>
public sealed class CloudsEffect : IEffect
{
    private readonly int _scale;
    private readonly double _power;
    private readonly byte _seed;
    private readonly ColorBgra _colorFrom, _colorTo;

    public CloudsEffect(int scale, double power, int seed, ColorBgra colorFrom, ColorBgra colorTo)
    {
        _scale = Math.Max(2, scale);
        _power = Math.Clamp(power, 0, 1);
        _seed = unchecked((byte)(DateTime.Now.Ticks ^ seed));
        _colorFrom = colorFrom;
        _colorTo = colorTo;
    }
    public string Name => "Clouds";

    public unsafe void Apply(Surface s) => Apply(s, EffectBounds.Full(s));

    public unsafe void Apply(Surface s, EffectBounds requested)
    {
        EffectBounds bounds = requested.Clip(s);
        if (bounds.IsEmpty) return;
        int w = s.Width, h = s.Height;
        System.Threading.Tasks.Parallel.For(bounds.Y, bounds.Bottom, y =>
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            int dy = 2 * y - h;
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                int dx = 2 * x - w;
                double val = PerlinNoise2D.Noise((double)dx / _scale, (double)dy / _scale, 12, _power, _seed);
                double t = Math.Clamp((val + 1) / 2, 0, 1);
                row[x] = ColorBgra.Lerp(_colorFrom, _colorTo, t);
            }
        });
    }
}

/// <summary>Renders a Julia set fractal. Factor controls color banding, Zoom the magnification,
/// Angle rotates the view.</summary>
public sealed class JuliaFractalEffect : IEffect
{
    private static readonly double Log2_10000 = Math.Log(10000);
    private readonly double _factor, _zoom, _angleTheta;

    public JuliaFractalEffect(double factor, double zoom, double angleDegrees)
    {
        _factor = factor;
        _zoom = Math.Max(0.01, zoom);
        _angleTheta = angleDegrees * Math.PI / 180.0;
    }
    public string Name => "Julia Fractal";

    private static double Julia(double x, double y, double r, double i)
    {
        double c = 0;
        while (c < 256 && x * x + y * y < 10000)
        {
            double t = x;
            x = x * x - y * y + r;
            y = 2 * t * y + i;
            c++;
        }
        c -= 2 - 2 * Log2_10000 / Math.Log(x * x + y * y);
        return c;
    }

    public unsafe void Apply(Surface s) => Apply(s, EffectBounds.Full(s));

    public unsafe void Apply(Surface s, EffectBounds requested)
    {
        EffectBounds bounds = requested.Clip(s);
        if (bounds.IsEmpty) return;
        const double jr = 0.3125, ji = 0.03;
        int w = s.Width, h = s.Height;
        double invH = 1.0 / h, invZoom = 1.0 / _zoom, aspect = (double)h / w;

        System.Threading.Tasks.Parallel.For(bounds.Y, bounds.Bottom, y =>
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                double u = (2.0 * x - w) * invH, v = (2.0 * y - h) * invH;
                double radius = Math.Sqrt(u * u + v * v);
                double theta = Math.Atan2(v, u) + _angleTheta;
                double uP = radius * Math.Cos(theta), vP = radius * Math.Sin(theta);
                double jX = (uP - vP * aspect) * invZoom, jY = (vP + uP * aspect) * invZoom;

                double c = _factor * Julia(jX, jY, jr, ji);
                row[x] = ColorBgra.FromBgra(Clamp.B(c - 768), Clamp.B(c - 512), Clamp.B(c - 256), Clamp.B(c - 0));
            }
        });
    }
}

/// <summary>Renders the Mandelbrot set fractal. Factor controls color banding/detail, Zoom the
/// magnification (pdn centers the default view near one of the set's classic boundary details,
/// not the whole set - kept as-is), Angle rotates the view.</summary>
public sealed class MandelbrotFractalEffect : IEffect
{
    private const double XOffset = -0.7, YOffset = -0.29, Max = 100000;
    private static readonly double InvLogMax = 1.0 / Math.Log(Max);

    private readonly int _factor;
    private readonly double _zoom, _angleTheta;
    private readonly bool _invert;

    public MandelbrotFractalEffect(int factor, double zoomSlider, double angleDegrees, bool invertColors = false)
    {
        _factor = Math.Clamp(factor, 1, 10);
        _zoom = 1 + 20.0 * zoomSlider;
        _angleTheta = angleDegrees * Math.PI / 180.0;
        _invert = invertColors;
    }
    public string Name => "Mandelbrot Fractal";

    private static double Mandelbrot(double r, double i, int factor)
    {
        int c = 0;
        double x = 0, y = 0;
        while (c * factor < 1024 && x * x + y * y < Max)
        {
            double t = x;
            x = x * x - y * y + r;
            y = 2 * t * y + i;
            c++;
        }
        return c - Math.Log(y * y + x * x) * InvLogMax;
    }

    public unsafe void Apply(Surface s) => Apply(s, EffectBounds.Full(s));

    public unsafe void Apply(Surface s, EffectBounds requested)
    {
        EffectBounds bounds = requested.Clip(s);
        if (bounds.IsEmpty) return;
        int w = s.Width, h = s.Height;
        double invH = 1.0 / h, invZoom = 1.0 / _zoom;

        System.Threading.Tasks.Parallel.For(bounds.Y, bounds.Bottom, y =>
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = bounds.X; x < bounds.Right; x++)
            {
                double u = (2.0 * x - w) * invH, v = (2.0 * y - h) * invH;
                double radius = Math.Sqrt(u * u + v * v);
                double theta = Math.Atan2(v, u) + _angleTheta;
                double uP = radius * Math.Cos(theta), vP = radius * Math.Sin(theta);

                double m = Mandelbrot((uP * invZoom) + XOffset, (vP * invZoom) + YOffset, _factor);
                double c = 64 + _factor * m;

                byte r8 = Clamp.B(c - 768), g8 = Clamp.B(c - 512), b8 = Clamp.B(c - 256), a8 = Clamp.B(c - 0);
                if (_invert) { r8 = (byte)(255 - r8); g8 = (byte)(255 - g8); b8 = (byte)(255 - b8); }
                row[x] = ColorBgra.FromBgra(b8, g8, r8, a8);
            }
        });
    }
}
