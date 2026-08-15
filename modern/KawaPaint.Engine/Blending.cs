namespace KawaPaint.Engine;

/// <summary>
/// Separable blend-mode math + Porter-Duff "over" compositing (SVG blending model).
/// Channels are non-premultiplied bytes [0,255]. For BlendMode.Normal this reduces to
/// a straight source-over.
/// </summary>
public static class Blending
{
    /// <summary>Per-channel blend of a backdrop (dst) and source (src) value, both [0,255].</summary>
    public static byte BlendChannel(BlendMode mode, int d, int s) => (byte)(mode switch
    {
        BlendMode.Normal => s,
        BlendMode.Multiply => d * s / 255,
        BlendMode.Additive => Math.Min(255, d + s),
        BlendMode.Screen => 255 - (255 - d) * (255 - s) / 255,
        BlendMode.Darken => Math.Min(d, s),
        BlendMode.Lighten => Math.Max(d, s),
        BlendMode.Difference => Math.Abs(d - s),
        BlendMode.Negation => 255 - Math.Abs(255 - d - s),
        BlendMode.Xor => d ^ s,
        BlendMode.Overlay => d < 128 ? 2 * d * s / 255 : 255 - 2 * (255 - d) * (255 - s) / 255,
        BlendMode.ColorDodge => s >= 255 ? 255 : Math.Min(255, d * 255 / (255 - s)),
        BlendMode.ColorBurn => s <= 0 ? 0 : Math.Max(0, 255 - (255 - d) * 255 / s),
        BlendMode.Reflect => s >= 255 ? 255 : Math.Min(255, d * d / (255 - s)),
        BlendMode.Glow => d >= 255 ? 255 : Math.Min(255, s * s / (255 - d)),
        _ => s
    });

    /// <summary>
    /// Composites <paramref name="src"/> (scaled by <paramref name="layerOpacity"/> [0,255])
    /// over <paramref name="dst"/> using the given blend mode.
    /// </summary>
    public static ColorBgra Composite(BlendMode mode, ColorBgra dst, ColorBgra src, int layerOpacity)
    {
        int ea = src.A * layerOpacity / 255;   // effective source alpha
        if (ea == 0) return dst;

        double sa = ea / 255.0;
        double ba = dst.A / 255.0;
        double ao = sa + ba * (1 - sa);
        if (ao <= 0) return ColorBgra.Transparent;

        byte Out(int sC, int dC)
        {
            int bl = mode == BlendMode.Normal ? sC : BlendChannel(mode, dC, sC);
            // Premultiplied output (SVG): Co = as*(1-ab)*Cs + as*ab*B + (1-as)*ab*Cb
            double co = sa * (1 - ba) * sC + sa * ba * bl + (1 - sa) * ba * dC;
            int v = (int)(co / ao + 0.5);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        return ColorBgra.FromBgra(
            Out(src.B, dst.B),
            Out(src.G, dst.G),
            Out(src.R, dst.R),
            (byte)(ao * 255 + 0.5));
    }
}
