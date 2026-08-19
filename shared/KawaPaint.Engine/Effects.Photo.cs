// KawaPaint — Tier 2.1 effect catalogue, Photo category. See Effects.Distort.cs for the
// porting-approach note (same applies here). Also shared by Effects.Artistic.cs.

namespace KawaPaint.Engine;

/// <summary>
/// Standard two-layer blend-mode formulas (base = bottom/existing pixel, blend = top/incoming
/// pixel), used by the composited effects below in place of paint.net's UserBlendOps — pdn's own
/// versions are alpha-compositing-aware fixed-point code tuned for full layer blending, which is
/// more than these effects need since here one side is always effectively opaque.
/// </summary>
internal static class BlendOps
{
    public static byte Screen(byte a, byte b) => (byte)(255 - (255 - a) * (255 - b) / 255);

    public static byte Overlay(byte baseC, byte blend) => baseC < 128
        ? (byte)(2 * baseC * blend / 255)
        : (byte)(255 - 2 * (255 - baseC) * (255 - blend) / 255);

    public static byte Darken(byte a, byte b) => Math.Min(a, b);

    public static byte ColorDodge(byte baseC, byte blend) => blend == 255 ? (byte)255 : Clamp.B(baseC * 255 / (255 - blend));
}

/// <summary>Blurs a copy of the image, brightens/contrasts it, then Screen-blends it back over the
/// original — a soft bloom around bright areas.</summary>
public sealed class GlowEffect : IEffect
{
    private readonly int _radius, _brightness, _contrastPct;

    public GlowEffect(int radius, int brightness, int contrast)
    {
        _radius = Math.Clamp(radius, 1, 20);
        _brightness = brightness;
        _contrastPct = contrast;
    }
    public string Name => "Glow";

    public unsafe void Apply(Surface s)
    {
        using var glow = s.Clone();
        new BoxBlurEffect(_radius).Apply(glow);
        new BrightnessContrastEffect(_brightness, 1.0 + _contrastPct / 100.0).Apply(glow);

        int w = s.Width, h = s.Height;
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            ColorBgra* glowRow = (ColorBgra*)glow.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                ColorBgra o = dst[x], g = glowRow[x];
                dst[x] = ColorBgra.FromBgra(BlendOps.Screen(g.B, o.B), BlendOps.Screen(g.G, o.G), BlendOps.Screen(g.R, o.R), o.A);
            }
        });
    }
}

/// <summary>Desaturates pixels that look like red-eye flash reflections (red channel dominant,
/// highly saturated) toward gray, leaving everything else untouched. Meant to be applied to a
/// selection drawn around the eye. Tolerance controls detection sensitivity, Saturation how much
/// residual redness is kept.</summary>
public sealed class RedEyeRemoveEffect : PerPixelEffect
{
    private readonly int _tolerance;
    private readonly double _setSaturation;

    public RedEyeRemoveEffect(int tolerance, int saturation)
    {
        _tolerance = tolerance;
        _setSaturation = saturation / 100.0;
    }
    public override string Name => "Red Eye Removal";

    protected override ColorBgra Transform(ColorBgra c)
    {
        int saturation = GetSaturation(c);
        int difference = c.R - Math.Max(c.B, c.G);

        if (difference > _tolerance && saturation > 100)
        {
            double i = 255.0 * c.GetIntensity();
            byte ib = (byte)(i * _setSaturation);
            return ColorBgra.FromBgra(c.B, c.G, ib, c.A);
        }
        return c;
    }

    private static int GetSaturation(ColorBgra c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
        double delta = max - min;
        double sVal = (max == 0 || delta == 0) ? 0 : delta / max;
        return (int)(sVal * 255);
    }
}

/// <summary>Blurs and brightens the base, then overlays a warmth-tinted desaturated version of the
/// original on top to bring back some detail — the classic "soft portrait" retouch. Softness
/// controls blur radius, Lighting brightens/darkens, Warmth shifts red/blue balance.</summary>
public sealed class SoftenPortraitEffect : IEffect
{
    private readonly int _softness, _lighting, _warmth;

    public SoftenPortraitEffect(int softness, int lighting, int warmth)
    {
        _softness = Math.Clamp(softness, 0, 10);
        _lighting = lighting;
        _warmth = warmth;
    }
    public string Name => "Soften Portrait";

    public unsafe void Apply(Surface s)
    {
        using var original = s.Clone();

        new BoxBlurEffect(Math.Max(1, _softness * 3)).Apply(s);
        new BrightnessContrastEffect(_lighting, 1.0 + (-_lighting / 2.0) / 100.0).Apply(s);

        float redAdjust = 1.0f + _warmth / 100.0f;
        float blueAdjust = 1.0f - _warmth / 100.0f;
        int w = s.Width, h = s.Height;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* origRow = (ColorBgra*)original.GetRowPointer(y);
            ColorBgra* dstRow = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                ColorBgra o = origRow[x];
                byte gray = o.GetIntensityByte();
                byte r = Clamp.B(gray * redAdjust);
                byte b = Clamp.B(gray * blueAdjust);
                ColorBgra srcGrey = ColorBgra.FromBgra(b, gray, r, o.A);

                ColorBgra d = dstRow[x];
                dstRow[x] = ColorBgra.FromBgra(
                    BlendOps.Overlay(srcGrey.B, d.B), BlendOps.Overlay(srcGrey.G, d.G),
                    BlendOps.Overlay(srcGrey.R, d.R), d.A);
            }
        });
    }
}
