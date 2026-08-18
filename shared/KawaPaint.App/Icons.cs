// KawaPaint — original line-art tool/action icons (themeable Avalonia geometry, 24x24 grid).
// Not derived from Paint.NET artwork; simple stroked glyphs.

using System.Collections.Generic;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace KawaPaint.App;

public static class Icons
{
    // key -> (path data, filled?)
    private static readonly Dictionary<string, (string Data, bool Fill)> Defs = new()
    {
        ["Pencil"]     = ("M4 20 L4 16 L15 5 L19 9 L8 20 Z M14 6 L18 10", false),
        ["Eraser"]     = ("M4 15 L11 8 a2 2 0 0 1 3 0 L20 14 L14 20 L8 20 Z M4 20 L20 20", false),
        ["Fill"]       = ("M11 3 L11 8 M6 8 L11 6 L18 13 L11 20 a2 2 0 0 1-3 0 L4 13 Z M20 14 c0 2-3 4-3 6", false),
        ["Pick"]       = ("M15 5 L19 9 M17 7 L9 15 L6 18 L5 19 L5 20 L6 20 L7 19 L10 16 Z", false),
        ["Line"]       = ("M4 20 L20 4", false),
        ["Rect"]       = ("M4 6 L20 6 L20 18 L4 18 Z", false),
        ["Ellipse"]    = ("M12 6 a8 6 0 1 0 0.1 0 Z", false),
        ["Gradient"]   = ("M4 6 L20 6 L20 18 L4 18 Z M4 6 L20 18", false),
        ["Text"]       = ("M5 20 L12 5 L19 20 M8 14 L16 14", false),
        ["Move"]       = ("M12 3 L12 21 M3 12 L21 12 M12 3 L9 6 M12 3 L15 6 M12 21 L9 18 M12 21 L15 18 M3 12 L6 9 M3 12 L6 15 M21 12 L18 9 M21 12 L18 15", false),
        ["RectSel"]    = ("M4 6 L20 6 L20 18 L4 18 Z", false),   // dashed applied via stroke
        ["EllipseSel"] = ("M12 6 a8 6 0 1 0 0.1 0 Z", false),    // dashed
        ["Lasso"]      = ("M6 16 a6 5 0 1 1 8 3 L12 22", false),
    };

    private static readonly HashSet<string> Dashed = new() { "RectSel", "EllipseSel" };

    public static Control Create(string key, double size = 18)
    {
        if (!Defs.TryGetValue(key, out var def))
            return new TextBlock { Text = "?" };

        var path = new Path
        {
            Data = Geometry.Parse(def.Data),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.7,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            Stroke = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
            Fill = def.Fill ? new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)) : null
        };
        if (Dashed.Contains(key)) path.StrokeDashArray = new AvaloniaList<double>(2, 2);

        return new Viewbox { Width = size, Height = size, Child = path };
    }
}
