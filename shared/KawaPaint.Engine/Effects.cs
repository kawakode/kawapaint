// KawaPaint - engine-side image effects. Each effect mutates a layer's Surface in place;
// neighbourhood effects (blur/sharpen) snapshot the input themselves. RGB is processed and
// alpha preserved, except blur which averages all channels.

namespace KawaPaint.Engine;

public interface IEffect
{
    string Name { get; }
    void Apply(Surface surface);
}

internal static class Clamp
{
    public static byte B(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    public static byte B(double v) => B((int)(v + 0.5));
}

/// <summary>Base for effects that transform each pixel independently.</summary>
public abstract class PerPixelEffect : IEffect
{
    public abstract string Name { get; }
    protected abstract ColorBgra Transform(ColorBgra c);

    public unsafe void Apply(Surface s)
    {
        int width = s.Width;
        System.Threading.Tasks.Parallel.For(0, s.Height, y =>
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < width; x++)
                row[x] = Transform(row[x]);
        });
    }
}

public sealed class InvertEffect : PerPixelEffect
{
    public override string Name => "Invert Colors";
    protected override ColorBgra Transform(ColorBgra c)
        => ColorBgra.FromBgra((byte)(255 - c.B), (byte)(255 - c.G), (byte)(255 - c.R), c.A);
}

public sealed class GrayscaleEffect : PerPixelEffect
{
    public override string Name => "Black & White";
    protected override ColorBgra Transform(ColorBgra c)
    {
        byte v = c.GetIntensityByte();
        return ColorBgra.FromBgra(v, v, v, c.A);
    }
}

public sealed class SepiaEffect : PerPixelEffect
{
    public override string Name => "Sepia";
    protected override ColorBgra Transform(ColorBgra c)
    {
        int r = c.R, g = c.G, b = c.B;
        byte nr = Clamp.B(0.393 * r + 0.769 * g + 0.189 * b);
        byte ng = Clamp.B(0.349 * r + 0.686 * g + 0.168 * b);
        byte nb = Clamp.B(0.272 * r + 0.534 * g + 0.131 * b);
        return ColorBgra.FromBgra(nb, ng, nr, c.A);
    }
}

public sealed class BrightnessContrastEffect : PerPixelEffect
{
    private readonly int _brightness;
    private readonly double _contrast;

    /// <param name="brightness">[-255,255] added per channel.</param>
    /// <param name="contrast">multiplier around mid-gray (1.0 = no change).</param>
    public BrightnessContrastEffect(int brightness, double contrast)
    {
        _brightness = brightness;
        _contrast = contrast;
    }

    public override string Name => "Brightness / Contrast";

    protected override ColorBgra Transform(ColorBgra c)
    {
        byte Adj(int v) => Clamp.B((v - 128) * _contrast + 128 + _brightness);
        return ColorBgra.FromBgra(Adj(c.B), Adj(c.G), Adj(c.R), c.A);
    }
}

/// <summary>Applies a 256-entry tone curve LUT to the R, G, B channels.</summary>
public sealed class CurvesEffect : PerPixelEffect
{
    private readonly byte[] _lut;
    public override string Name => "Curves";

    public CurvesEffect(byte[] lut256)
    {
        if (lut256.Length != 256) throw new ArgumentException("LUT must have 256 entries");
        _lut = lut256;
    }

    protected override ColorBgra Transform(ColorBgra c)
        => ColorBgra.FromBgra(_lut[c.B], _lut[c.G], _lut[c.R], c.A);
}

/// <summary>Posterize: reduce each channel to N levels.</summary>
public sealed class PosterizeEffect : PerPixelEffect
{
    private readonly byte[] _lut = new byte[256];
    public override string Name => "Posterize";

    public PosterizeEffect(int levels)
    {
        levels = Math.Clamp(levels, 2, 256);
        for (int v = 0; v < 256; v++)
        {
            int q = v * (levels - 1) / 255;          // bucket
            _lut[v] = (byte)(q * 255 / (levels - 1)); // back to 0..255
        }
    }

    protected override ColorBgra Transform(ColorBgra c)
        => ColorBgra.FromBgra(_lut[c.B], _lut[c.G], _lut[c.R], c.A);
}

/// <summary>Adds uniform random noise (+/- amount) to each channel.</summary>
public sealed class NoiseEffect : PerPixelEffect
{
    private readonly int _amount;
    public NoiseEffect(int amount) => _amount = Math.Max(0, amount);
    public override string Name => "Add Noise";

