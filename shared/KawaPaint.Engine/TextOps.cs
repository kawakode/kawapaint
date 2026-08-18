// KawaPaint — text rasterization via SkiaSharp, drawn straight onto a layer's Surface.

using SkiaSharp;

namespace KawaPaint.Engine;

public static class TextOps
{
    /// <summary>Draws multi-line text with its top-left at (x,y).</summary>
    public static void DrawText(Surface s, string text, float x, float y, float sizePx, ColorBgra color, string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        using var bitmap = s.WrapSKBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var typeface = fontFamily is null ? SKTypeface.Default : SKTypeface.FromFamilyName(fontFamily);
        using var font = new SKFont(typeface, sizePx);
        using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A), IsAntialias = true };

        SKFontMetrics metrics = font.Metrics;
        float lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
        float baseline = y - metrics.Ascent;   // so (x,y) is the top-left of the text block

        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, font, paint);
            baseline += lineHeight;
        }
    }
}
