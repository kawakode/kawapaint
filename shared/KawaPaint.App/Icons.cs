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
        ["Wand"]       = ("M4 20 L13 11 L16 14 L7 20 M13 11 L16 14 M17 4 L17 7 M17 9 L17 12 M13.5 5.5 L15.5 7.5 M18.5 8.5 L20.5 10.5 M20.5 5.5 L18.5 7.5 M15.5 8.5 L13.5 10.5", false),

        // Top-right panel-visibility toggles.
        ["PanelTools"]      = ("M4 4 L10 4 L10 10 L4 10 Z M14 4 L20 4 L20 10 L14 10 Z M4 14 L10 14 L10 20 L4 20 Z M14 14 L20 14 L20 20 L14 20 Z", false),
        ["PanelColors"]     = ("M12 3 C12 3 6 10.5 6 14.5 a6 6 0 0 0 12 0 C18 10.5 12 3 12 3 Z", false),
        ["PanelColorWheel"] = ("M12 4 a8 8 0 1 0 0.1 0 Z M12 4 L12 20 M4 12 L20 12", false),
        ["PanelLayers"]     = ("M12 3 L20 8 L12 13 L4 8 Z M4 12 L12 17 L20 12 M4 16 L12 21 L20 16", false),
        ["PanelHistory"]    = ("M12 4 a8 8 0 1 1-6.9 4 M5 3 L5 8 L10 8 M12 8 L12 12 L15 14", false),
        ["PanelDock"]       = ("M4 4 L20 4 L20 20 L4 20 Z M4 9 L20 9 M8 9 L8 20", false),

        // Per-panel float/dock toggle.
        ["Float"] = ("M4 10 L4 20 L14 20 L14 14 M9 15 L20 4 M13 4 L20 4 L20 11", false),

        // File menu.
        ["New"]         = ("M6 3 L14 3 L18 7 L18 21 L6 21 Z M14 3 L14 7 L18 7", false),
        ["Open"]        = ("M3 6 L9 6 L11 8 L21 8 L21 18 L3 18 Z", false),
        ["OpenProject"] = ("M3 6 L9 6 L11 8 L21 8 L21 18 L3 18 Z M9 12 L15 12 L15 16 L9 16 Z", false),
        ["Save"]        = ("M5 4 L16 4 L20 8 L20 20 L5 20 Z M8 4 L8 9 L16 9 L16 4 M7 20 L7 13 L17 13 L17 20", false),
        ["Export"]      = ("M4 15 L4 20 L20 20 L20 15 M12 14 L12 3 M7 8 L12 3 L17 8", false),
        ["Exit"]        = ("M14 4 L6 4 L6 20 L14 20 M9 12 L20 12 M16 8 L20 12 L16 16", false),

        // Edit menu.
        ["Undo"] = ("M7 7 L3 11 L7 15 M3 11 L15 11 a6 6 0 1 1 -4.5 10", false),
        ["Redo"] = ("M17 7 L21 11 L17 15 M21 11 L9 11 a6 6 0 1 0 4.5 10", false),

        // Select menu.
        ["SelectAll"] = ("M4 9 L4 4 L9 4 M15 4 L20 4 L20 9 M20 15 L20 20 L15 20 M9 20 L4 20 L4 15", false),
        ["InvertSel"] = ("M5 5 L19 5 L5 19 L19 19 Z", false),
        ["Deselect"]  = ("M4 4 L20 4 L20 20 L4 20 Z M8 8 L16 16 M16 8 L8 16", false),

        // View menu.
        ["ZoomIn"]     = ("M11 4 a7 7 0 1 0 0.1 0 Z M16 16 L21 21 M11 8 L11 14 M8 11 L14 11", false),
        ["ZoomOut"]    = ("M11 4 a7 7 0 1 0 0.1 0 Z M16 16 L21 21 M8 11 L14 11", false),
        ["ZoomFit"]    = ("M4 10 L4 4 L10 4 M4 4 L9 9 M14 4 L20 4 L20 10 M20 4 L15 9 M20 14 L20 20 L14 20 M20 20 L15 15 M10 20 L4 20 L4 14 M4 20 L9 15", false),
        ["ZoomActual"] = ("M11 4 a7 7 0 1 0 0.1 0 Z M16 16 L21 21 M8.5 8.5 L13.5 8.5 L13.5 13.5 L8.5 13.5 Z", false),

        // Image menu.
        ["Resize"]    = ("M4 4 L20 4 L20 20 L4 20 Z M9 15 L15 9 M9 15 L9 11 M9 15 L13 15 M15 9 L15 13 M15 9 L11 9", false),
        ["CanvasSize"] = ("M3 3 L21 3 L21 21 L3 21 Z M8 8 L16 8 L16 16 L8 16 Z", false),
        ["Crop"]      = ("M6 2 L6 18 L22 18 M2 6 L18 6 L18 22", false),
        ["FlipH"]     = ("M12 3 L12 21 M4 12 L8 8 L8 16 Z M20 12 L16 8 L16 16 Z", false),
        ["FlipV"]     = ("M3 12 L21 12 M12 4 L8 8 L16 8 Z M12 20 L8 16 L16 16 Z", false),
        ["RotateCW"]  = ("M20 8 a8 8 0 1 0 0.5 6 M20 4 L20 8 L16 8", false),
        ["RotateCCW"] = ("M4 8 a8 8 0 1 1 -0.5 6 M4 4 L4 8 L8 8", false),
        ["Flatten"]   = ("M12 3 L20 8 L12 13 L4 8 Z M12 13 L12 19 M8 16 L12 20 L16 16", false),

        // Effects & adjustments menu.
        ["EffectInvert"] = ("M12 4 a8 8 0 1 0 0.1 0 Z M12 4 L12 20", false),
        ["EffectGray"]   = ("M12 3 C8 9 5 13 5 16 a7 7 0 0 0 14 0 C19 13 16 9 12 3 Z M4 20 L20 4", false),
        ["EffectSepia"]  = ("M12 8 a4 4 0 1 0 0.1 0 Z M12 5 L12 2 M12 22 L12 19 M5 12 L2 12 M22 12 L19 12 M7 7 L5 5 M17 17 L19 19 M17 7 L19 5 M7 17 L5 19", false),
        ["Adjust"]       = ("M4 8 L20 8 M14 5 L14 11 M4 16 L20 16 M9 13 L9 19", false),
        ["HueSat"]       = ("M12 4 a8 8 0 1 0 0.1 0 Z M12 4 L12 12 L18 8 Z", false),
        ["Levels"]       = ("M4 20 L4 6 L12 16 L20 4 M4 20 L20 20", false),
        ["Curves"]       = ("M4 20 L4 4 M4 20 L20 20 M5 19 C9 19 8 9 12 9 C16 9 15 5 19 5", false),
        ["AutoLevels"]   = ("M4 20 L20 20 M5 20 L5 10 M9 20 L9 6 M13 20 L13 13 M17 20 L17 8", false),
        ["Blur"]         = ("M12 5 a7 7 0 1 0 0.1 0 Z", false),
        ["Sharpen"]      = ("M12 3 L14 10 L21 10 L15 14 L17 21 L12 16 L7 21 L9 14 L3 10 L10 10 Z", false),
        ["Emboss"]       = ("M6 10 L16 10 L16 20 L6 20 Z M9 6 L19 6 L19 16", false),
        ["EdgeDetect"]   = ("M4 4 L20 4 L20 20 L4 20 Z M4 4 L4 11 M4 4 L11 4", false),
        ["Posterize"]    = ("M4 20 L4 14 L9 14 L9 8 L14 8 L14 4 L20 4", false),
        ["Noise"]        = ("M6 6 L6.05 6 M12 5 L12.05 5 M18 7 L18.05 7 M5 12 L5.05 12 M11 11 L11.05 11 M17 13 L17.05 13 M7 17 L7.05 17 M13 18 L13.05 18 M19 17 L19.05 17", false),

        // Layers panel.
        ["Plus"]        = ("M12 5 L12 19 M5 12 L19 12", false),
        ["Copy"]        = ("M6 6 L16 6 L16 16 L6 16 Z M8 8 L18 8 L18 18 L8 18 Z", false),
        ["Cut"]         = ("M9 9 L20 20 M20 9 L14 15 M9.5 14.5 L4 20 M6.5 6.5 a2.5 2.5 0 1 0 0.05 0 Z M6.5 17.5 a2.5 2.5 0 1 0 0.05 0 Z M9 9 L6.9 8 M9.5 14.5 L6.9 15.5", false),
        ["Paste"]       = ("M9 4 L15 4 L15 6 L9 6 Z M6 6 L18 6 L18 21 L6 21 Z M9 11 L15 11 M9 15 L15 15", false),
        ["Trash"]       = ("M5 7 L19 7 M9 7 L9 4 L15 4 L15 7 M7 7 L7 20 L17 20 L17 7 M10 10 L10 17 M14 10 L14 17", false),
        ["Settings"]    = ("M12 8 a4 4 0 1 0 0.1 0 Z M12 2 L12 5 M12 19 L12 22 M2 12 L5 12 M19 12 L22 12 M4.9 4.9 L7 7 M17 17 L19.1 19.1 M19.1 4.9 L17 7 M7 17 L4.9 19.1", false),
        ["MergeDown"]   = ("M8 4 L8 10 L12 14 L16 10 L16 4 M12 14 L12 20", false),
        ["ChevronUp"]   = ("M6 15 L12 9 L18 15", false),
        ["ChevronDown"] = ("M6 9 L12 15 L18 9", false),

        // Misc.
        ["Swap"]  = ("M4 8 L16 8 M12 4 L16 8 L12 12 M20 16 L8 16 M12 12 L8 16 L12 20", false),
        ["Close"] = ("M5 5 L19 19 M19 5 L5 19", false),
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
