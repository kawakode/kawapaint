// KawaPaint - linear gradient fill. Colors are interpolated along the drag vector and
// alpha-composited onto the surface (so semi-transparent endpoints blend).

namespace KawaPaint.Engine;

public static class GradientOps
{
    public static unsafe void LinearGradient(Surface s, double x0, double y0, double x1, double y1,
                                             ColorBgra from, ColorBgra to)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len2 = dx * dx + dy * dy;

        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < s.Width; x++)
            {
                double t = len2 < 1e-6 ? 0 : ((x - x0) * dx + (y - y0) * dy) / len2;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                row[x] = ColorBgra.BlendOver(row[x], ColorBgra.Lerp(from, to, t));
            }
        }
    }
}
