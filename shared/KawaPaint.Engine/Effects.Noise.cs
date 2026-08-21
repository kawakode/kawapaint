// KawaPaint - Tier 2.1 effect catalogue, Noise category. See Effects.Distort.cs for the
// porting-approach note (same applies here).

namespace KawaPaint.Engine;

/// <summary>
/// Selective smoothing: remaps each pixel toward where its value would sit if its neighborhood's
/// colors were evenly distributed (a percentile/rank normalization), blended in by Strength and
/// weighted down on already-bright pixels. Ported as-is from pdn's ReduceNoiseEffect, including
/// its negative lerp factor - matches production paint.net rather than a straightforward "blend
/// toward neighborhood average" guess.
/// </summary>
public sealed class ReduceNoiseEffect : LocalHistogramEffect
{
    private readonly double _strength;

    public ReduceNoiseEffect(int radius, double strength) : base(radius)
        => _strength = -0.2 * Math.Clamp(strength, 0, 1);
    public override string Name => "Reduce Noise";

    protected override ColorBgra Apply(ColorBgra src, int area, int[] hb, int[] hg, int[] hr, int[] ha)
    {
        if (area <= 0) return src;

        int rc = 0, gc = 0, bc = 0;
        for (int i = 0; i < src.R; i++) rc += hr[i];
        for (int i = 0; i < src.G; i++) gc += hg[i];
        for (int i = 0; i < src.B; i++) bc += hb[i];

        var normalized = ColorBgra.FromBgr((byte)(bc * 255 / area), (byte)(gc * 255 / area), (byte)(rc * 255 / area));
        double lerp = _strength * (1 - 0.75 * src.GetIntensity());
        return ColorBgra.Lerp(src, normalized, lerp);
    }
}