    protected override ColorBgra Transform(ColorBgra c)
    {
        // Random.Shared, not a private Random: PerPixelEffect runs Transform on every worker
        // thread at once, and a plain Random shared across threads corrupts its own state and
        // starts returning zeros (i.e. bands of un-noised pixels).
        int n = Random.Shared.Next(-_amount, _amount + 1);
        return ColorBgra.FromBgra(Clamp.B(c.B + n), Clamp.B(c.G + n), Clamp.B(c.R + n), c.A);
    }
}

/// <summary>Levels: remap each channel through input black/white points and gamma.</summary>
public sealed class LevelsEffect : PerPixelEffect
{
    private readonly byte[] _lut = new byte[256];
    public override string Name => "Levels";

    public LevelsEffect(int inBlack, int inWhite, double gamma)
    {
        inBlack = Math.Clamp(inBlack, 0, 254);
        inWhite = Math.Clamp(inWhite, inBlack + 1, 255);
        double invGamma = 1.0 / Math.Max(0.01, gamma);
        double range = inWhite - inBlack;

        for (int v = 0; v < 256; v++)
        {
            double t = (v - inBlack) / range;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            _lut[v] = Clamp.B(Math.Pow(t, invGamma) * 255);
        }
    }

    protected override ColorBgra Transform(ColorBgra c)
        => ColorBgra.FromBgra(_lut[c.B], _lut[c.G], _lut[c.R], c.A);
}

/// <summary>Auto levels: stretch each channel's used range to full [0,255].</summary>
public sealed class AutoLevelsEffect : IEffect
{
    public string Name => "Auto Levels";

    public unsafe void Apply(Surface s)
    {
        int minB = 255, minG = 255, minR = 255, maxB = 0, maxG = 0, maxR = 0;
        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width; x++)
            {
                ColorBgra c = row[x];
                if (c.B < minB) minB = c.B; if (c.B > maxB) maxB = c.B;
                if (c.G < minG) minG = c.G; if (c.G > maxG) maxG = c.G;
                if (c.R < minR) minR = c.R; if (c.R > maxR) maxR = c.R;
            }
        }

        byte[] lutB = BuildStretch(minB, maxB);
        byte[] lutG = BuildStretch(minG, maxG);
        byte[] lutR = BuildStretch(minR, maxR);

        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width; x++)
                row[x] = ColorBgra.FromBgra(lutB[row[x].B], lutG[row[x].G], lutR[row[x].R], row[x].A);
        }
    }

    private static byte[] BuildStretch(int min, int max)
    {
        var lut = new byte[256];
        int range = Math.Max(1, max - min);
        for (int v = 0; v < 256; v++)
            lut[v] = Clamp.B((v - min) * 255 / range);
        return lut;
    }
}

/// <summary>Hue rotation (degrees), saturation multiplier, and lightness delta [-1,1] via HSL.</summary>
public sealed class HueSaturationEffect : PerPixelEffect
{
    private readonly double _hueShift;   // degrees
    private readonly double _sat;        // multiplier
    private readonly double _light;      // additive, [-1,1]

    public HueSaturationEffect(double hueShiftDegrees, double saturation, double lightnessDelta)
    {
        _hueShift = hueShiftDegrees;
        _sat = saturation;
        _light = lightnessDelta;
    }

    public override string Name => "Hue / Saturation";

    protected override ColorBgra Transform(ColorBgra c)
    {
        RgbToHsl(c.R, c.G, c.B, out double h, out double s, out double l);
        h = (h + _hueShift / 360.0) % 1.0;
        if (h < 0) h += 1.0;
        s = Math.Clamp(s * _sat, 0, 1);
        l = Math.Clamp(l + _light, 0, 1);
        HslToRgb(h, s, l, out byte r, out byte g, out byte b);
        return ColorBgra.FromBgra(b, g, r, c.A);
    }

    private static void RgbToHsl(int r, int g, int b, out double h, out double s, out double l)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
        l = (max + min) / 2;
        if (max == min) { h = s = 0; return; }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
        else if (max == gd) h = (bd - rd) / d + 2;
        else h = (rd - gd) / d + 4;
        h /= 6;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        if (s == 0) { r = g = b = Clamp.B(l * 255); return; }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = Clamp.B(Hue(p, q, h + 1.0 / 3) * 255);
        g = Clamp.B(Hue(p, q, h) * 255);
        b = Clamp.B(Hue(p, q, h - 1.0 / 3) * 255);
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}

/// <summary>Separable box blur; a decent, fast stand-in for a Gaussian.</summary>
public sealed class BoxBlurEffect : IEffect
{
    private readonly int _radius;
    public BoxBlurEffect(int radius) => _radius = Math.Max(1, radius);
    public string Name => "Blur";

    public unsafe void Apply(Surface s)
    {
        using var tmp = s.Clone();
        BlurPass(s, tmp, _radius, horizontal: true);   // s -> tmp
        BlurPass(tmp, s, _radius, horizontal: false);  // tmp -> s
    }

