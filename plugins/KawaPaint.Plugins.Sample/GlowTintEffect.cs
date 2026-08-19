using KawaPaint.Engine;

namespace KawaPaint.Plugins.Sample;

/// <summary>Blends each pixel toward a target color by Amount%. Invert switches the target from
/// the fixed tint color to that pixel's own inverted color, so all three parameters genuinely
/// participate in the output rather than just existing for the dialog to render.</summary>
internal sealed class GlowTintEffect : PerPixelEffect
{
    private readonly int _amount;
    private readonly bool _invert;
    private readonly ColorBgra _tint;

    public GlowTintEffect(int amount, bool invert, ColorBgra tint)
    {
        _amount = amount;
        _invert = invert;
        _tint = tint;
    }

    public override string Name => "Glow Tint";

    protected override ColorBgra Transform(ColorBgra c)
    {
        ColorBgra target = _invert
            ? ColorBgra.FromBgra((byte)(255 - c.B), (byte)(255 - c.G), (byte)(255 - c.R), c.A)
            : _tint;
        return ColorBgra.Lerp(c, target, _amount / 100.0);
    }
}
