// KawaPaint — pluggable tools. Each tool receives a ToolContext per pointer event with the
// active layer, image-space coordinates, current color/size, and callbacks back into the view
// (history snapshot, recomposite, color sampling). Engine algorithms do the actual pixel work.

using System;
using System.Collections.Generic;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class ToolContext
{
    public required Layer Layer { get; init; }
    public required Surface PreStroke { get; init; }   // active layer snapshot at pointer-down
    public required ColorBgra PrimaryColor { get; init; }
    public required ColorBgra SecondaryColor { get; init; }
    public required int BrushWidth { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public int IX => (int)Math.Round(X);
    public int IY => (int)Math.Round(Y);

    public required Action PushHistory { get; init; }
    public required Action Composite { get; init; }
    public required Func<int, int, ColorBgra> SampleComposite { get; init; }
    public required Action<ColorBgra> SetPrimaryColor { get; init; }

    public required Selection Selection { get; init; }
    public required Action SelectionChanged { get; init; }
    public required Action<int, int> RequestText { get; init; }
}

public interface ITool
{
    string Name { get; }
    void PointerDown(ToolContext c);
    void PointerMove(ToolContext c);
    void PointerUp(ToolContext c);
}

/// <summary>Freehand pencil: alpha-blended round stroke on the active layer.</summary>
public sealed class PencilTool : ITool
{
    private double _lx, _ly;
    public string Name => "Pencil";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _lx = c.X; _ly = c.Y;
        BrushOps.FillDisc(c.Layer.Surface, c.IX, c.IY, c.BrushWidth / 2, c.PrimaryColor);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        BrushOps.DrawLine(c.Layer.Surface, _lx, _ly, c.X, c.Y, c.BrushWidth / 2, c.PrimaryColor);
        _lx = c.X; _ly = c.Y;
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }
}

/// <summary>Eraser: overwrites the active layer with transparency.</summary>
public sealed class EraserTool : ITool
{
    private double _lx, _ly;
    public string Name => "Eraser";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _lx = c.X; _ly = c.Y;
        BrushOps.FillDisc(c.Layer.Surface, c.IX, c.IY, c.BrushWidth / 2, ColorBgra.Transparent, StampMode.Set);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        BrushOps.DrawLine(c.Layer.Surface, _lx, _ly, c.X, c.Y, c.BrushWidth / 2, ColorBgra.Transparent, StampMode.Set);
        _lx = c.X; _ly = c.Y;
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }
}

/// <summary>Eyedropper: samples the composited image into the primary color.</summary>
public sealed class ColorPickerTool : ITool
{
    public string Name => "Color Picker";
    public void PointerDown(ToolContext c) => c.SetPrimaryColor(c.SampleComposite(c.IX, c.IY));
    public void PointerMove(ToolContext c) { }
    public void PointerUp(ToolContext c) { }
}

/// <summary>Paint bucket: contiguous flood fill on the active layer.</summary>
public sealed class PaintBucketTool : ITool
{
    public string Name => "Paint Bucket";
    public int Tolerance { get; set; } = 32;

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        FloodFill.Fill(c.Layer.Surface, c.IX, c.IY, c.PrimaryColor, Tolerance);
        c.Composite();
    }

    public void PointerMove(ToolContext c) { }
    public void PointerUp(ToolContext c) { }
}

/// <summary>Base for click-drag shapes with a live preview (revert to snapshot each move).</summary>
public abstract class ShapeToolBase : ITool
{
    private double _sx, _sy;
    public abstract string Name { get; }

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _sx = c.X; _sy = c.Y;
    }

    public void PointerMove(ToolContext c)
    {
        c.Layer.Surface.CopyFrom(c.PreStroke);   // discard previous preview
        Draw(c, _sx, _sy, c.X, c.Y);
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }

    protected abstract void Draw(ToolContext c, double x0, double y0, double x1, double y1);
}

public sealed class LineTool : ShapeToolBase
{
    public override string Name => "Line";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
        => BrushOps.DrawLine(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor);
}

public sealed class RectangleTool : ShapeToolBase
{
    public override string Name => "Rectangle";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
        => ShapeOps.DrawRectangle(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor);
}

public sealed class GradientTool : ShapeToolBase
{
    public override string Name => "Gradient";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
        => GradientOps.LinearGradient(c.Layer.Surface, x0, y0, x1, y1, c.PrimaryColor, c.SecondaryColor);
}

/// <summary>Text tool: a click asks the host to prompt for text and render it at that point.</summary>
public sealed class TextTool : ITool
{
    public string Name => "Text";
    public void PointerDown(ToolContext c) => c.RequestText(c.IX, c.IY);
    public void PointerMove(ToolContext c) { }
    public void PointerUp(ToolContext c) { }
}

/// <summary>Move tool: drags the whole active layer's content.</summary>
public sealed class MoveTool : ITool
{
    private double _sx, _sy;
    public string Name => "Move";

    public void PointerDown(ToolContext c) { c.PushHistory(); _sx = c.X; _sy = c.Y; }

    public void PointerMove(ToolContext c)
    {
        int dx = (int)Math.Round(c.X - _sx);
        int dy = (int)Math.Round(c.Y - _sy);
        SurfaceOps.ShiftInto(c.Layer.Surface, c.PreStroke, dx, dy);
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }
}

public sealed class EllipseTool : ShapeToolBase
{
    public override string Name => "Ellipse";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
        => ShapeOps.DrawEllipse(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor);
}

/// <summary>Base for drag-out selection tools (rectangle / ellipse).</summary>
public abstract class SelectToolBase : ITool
{
    private double _sx, _sy;
    public abstract string Name { get; }

    public void PointerDown(ToolContext c) { _sx = c.X; _sy = c.Y; }

    public void PointerMove(ToolContext c)
    {
        Select(c.Selection, _sx, _sy, c.X, c.Y);
        c.SelectionChanged();
    }

    public void PointerUp(ToolContext c)
    {
        if (Math.Abs(c.X - _sx) < 1 && Math.Abs(c.Y - _sy) < 1)
            c.Selection.SelectNone();     // a click clears the selection
        else
            Select(c.Selection, _sx, _sy, c.X, c.Y);
        c.SelectionChanged();
    }

    protected abstract void Select(Selection sel, double x0, double y0, double x1, double y1);
}

public sealed class RectSelectTool : SelectToolBase
{
    public override string Name => "Rectangle Select";
    protected override void Select(Selection sel, double x0, double y0, double x1, double y1)
        => sel.ReplaceWithRectangle(x0, y0, x1, y1);
}

public sealed class EllipseSelectTool : SelectToolBase
{
    public override string Name => "Ellipse Select";
    protected override void Select(Selection sel, double x0, double y0, double x1, double y1)
        => sel.ReplaceWithEllipse(x0, y0, x1, y1);
}

/// <summary>Freehand lasso selection.</summary>
public sealed class LassoSelectTool : ITool
{
    private readonly List<(double X, double Y)> _points = new();
    public string Name => "Lasso Select";

    public void PointerDown(ToolContext c) { _points.Clear(); _points.Add((c.X, c.Y)); }

    public void PointerMove(ToolContext c)
    {
        _points.Add((c.X, c.Y));
        if (_points.Count >= 3)
        {
            c.Selection.ReplaceWithPolygon(_points);
            c.SelectionChanged();
        }
    }

    public void PointerUp(ToolContext c)
    {
        if (_points.Count < 3) c.Selection.SelectNone();
        else c.Selection.ReplaceWithPolygon(_points);
        c.SelectionChanged();
    }
}
