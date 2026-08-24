// KawaPaint - text rasterization via SkiaSharp, drawn straight onto a layer's Surface.

using SkiaSharp;
using KawaPaint.Engine.MailMerge;

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

    /// <summary>Draws wrapped/aligned text clipped to a rectangle, optionally shrinking to fit.</summary>
    public static void DrawTextBox(Surface s, string text, int x, int y, int width, int height,
        float requestedSize, ColorBgra color, string? fontFamily, bool wrap, bool shrinkToFit,
        DynamicTextAlignment alignment, DynamicTextVerticalAlignment verticalAlignment)
    {
        if (string.IsNullOrEmpty(text) || width <= 0 || height <= 0) return;
        using var bitmap = s.WrapSKBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var typeface = string.IsNullOrWhiteSpace(fontFamily) ? SKTypeface.Default : SKTypeface.FromFamilyName(fontFamily);
        using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A), IsAntialias = true };
        using var font = new SKFont(typeface, Math.Max(1, requestedSize));

        List<string> lines;
        float lineHeight;
        while (true)
        {
            lines = LayoutLines(text, width, font, paint, wrap);
            SKFontMetrics metrics = font.Metrics;
            lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
            float widest = lines.Count == 0 ? 0 : lines.Max(line => font.MeasureText(line.AsSpan(), paint));
            if (!shrinkToFit || font.Size <= 6 || (widest <= width && lines.Count * lineHeight <= height)) break;
            font.Size = Math.Max(6, font.Size - 1);
        }

        SKFontMetrics fm = font.Metrics;
        float blockHeight = lines.Count * lineHeight;
        float top = verticalAlignment switch
        {
            DynamicTextVerticalAlignment.Center => y + (height - blockHeight) / 2,
            DynamicTextVerticalAlignment.Bottom => y + height - blockHeight,
            _ => y
        };
        SKTextAlign textAlign = alignment switch
        {
            DynamicTextAlignment.Center => SKTextAlign.Center,
            DynamicTextAlignment.Right => SKTextAlign.Right,
            _ => SKTextAlign.Left
        };
        float drawX = alignment switch
        {
            DynamicTextAlignment.Center => x + width / 2f,
            DynamicTextAlignment.Right => x + width,
            _ => x
        };

        canvas.Save();
        canvas.ClipRect(new SKRect(x, y, x + width, y + height));
        float baseline = top - fm.Ascent;
        foreach (string line in lines)
        {
            canvas.DrawText(line, drawX, baseline, textAlign, font, paint);
            baseline += lineHeight;
        }
        canvas.Restore();
    }

    private static List<string> LayoutLines(string text, int width, SKFont font, SKPaint paint, bool wrap)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (!wrap) { lines.Add(paragraph); continue; }
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.MeasureText(candidate.AsSpan(), paint) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines;
    }
}
