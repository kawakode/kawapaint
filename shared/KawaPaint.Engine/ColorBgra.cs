// KawaPaint — modern port of Paint.NET 3.36.
// ColorBgra: a 32-bit, non-premultiplied BGRA pixel. Byte layout (B@0, G@1, R@2, A@3)
// is deliberately identical to the original Paint.NET struct and to Skia's Bgra8888,
// so raw pixel buffers are interchangeable with SkiaSharp/Avalonia with zero conversion.

using System.Runtime.InteropServices;

namespace KawaPaint.Engine;

[StructLayout(LayoutKind.Explicit)]
public struct ColorBgra : IEquatable<ColorBgra>
{
    [FieldOffset(0)] public byte B;
    [FieldOffset(1)] public byte G;
    [FieldOffset(2)] public byte R;
    [FieldOffset(3)] public byte A;

    /// <summary>All four channels as a single little-endian uint (0xAARRGGBB in memory order B,G,R,A).</summary>
    [FieldOffset(0)] public uint Bgra;

    public const int BlueChannel = 0;
    public const int GreenChannel = 1;
    public const int RedChannel = 2;
    public const int AlphaChannel = 3;
    public const int SizeOf = 4;

    public static readonly ColorBgra Transparent = FromBgra(0, 0, 0, 0);
    public static readonly ColorBgra Black = FromBgra(0, 0, 0, 255);
    public static readonly ColorBgra White = FromBgra(255, 255, 255, 255);

    public static ColorBgra FromBgra(byte b, byte g, byte r, byte a)
    {
        ColorBgra c = default;
        c.B = b; c.G = g; c.R = r; c.A = a;
        return c;
    }

    public static ColorBgra FromBgr(byte b, byte g, byte r) => FromBgra(b, g, r, 255);

    public static ColorBgra FromUInt32(uint bgra)
    {
        ColorBgra c = default;
        c.Bgra = bgra;
        return c;
    }

    public uint ToUInt32() => Bgra;

    /// <summary>Byte access by channel index (0=B,1=G,2=R,3=A).</summary>
    public byte this[int channel]
    {
        readonly get => channel switch
        {
            0 => B,
            1 => G,
            2 => R,
            3 => A,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "valid range is [0,3]")
        };
        set
        {
            switch (channel)
            {
                case 0: B = value; break;
                case 1: G = value; break;
                case 2: R = value; break;
                case 3: A = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(channel), channel, "valid range is [0,3]");
            }
        }
    }

    /// <summary>Luminance (Rec.601) as a byte, alpha ignored. Matches the original fixed-point formula.</summary>
    public readonly byte GetIntensityByte() => (byte)((7471 * B + 38470 * G + 19595 * R) >> 16);

    /// <summary>Luminance in [0,1], alpha ignored.</summary>
    public readonly double GetIntensity() => (0.114 * B + 0.587 * G + 0.299 * R) / 255.0;

    public readonly byte GetMaxColorChannelValue() => Math.Max(B, Math.Max(G, R));

    public readonly byte GetAverageColorChannelValue() => (byte)((B + G + R) / 3);

    /// <summary>Linear interpolation between two colors. t in [0,1].</summary>
    public static ColorBgra Lerp(ColorBgra from, ColorBgra to, double t)
    {
        return FromBgra(
            (byte)ClampToByte(from.B + (to.B - from.B) * t),
            (byte)ClampToByte(from.G + (to.G - from.G) * t),
            (byte)ClampToByte(from.R + (to.R - from.R) * t),
            (byte)ClampToByte(from.A + (to.A - from.A) * t));
    }

    /// <summary>Source-over alpha composite of <paramref name="src"/> onto <paramref name="dst"/> (non-premultiplied).</summary>
    public static ColorBgra BlendOver(ColorBgra dst, ColorBgra src)
    {
        if (src.A == 255 || dst.A == 0)
        {
            return src;
        }

        if (src.A == 0)
        {
            return dst;
        }

        int sa = src.A;
        int da = dst.A * (255 - sa) / 255;
        int outA = sa + da;

        if (outA == 0)
        {
            return Transparent;
        }

        byte Mix(byte s, byte d) => (byte)((s * sa + d * da) / outA);

        return FromBgra(Mix(src.B, dst.B), Mix(src.G, dst.G), Mix(src.R, dst.R), (byte)outA);
    }

    private static int ClampToByte(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5);

    public static ColorBgra ParseHexString(string hexString) => FromUInt32(Convert.ToUInt32(hexString, 16));

    public readonly string ToHexString()
    {
        int rgb = (R << 16) | (G << 8) | B;
        return $"{A:X2}{rgb:X6}";
    }

    public readonly bool Equals(ColorBgra other) => Bgra == other.Bgra;

    public override readonly bool Equals(object? obj) => obj is ColorBgra other && Equals(other);

    public override readonly int GetHashCode() => (int)Bgra;

    public static bool operator ==(ColorBgra a, ColorBgra b) => a.Bgra == b.Bgra;

    public static bool operator !=(ColorBgra a, ColorBgra b) => a.Bgra != b.Bgra;

    public override readonly string ToString() => $"B={B},G={G},R={R},A={A}";
}