    private static unsafe void BlurPass(Surface src, Surface dst, int radius, bool horizontal)
    {
        int w = src.Width, h = src.Height;
        int len = horizontal ? w : h;
        int lines = horizontal ? h : w;
        int window = radius * 2 + 1;

        for (int line = 0; line < lines; line++)
        {
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;

            ColorBgra Get(int i) => horizontal
                ? ((ColorBgra*)src.GetRowPointer(line))[i]
                : *(ColorBgra*)src.GetPointPointer(line, i);
            void Set(int i, ColorBgra c)
            {
                if (horizontal) ((ColorBgra*)dst.GetRowPointer(line))[i] = c;
                else *(ColorBgra*)dst.GetPointPointer(line, i) = c;
            }

            // Prime the running window at i = 0.
            for (int k = -radius; k <= radius; k++)
            {
                var c = Get(Math.Clamp(k, 0, len - 1));
                sumB += c.B; sumG += c.G; sumR += c.R; sumA += c.A;
            }

            for (int i = 0; i < len; i++)
            {
                Set(i, ColorBgra.FromBgra((byte)(sumB / window), (byte)(sumG / window),
                                          (byte)(sumR / window), (byte)(sumA / window)));

                var outC = Get(Math.Clamp(i - radius, 0, len - 1));
                var inC = Get(Math.Clamp(i + radius + 1, 0, len - 1));
                sumB += inC.B - outC.B;
                sumG += inC.G - outC.G;
                sumR += inC.R - outC.R;
                sumA += inC.A - outC.A;
            }
        }
    }
}

/// <summary>3x3 emboss convolution (adds a mid-gray bias).</summary>
public sealed class EmbossEffect : IEffect
{
    private static readonly int[,] Kernel = { { -2, -1, 0 }, { -1, 1, 1 }, { 0, 1, 2 } };
    public string Name => "Emboss";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* d = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int sb = 0, sg = 0, sr = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    ColorBgra* r = (ColorBgra*)src.GetRowPointer(Math.Clamp(y + dy, 0, h - 1));
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int k = Kernel[dy + 1, dx + 1];
                        ColorBgra c = r[Math.Clamp(x + dx, 0, w - 1)];
                        sb += c.B * k; sg += c.G * k; sr += c.R * k;
                    }
                }
                byte a = ((ColorBgra*)src.GetRowPointer(y))[x].A;
                d[x] = ColorBgra.FromBgra(Clamp.B(sb + 128), Clamp.B(sg + 128), Clamp.B(sr + 128), a);
            }
        });
    }
}

/// <summary>Sobel edge detection (gradient magnitude per channel).</summary>
public sealed class EdgeDetectEffect : IEffect
{
    public string Name => "Edge Detect";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        int[,] gxK = { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } };
        int[,] gyK = { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } };

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* d = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int gxB = 0, gxG = 0, gxR = 0, gyB = 0, gyG = 0, gyR = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    ColorBgra* r = (ColorBgra*)src.GetRowPointer(Math.Clamp(y + dy, 0, h - 1));
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        ColorBgra c = r[Math.Clamp(x + dx, 0, w - 1)];
                        int kx = gxK[dy + 1, dx + 1], ky = gyK[dy + 1, dx + 1];
                        gxB += c.B * kx; gxG += c.G * kx; gxR += c.R * kx;
                        gyB += c.B * ky; gyG += c.G * ky; gyR += c.R * ky;
                    }
                }
                byte Mag(int gx, int gy) => Clamp.B(Math.Sqrt((double)gx * gx + (double)gy * gy));
                byte a = ((ColorBgra*)src.GetRowPointer(y))[x].A;
                d[x] = ColorBgra.FromBgra(Mag(gxB, gyB), Mag(gxG, gyG), Mag(gxR, gyR), a);
            }
        });
    }
}

/// <summary>3x3 unsharp/sharpen convolution.</summary>
public sealed class SharpenEffect : IEffect
{
    public string Name => "Sharpen";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dRow = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    ColorBgra* sRow = (ColorBgra*)src.GetRowPointer(Math.Clamp(y + dy, 0, h - 1));
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int weight = (dx == 0 && dy == 0) ? 9 : -1;
                        ColorBgra c = sRow[Math.Clamp(x + dx, 0, w - 1)];
                        sumB += c.B * weight; sumG += c.G * weight; sumR += c.R * weight;
                    }
                }
                ColorBgra center = ((ColorBgra*)src.GetRowPointer(y))[x];
                dRow[x] = ColorBgra.FromBgra(Clamp.B(sumB), Clamp.B(sumG), Clamp.B(sumR), center.A);
            }
        });
    }
}
