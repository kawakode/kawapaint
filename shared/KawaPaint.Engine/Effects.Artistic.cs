// KawaPaint — Tier 2.1 effect catalogue, Artistic category. See Effects.Distort.cs for the
// porting-approach note (same applies here). InkSketch/PencilSketch reuse GlowEffect/BlendOps
// from Effects.Photo.cs, same as pdn's originals compose GlowEffect/UserBlendOps.

namespace KawaPaint.Engine;

/// <summary>Glows the background, then finds edges via a directional 5x5 convolution and darkens
/// them to pure black/white outlines over it. InkOutline controls the black/white threshold,
/// Coloring how much of the glowed background color shows through.</summary>
public sealed class InkSketchEffect : IEffect
{
    private static readonly int[,] Kernel =
    {
        { -1, -1, -1, -1, -1 },
        { -1, -1, -1, -1, -1 },
        { -1, -1, 30, -1, -1 },
        { -1, -1, -1, -1, -1 },
        { -1, -1, -5, -1, -1 },
    };

    private readonly int _inkOutline, _coloring;

    public InkSketchEffect(int inkOutline, int coloring)
    {
        _inkOutline = Math.Clamp(inkOutline, 0, 99);
        _coloring = Math.Clamp(coloring, 0, 100);
    }
    public string Name => "Ink Sketch";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        new GlowEffect(6, -(_coloring - 50) * 2, -(_coloring - 50) * 2).Apply(s);

        int w = s.Width, h = s.Height;
        int threshold = _inkOutline * 255 / 100;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int rr = 0, gg = 0, bb = 0;
                for (int j = -2; j <= 2; j++)
                {
                    ColorBgra* row = (ColorBgra*)src.GetRowPointer(Math.Clamp(y + j, 0, h - 1));
                    for (int i = -2; i <= 2; i++)
                    {
                        ColorBgra c = row[Math.Clamp(x + i, 0, w - 1)];
                        int wgt = Kernel[j + 2, i + 2];
                        rr += c.R * wgt; gg += c.G * wgt; bb += c.B * wgt;
                    }
                }
                byte gray = ColorBgra.FromBgr(Clamp.B(bb), Clamp.B(gg), Clamp.B(rr)).GetIntensityByte();
                ColorBgra outline = gray > threshold ? ColorBgra.White : ColorBgra.Black;

                ColorBgra d = dst[x];
                dst[x] = ColorBgra.FromBgra(BlendOps.Darken(outline.B, d.B), BlendOps.Darken(outline.G, d.G),
                                             BlendOps.Darken(outline.R, d.R), d.A);
            }
        });
    }
}

/// <summary>Blurs, brightens/contrasts, inverts, then desaturates a copy of the image, and
/// Color-Dodge-blends the original desaturated pixel underneath it — the classic "blur + invert +
/// color dodge" pencil-sketch trick. PencilTipSize controls blur radius, ColorRange the contrast.</summary>
public sealed class PencilSketchEffect : IEffect
{
    private readonly int _pencilTipSize, _colorRange;

    public PencilSketchEffect(int pencilTipSize, int colorRange)
    {
        _pencilTipSize = Math.Clamp(pencilTipSize, 1, 20);
        _colorRange = colorRange;
    }
    public string Name => "Pencil Sketch";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();

        new BoxBlurEffect(_pencilTipSize).Apply(s);
        new BrightnessContrastEffect(_colorRange, 1.0 + (-_colorRange) / 100.0).Apply(s);
        new InvertEffect().Apply(s);
        new GrayscaleEffect().Apply(s);

        int w = s.Width, h = s.Height;
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            ColorBgra* srcRow = (ColorBgra*)src.GetRowPointer(y);
            ColorBgra* dstRow = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                byte gray = srcRow[x].GetIntensityByte();
                ColorBgra srcGrey = ColorBgra.FromBgra(gray, gray, gray, srcRow[x].A);
                ColorBgra d = dstRow[x];
                dstRow[x] = ColorBgra.FromBgra(
                    BlendOps.ColorDodge(srcGrey.B, d.B), BlendOps.ColorDodge(srcGrey.G, d.G),
                    BlendOps.ColorDodge(srcGrey.R, d.R), d.A);
            }
        });
    }
}

/// <summary>Mode filter: replaces each pixel with the average color of whichever intensity bucket
/// is most common in its square neighborhood, giving a smeared "brush stroke" look. BrushSize is
/// the neighborhood radius, Coarseness the number of intensity buckets (fewer = blockier).</summary>
public sealed class OilPaintingEffect : IEffect
{
    private readonly int _brushSize, _coarseness;

    public OilPaintingEffect(int brushSize, int coarseness)
    {
        _brushSize = Math.Clamp(brushSize, 1, 8);
        _coarseness = Math.Clamp(coarseness, 3, 255);
    }
    public string Name => "Oil Painting";

    public unsafe void Apply(Surface s)
    {
        using var src = s.Clone();
        int w = s.Width, h = s.Height;
        int maxIntensity = _coarseness;
        int bins = maxIntensity + 1;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            var intensityCount = new int[bins];
            var avgR = new long[bins];
            var avgG = new long[bins];
            var avgB = new long[bins];
            var avgA = new long[bins];
            ColorBgra* dst = (ColorBgra*)s.GetRowPointer(y);

            int top = Math.Max(0, y - _brushSize), bottom = Math.Min(h, y + _brushSize + 1);

            for (int x = 0; x < w; x++)
            {
                Array.Clear(intensityCount); Array.Clear(avgR); Array.Clear(avgG); Array.Clear(avgB); Array.Clear(avgA);
                int left = Math.Max(0, x - _brushSize), right = Math.Min(w, x + _brushSize + 1);

                for (int j = top; j < bottom; j++)
                {
                    ColorBgra* row = (ColorBgra*)src.GetRowPointer(j);
                    for (int i = left; i < right; i++)
                    {
                        ColorBgra c = row[i];
                        int intensity = c.GetIntensityByte() * maxIntensity / 255;
                        intensityCount[intensity]++;
                        avgR[intensity] += c.R; avgG[intensity] += c.G; avgB[intensity] += c.B; avgA[intensity] += c.A;
                    }
                }

                byte chosen = 0;
                int maxInstance = 0;
                for (int i = 0; i < bins; i++)
                    if (intensityCount[i] > maxInstance) { chosen = (byte)i; maxInstance = intensityCount[i]; }

                dst[x] = maxInstance > 0
                    ? ColorBgra.FromBgra((byte)(avgB[chosen] / maxInstance), (byte)(avgG[chosen] / maxInstance),
                                          (byte)(avgR[chosen] / maxInstance), (byte)(avgA[chosen] / maxInstance))
                    : src[x, y];
            }
        });
    }
}
