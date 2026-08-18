// KawaPaint — a horizontal or vertical ruler bar along the canvas edge. Ticks are placed by
// RulerMath (pure, unit-testable) against the SurfaceView's current Zoom/Origin, so the ruler
// always agrees with what's actually on screen.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KawaPaint.Engine;

namespace KawaPaint.App;

public enum RulerOrientation { Horizontal, Vertical }

public sealed class RulerBar : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly IBrush OutOfCanvas = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly IPen MajorTick = new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), 1);
    private static readonly IPen MinorTick = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly IPen CursorMarker = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30)), 1);
    private static readonly Typeface LabelFace = new("monospace");

    public RulerOrientation Orientation { get; init; } = RulerOrientation.Horizontal;

    /// <summary>The SurfaceView this ruler tracks. Set once; the ruler reads Zoom/Origin/Document
    /// from it on every render rather than caching copies that could drift out of sync.</summary>
    public SurfaceView? Target { get; set; }

    public RulerUnit Unit { get; set; } = RulerUnit.Pixels;

    /// <summary>Image-space cursor position along this ruler's axis, or null to hide the marker.</summary>
    public double? CursorPosition { get; set; }

    public RulerBar() => ClipToBounds = true;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Background, new Rect(Bounds.Size));

        if (Target is not { Document: { } doc } target) return;

        bool horizontal = Orientation == RulerOrientation.Horizontal;
        double zoom = target.Zoom;
        double originComponent = horizontal ? target.Origin.X : target.Origin.Y;
        double length = horizontal ? Bounds.Width : Bounds.Height;
        double canvasExtentPx = horizontal ? doc.Width : doc.Height;
        double dpi = doc.Dpi;
        double pxPerUnit = RulerMath.PixelsPerUnit(Unit, dpi);

        // Shade the region before image-pixel 0 and past the canvas edge, so the ruler visually
        // anchors where the actual document is.
        double canvasStartScreen = originComponent;
        double canvasEndScreen = originComponent + canvasExtentPx * zoom;
        DrawOutOfCanvasBand(context, horizontal, 0, Math.Max(0, canvasStartScreen), length);
        DrawOutOfCanvasBand(context, horizontal, Math.Min(length, canvasEndScreen), length, length);

        // Visible range, converted from screen space to unit space.
        double startUnit = (0 - originComponent) / zoom / pxPerUnit;
        double endUnit = (length - originComponent) / zoom / pxPerUnit;
        if (endUnit < startUnit) (startUnit, endUnit) = (endUnit, startUnit);

        double majorStep = RulerMath.MajorStep(Unit, dpi, zoom);
        int minorCount = RulerMath.MinorSubdivisions(Unit);
        double minorStep = majorStep / minorCount;

        double firstMajor = Math.Floor(startUnit / majorStep) * majorStep;
        // A little headroom avoids clipping the first/last tick's label.
        for (double u = firstMajor; u <= endUnit + majorStep; u += majorStep)
        {
            DrawTick(context, horizontal, u, pxPerUnit, zoom, originComponent, length, major: true);

            for (int i = 1; i < minorCount; i++)
            {
                double mu = u + i * minorStep;
                if (mu < startUnit - minorStep || mu > endUnit + minorStep) continue;
                DrawTick(context, horizontal, mu, pxPerUnit, zoom, originComponent, length, major: false);
            }
        }

        if (CursorPosition is double cursor)
        {
            double screenPos = originComponent + cursor * zoom;
            if (screenPos >= 0 && screenPos <= length)
            {
                var line = horizontal
                    ? new[] { new Point(screenPos, 0), new Point(screenPos, Bounds.Height) }
                    : new[] { new Point(0, screenPos), new Point(Bounds.Width, screenPos) };
                context.DrawLine(CursorMarker, line[0], line[1]);
            }
        }
    }

    private void DrawOutOfCanvasBand(DrawingContext context, bool horizontal, double from, double to, double clampTo)
    {
        from = Math.Clamp(from, 0, clampTo);
        to = Math.Clamp(to, 0, clampTo);
        if (to <= from) return;

        var rect = horizontal
            ? new Rect(from, 0, to - from, Bounds.Height)
            : new Rect(0, from, Bounds.Width, to - from);
        context.FillRectangle(OutOfCanvas, rect);
    }

    private void DrawTick(DrawingContext context, bool horizontal, double unitValue, double pxPerUnit,
                           double zoom, double originComponent, double length, bool major)
    {
        double screenPos = originComponent + unitValue * pxPerUnit * zoom;
        if (screenPos < -1 || screenPos > length + 1) return;

        double barThickness = horizontal ? Bounds.Height : Bounds.Width;
        double tickLen = major ? barThickness * 0.6 : barThickness * 0.3;

        var pen = major ? MajorTick : MinorTick;
        var p1 = horizontal ? new Point(screenPos, barThickness - tickLen) : new Point(barThickness - tickLen, screenPos);
        var p2 = horizontal ? new Point(screenPos, barThickness) : new Point(barThickness, screenPos);
        context.DrawLine(pen, p1, p2);

        if (!major) return;

        string text = RulerMath.FormatLabel(Unit, unitValue);
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, 9, Label);

        if (horizontal)
            context.DrawText(formatted, new Point(screenPos + 2, 1));
        else
        {
            // Vertical labels are drawn sideways so a wide ruler bar isn't needed to fit them.
            using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(barThickness - 2, screenPos - 2)))
                context.DrawText(formatted, new Point(-formatted.Width, -formatted.Height));
        }
    }
}
