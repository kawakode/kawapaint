// KawaPaint - layer-combining helpers (pixel work kept in the engine).

namespace KawaPaint.Engine;

public static class LayerOps
{
    /// <summary>Composites <paramref name="above"/> (with its blend mode + opacity) onto <paramref name="below"/> in place.</summary>
    public static unsafe void MergeInto(Layer below, Layer above)
    {
        var dst = below.Surface;
        var src = above.Surface;
        BlendMode mode = above.BlendMode;
        byte op = above.Opacity;

        for (int y = 0; y < dst.Height; y++)
        {
            ColorBgra* d = (ColorBgra*)dst.GetRowPointer(y);
            ColorBgra* s = (ColorBgra*)src.GetRowPointer(y);
            Blending.CompositeSpan(mode, d, s, dst.Width, op);
        }
    }
}
