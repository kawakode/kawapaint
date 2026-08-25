namespace KawaPaint.Engine;

/// <summary>
/// Separable blend-mode math + Porter-Duff "over" compositing (SVG blending model).
/// Channels are non-premultiplied bytes [0,255]. For BlendMode.Normal this reduces to
/// a straight source-over.
/// </summary>
public static class Blending
{
    private interface IChannelBlend { static abstract int Apply(int backdrop, int source); }
    private readonly struct MultiplyBlend : IChannelBlend { public static int Apply(int d, int s) => d * s / 255; }
    private readonly struct AdditiveBlend : IChannelBlend { public static int Apply(int d, int s) => Math.Min(255, d + s); }
    private readonly struct ScreenBlend : IChannelBlend { public static int Apply(int d, int s) => 255 - (255 - d) * (255 - s) / 255; }
    private readonly struct DarkenBlend : IChannelBlend { public static int Apply(int d, int s) => Math.Min(d, s); }
    private readonly struct LightenBlend : IChannelBlend { public static int Apply(int d, int s) => Math.Max(d, s); }
    private readonly struct DifferenceBlend : IChannelBlend { public static int Apply(int d, int s) => Math.Abs(d - s); }
    private readonly struct NegationBlend : IChannelBlend { public static int Apply(int d, int s) => 255 - Math.Abs(255 - d - s); }
    private readonly struct XorBlend : IChannelBlend { public static int Apply(int d, int s) => d ^ s; }
    private readonly struct OverlayBlend : IChannelBlend { public static int Apply(int d, int s) => d < 128 ? 2 * d * s / 255 : 255 - 2 * (255 - d) * (255 - s) / 255; }
    private readonly struct DodgeBlend : IChannelBlend { public static int Apply(int d, int s) => s >= 255 ? 255 : Math.Min(255, d * 255 / (255 - s)); }
    private readonly struct BurnBlend : IChannelBlend { public static int Apply(int d, int s) => s <= 0 ? 0 : Math.Max(0, 255 - (255 - d) * 255 / s); }
    private readonly struct ReflectBlend : IChannelBlend { public static int Apply(int d, int s) => s >= 255 ? 255 : Math.Min(255, d * d / (255 - s)); }
    private readonly struct GlowBlend : IChannelBlend { public static int Apply(int d, int s) => d >= 255 ? 255 : Math.Min(255, s * s / (255 - d)); }

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
        if (mode == BlendMode.Normal)
        {
            if (ea == 255) return src;
            return CompositeNormal(dst, src, ea);
        }

        double sa = ea / 255.0;
        double ba = dst.A / 255.0;
        double ao = sa + ba * (1 - sa);
        if (ao <= 0) return ColorBgra.Transparent;

        byte Out(int sC, int dC)
        {
            int bl = BlendChannel(mode, dC, sC);
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

    /// <summary>Composites one contiguous row span. The blend-mode dispatch occurs once here,
    /// rather than three times for every pixel through <see cref="BlendChannel"/>.</summary>
    public static unsafe void CompositeSpan(BlendMode mode, ColorBgra* destination,
        ColorBgra* source, int count, int layerOpacity)
    {
        if (count <= 0 || layerOpacity <= 0) return;
        switch (mode)
        {
            case BlendMode.Normal:
                for (int x = 0; x < count; x++)
                {
                    ColorBgra src = source[x];
                    int alpha = src.A * layerOpacity / 255;
                    if (alpha == 0) continue;
                    destination[x] = alpha == 255 ? src : CompositeNormal(destination[x], src, alpha);
                }
                break;
            case BlendMode.Multiply: CompositeSpan<MultiplyBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Additive: CompositeSpan<AdditiveBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Screen: CompositeSpan<ScreenBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Darken: CompositeSpan<DarkenBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Lighten: CompositeSpan<LightenBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Difference: CompositeSpan<DifferenceBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Negation: CompositeSpan<NegationBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Xor: CompositeSpan<XorBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Overlay: CompositeSpan<OverlayBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.ColorDodge: CompositeSpan<DodgeBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.ColorBurn: CompositeSpan<BurnBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Reflect: CompositeSpan<ReflectBlend>(destination, source, count, layerOpacity); break;
            case BlendMode.Glow: CompositeSpan<GlowBlend>(destination, source, count, layerOpacity); break;
        }
    }

    private static unsafe void CompositeSpan<T>(ColorBgra* destination, ColorBgra* source,
        int count, int layerOpacity) where T : struct, IChannelBlend
    {
        for (int x = 0; x < count; x++)
        {
            ColorBgra src = source[x];
            int effectiveAlpha = src.A * layerOpacity / 255;
            if (effectiveAlpha == 0) continue;

            ColorBgra dst = destination[x];
            double sourceAlpha = effectiveAlpha / 255.0;
            double backdropAlpha = dst.A / 255.0;
            double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
            if (outputAlpha <= 0) { destination[x] = ColorBgra.Transparent; continue; }

            byte Mix(int sourceChannel, int destinationChannel)
            {
                int blend = T.Apply(destinationChannel, sourceChannel);
                double premultiplied = sourceAlpha * (1 - backdropAlpha) * sourceChannel
                    + sourceAlpha * backdropAlpha * blend
                    + (1 - sourceAlpha) * backdropAlpha * destinationChannel;
                int value = (int)(premultiplied / outputAlpha + 0.5);
                return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            }

            destination[x] = ColorBgra.FromBgra(Mix(src.B, dst.B), Mix(src.G, dst.G),
                Mix(src.R, dst.R), (byte)(outputAlpha * 255 + 0.5));
        }
    }

    /// <summary>Specialized source-over path for the overwhelmingly common Normal blend mode. It
    /// deliberately retains the former floating-point operation order: an algebraically equivalent
    /// integer formula differs by one at rare half-way values because of binary rounding.</summary>
    private static ColorBgra CompositeNormal(ColorBgra dst, ColorBgra src, int effectiveAlpha)
    {
        double sourceAlpha = effectiveAlpha / 255.0;
        double backdropAlpha = dst.A / 255.0;
        double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);

        byte Mix(int source, int destination)
        {
            double premultiplied = sourceAlpha * (1 - backdropAlpha) * source
                + sourceAlpha * backdropAlpha * source
                + (1 - sourceAlpha) * backdropAlpha * destination;
            int value = (int)(premultiplied / outputAlpha + 0.5);
            return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
        }

        return ColorBgra.FromBgra(
            Mix(src.B, dst.B),
            Mix(src.G, dst.G),
            Mix(src.R, dst.R),
            (byte)(outputAlpha * 255 + 0.5));
    }
}
