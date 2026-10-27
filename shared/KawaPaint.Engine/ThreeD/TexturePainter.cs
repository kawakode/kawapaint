using System.Numerics;
using SkiaSharp;

namespace KawaPaint.Engine.ThreeD;

/// <summary>
/// Texture-space helpers for painting on a 3D model: a round dab placed at a texture coordinate,
/// and the UV layout drawn as a guide layer. The texture is the document itself, so both work on
/// ordinary layer surfaces.
/// </summary>
public static class TexturePainter
{
    /// <summary>
    /// Paints an antialiased round dab of <paramref name="radius"/> texture pixels centred on
    /// <paramref name="uv"/>. Texture coordinates repeat, so a dab that crosses an edge also lands
    /// on the opposite side. Returns the rectangles it changed, already clipped to the surface.
    /// </summary>
    public static List<(int X, int Y, int W, int H)> Dab(Surface target, Vector2 uv, float radius, ColorBgra color)
    {
        ArgumentNullException.ThrowIfNull(target);
        var changed = new List<(int, int, int, int)>();
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || radius <= 0 || color.A == 0) return changed;

        int width = target.Width, height = target.Height;
        double cx = Wrap(uv.X) * width, cy = Wrap(uv.Y) * height;
        int reach = (int)Math.Ceiling(radius + 1);
        foreach (int ox in new[] { 0, -width, width })
            foreach (int oy in new[] { 0, -height, height })
            {
                double x = cx + ox, y = cy + oy;
                int left = Math.Max(0, (int)Math.Floor(x - reach)), right = Math.Min(width, (int)Math.Ceiling(x + reach));
                int top = Math.Max(0, (int)Math.Floor(y - reach)), bottom = Math.Min(height, (int)Math.Ceiling(y + reach));
                if (right <= left || bottom <= top) continue;
                StampCircle(target, x, y, radius, color, left, top, right, bottom);
                changed.Add((left, top, right - left, bottom - top));
            }
        return changed;
    }

    private static unsafe void StampCircle(Surface target, double cx, double cy, float radius, ColorBgra color,
        int left, int top, int right, int bottom)
    {
        for (int y = top; y < bottom; y++)
        {
            ColorBgra* row = (ColorBgra*)target.GetRowPointer(y);
            double dy = y + 0.5 - cy;
            for (int x = left; x < right; x++)
            {
                double dx = x + 0.5 - cx;
                double coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                if (coverage <= 0) continue;
                byte alpha = (byte)Math.Round(color.A * coverage);
                row[x] = ColorBgra.BlendOver(row[x], ColorBgra.FromBgra(color.B, color.G, color.R, alpha));
            }
        }
    }

    private static double Wrap(float value) => value - Math.Floor(value);

    /// <summary>
    /// Draws every UV-mapped face's outline at texture size (<paramref name="width"/> x
    /// <paramref name="height"/>) on a transparent surface, as a guide for painting the texture in
    /// 2D. Coordinates outside 0..1 wrap like the texture does.
    /// </summary>
    public static Surface RenderUvLayout(ObjMesh mesh, int width, int height, ColorBgra lineColor)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var surface = new Surface(width, height);
        if (!mesh.HasTexCoords) return surface;

        using SKBitmap bitmap = surface.WrapSKBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Color = new SKColor(lineColor.R, lineColor.G, lineColor.B, lineColor.A),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        using var path = new SKPath();
        foreach (ObjTriangle triangle in mesh.Triangles)
        {
            if (!triangle.HasTexCoords) continue;
            Vector2 a = mesh.TexCoords[triangle.TA], b = mesh.TexCoords[triangle.TB], c = mesh.TexCoords[triangle.TC];
            // Shift the whole face by whole tiles so it lands on the texture; a face straddling a
            // tile edge keeps its shape rather than being split.
            float shiftX = MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)));
            float shiftY = MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            Vector2 shift = new(shiftX, shiftY);
            Vector2 size = new(width, height);
            Vector2 pa = (a - shift) * size, pb = (b - shift) * size, pc = (c - shift) * size;
            path.MoveTo(pa.X, pa.Y);
            path.LineTo(pb.X, pb.Y);
            path.LineTo(pc.X, pc.Y);
            path.Close();
        }
        canvas.DrawPath(path, paint);
        canvas.Flush();
        return surface;
    }
}
