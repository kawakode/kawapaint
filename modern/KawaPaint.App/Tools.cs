// KawaPaint — pluggable tools. Each tool receives a ToolContext per pointer event with the
// active layer, image-space coordinates, current color/size, and callbacks back into the view
// (history snapshot, recomposite, color sampling). Engine algorithms do the actual pixel work.

using System;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class ToolContext
{
    public required Layer Layer { get; init; }
    public required Surface PreStroke { get; init; }   // active layer snapshot at pointer-down
    public required ColorBgra PrimaryColor { get; init; }
    public required int BrushWidth { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public int IX => (int)Math.Round(X);
    public int IY => (int)Math.Round(Y);

    public required Action PushHistory { get; init; }
    public required Action Composite { get; init; }
    public required Func<int, int, ColorBgra> SampleComposite { get; init; }
    public required Action<ColorBgra> SetPrimaryColor { get; init; }
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

public sealed class EllipseTool : ShapeToolBase
{
    public override string Name => "Ellipse";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
        => ShapeOps.DrawEllipse(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor);
}
