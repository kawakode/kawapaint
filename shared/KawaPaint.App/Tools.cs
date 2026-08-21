// KawaPaint - pluggable tools. Each tool receives a ToolContext per pointer event with the
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

    /// <summary>Paintbrush edge falloff, 0 (fully soft) to 1 (hard). Only the paintbrush reads it;
    /// every other sized tool draws a hard-or-antialiased disc governed by <see cref="Antialias"/>.</summary>
    public required double BrushHardness { get; init; }

    public required bool Antialias { get; init; }
    public required int FillTolerance { get; init; }
    public required bool GlobalFill { get; init; }
    public required bool FillShapes { get; init; }
    public required bool CtrlHeld { get; init; }

    /// <summary>Identifies which document these coordinates belong to. Changes whenever a different
    /// document is loaded or a canvas-level op (crop/resize/rotate/flatten) replaces it, so a tool
    /// holding image coordinates across gestures can tell they've gone stale. See CloneStampTool.</summary>
    public required int DocumentVersion { get; init; }

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
    public required SelectionCombineMode CombineMode { get; init; }
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
        BrushOps.FillDisc(c.Layer.Surface, c.IX, c.IY, c.BrushWidth / 2, c.PrimaryColor, StampMode.Blend, c.Antialias);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        BrushOps.DrawLine(c.Layer.Surface, _lx, _ly, c.X, c.Y, c.BrushWidth / 2, c.PrimaryColor, StampMode.Blend, c.Antialias);
        _lx = c.X; _ly = c.Y;
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }
}

/// <summary>
/// Freehand paintbrush: a soft, size-and-hardness controlled round brush. Distinct from the pencil
/// in more than looks - the pencil stamps each dab straight onto the layer, whereas this one
/// accumulates the whole stroke into a <see cref="SoftBrushStroke"/> coverage mask and re-composites
/// it over the pointer-down snapshot, so a soft edge doesn't darken where dabs overlap.
/// </summary>
public sealed class PaintbrushTool : ITool
{
    private SoftBrushStroke? _stroke;
    private double _lx, _ly;
    public string Name => "Paintbrush";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _stroke = new SoftBrushStroke(c.Layer.Surface.Width, c.Layer.Surface.Height);
        _lx = c.X; _ly = c.Y;
        _stroke.Dab(c.X, c.Y, c.BrushWidth / 2.0, c.BrushHardness);
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        if (_stroke is null) return;   // move without a preceding down (tool switched mid-drag)

        _stroke.DabLine(_lx, _ly, c.X, c.Y, c.BrushWidth / 2.0, c.BrushHardness);
        _lx = c.X; _ly = c.Y;
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.Composite();
    }

    // The mask is a canvas-sized allocation; dropping it here keeps it off the heap between
    // strokes rather than for as long as the tool stays selected.
    public void PointerUp(ToolContext c) => _stroke = null;
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

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        if (c.GlobalFill)
            FloodFill.FillGlobal(c.Layer.Surface, c.IX, c.IY, c.PrimaryColor, c.FillTolerance);
        else
            FloodFill.Fill(c.Layer.Surface, c.IX, c.IY, c.PrimaryColor, c.FillTolerance);
        c.Composite();
    }

    public void PointerMove(ToolContext c) { }
    public void PointerUp(ToolContext c) { }
}

/// <summary>Selects the contiguous (or, with GlobalFill on, the whole-canvas) region matching the
/// clicked pixel - reuses the paint bucket's tolerance and global-fill toolbar controls, and
/// combines with the existing selection the same way the drag-select tools do.</summary>
public sealed class MagicWandTool : ITool
{
    public string Name => "Magic Wand";

    public void PointerDown(ToolContext c)
    {
        var shape = new Selection(c.Selection.Width, c.Selection.Height);
        if (c.GlobalFill)
            FloodFill.SelectGlobal(c.Layer.Surface, c.IX, c.IY, shape, c.FillTolerance);
        else
            FloodFill.Select(c.Layer.Surface, c.IX, c.IY, shape, c.FillTolerance);

        c.Selection.Combine(c.CombineMode, shape);
        c.SelectionChanged();
    }

    public void PointerMove(ToolContext c) { }
    public void PointerUp(ToolContext c) { }
}

/// <summary>Base for click-drag shapes with a live preview (revert to snapshot each move).</summary>
public abstract class ShapeToolBase : ITool
{
    private double _sx, _sy;
    private bool _pushed;
    public abstract string Name { get; }

    public void PointerDown(ToolContext c)
    {
        _sx = c.X; _sy = c.Y;
        _pushed = false;
    }

    public void PointerMove(ToolContext c)
    {
        // History is taken on the first drag rather than on the press: a click that never moves
        // draws nothing, and would otherwise leave an undo step that reverses nothing. The layer
        // is still untouched at this point, so the snapshot is the true pre-shape state.
        if (!_pushed) { c.PushHistory(); _pushed = true; }

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
        => BrushOps.DrawLine(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor, StampMode.Blend, c.Antialias);
}

public sealed class RectangleTool : ShapeToolBase
{
    public override string Name => "Rectangle";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
    {
        if (c.FillShapes) ShapeOps.FillRectangle(c.Layer.Surface, x0, y0, x1, y1, c.PrimaryColor);
        else ShapeOps.DrawRectangle(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
    }
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
    private bool _pushed;
    public string Name => "Move";

    public void PointerDown(ToolContext c) { _sx = c.X; _sy = c.Y; _pushed = false; }

    public void PointerMove(ToolContext c)
    {
        int dx = (int)Math.Round(c.X - _sx);
        int dy = (int)Math.Round(c.Y - _sy);
        if (!_pushed)
        {
            if (dx == 0 && dy == 0) return;      // nothing moved yet: no edit, no undo step
            c.PushHistory();
            _pushed = true;
        }

        // Once a gesture is underway, always re-shift from PreStroke - including back to (0,0) -
        // so dragging back to the start restores the original position instead of leaving the
        // surface at whatever the last non-zero offset was.
        SurfaceOps.ShiftInto(c.Layer.Surface, c.PreStroke, dx, dy);
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }
}

public sealed class EllipseTool : ShapeToolBase
{
    public override string Name => "Ellipse";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
    {
        if (c.FillShapes) ShapeOps.FillEllipse(c.Layer.Surface, x0, y0, x1, y1, c.PrimaryColor);
        else ShapeOps.DrawEllipse(c.Layer.Surface, x0, y0, x1, y1, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
    }
}

/// <summary>Base for drag-out selection tools (rectangle / ellipse).</summary>
/// <summary>
/// Shared drag-to-select behaviour for the rectangle/ellipse tools. A click-and-drag rasterizes
/// the shape into a scratch selection and combines it with whatever was selected when the drag
/// started (per ToolContext.CombineMode), so Add/Subtract/Intersect preview live while dragging
/// instead of only applying once the pointer is released.
/// </summary>
public abstract class SelectToolBase : ITool
{
    private double _sx, _sy;
    private Selection? _base;    // selection as it was before this drag
    private Selection? _shape;   // scratch: just the shape being dragged out
    public abstract string Name { get; }

    public void PointerDown(ToolContext c)
    {
        _sx = c.X;
        _sy = c.Y;
        _base = c.Selection.Clone();
        _shape = new Selection(c.Selection.Width, c.Selection.Height);
    }

    public void PointerMove(ToolContext c) => Apply(c, _sx, _sy, c.X, c.Y);

    public void PointerUp(ToolContext c)
    {
        bool zeroSize = Math.Abs(c.X - _sx) < 1 && Math.Abs(c.Y - _sy) < 1;
        if (zeroSize && c.CombineMode == SelectionCombineMode.Replace)
            c.Selection.SelectNone();     // a plain click clears the selection
        else if (!zeroSize)
            Apply(c, _sx, _sy, c.X, c.Y);
        // A zero-size drag in Add/Subtract/Intersect mode changes nothing - leave the base as is.

        c.SelectionChanged();
        _base = null;
        _shape = null;
    }

    private void Apply(ToolContext c, double x0, double y0, double x1, double y1)
    {
        if (_base is null || _shape is null) return;

        _shape.SelectNone();
        Select(_shape, x0, y0, x1, y1);

        c.Selection.CopyFrom(_base);
        c.Selection.Combine(c.CombineMode, _shape);
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

/// <summary>Freehand lasso selection. Combines against the pre-drag selection the same way the
/// rectangle/ellipse tools do (see SelectToolBase).</summary>
public sealed class LassoSelectTool : ITool
{
    private readonly List<(double X, double Y)> _points = new();
    private Selection? _base;
    private Selection? _shape;
    public string Name => "Lasso Select";

    public void PointerDown(ToolContext c)
    {
        _points.Clear();
        _points.Add((c.X, c.Y));
        _base = c.Selection.Clone();
        _shape = new Selection(c.Selection.Width, c.Selection.Height);
    }

    public void PointerMove(ToolContext c)
    {
        _points.Add((c.X, c.Y));
        if (_points.Count >= 3) Apply(c);
    }

    public void PointerUp(ToolContext c)
    {
        if (_points.Count < 3 && c.CombineMode == SelectionCombineMode.Replace)
            c.Selection.SelectNone();
        else if (_points.Count >= 3)
            Apply(c);

        c.SelectionChanged();
        _base = null;
        _shape = null;
    }

    private void Apply(ToolContext c)
    {
        if (_base is null || _shape is null) return;

        _shape.SelectNone();
        _shape.ReplaceWithPolygon(_points);

        c.Selection.CopyFrom(_base);
        c.Selection.Combine(c.CombineMode, _shape);
        c.SelectionChanged();
    }
}

/// <summary>
/// Clone Stamp: Ctrl+click sets the source point (no painting - a click that only sets the
/// anchor shouldn't leave an undo step). A plain click-drag afterward paints from that source,
/// re-anchoring the source-to-cursor offset at the start of each stroke so repeated strokes stay
/// relative to the same fixed source point rather than drifting. Samples from PreStroke (the
/// layer as it was before this stroke began) rather than the live surface, so painting over a
/// source area mid-stroke can't feed back into itself as a smear.
/// </summary>
public sealed class CloneStampTool : ITool
{
    private (int X, int Y)? _source;
    private int _sourceDocumentVersion = -1;
    private int _offsetX, _offsetY;
    private bool _painting;
    private double _lx, _ly;
    public string Name => "Clone Stamp";

    public void PointerDown(ToolContext c)
    {
        // The tool instance outlives any one document, so a source set in a previous document (or
        // before a crop/resize/rotate) names coordinates that mean nothing now. CloneDisc would
        // bounds-check the stale sample and paint nothing, leaving the tool looking broken with no
        // hint that the source needs re-setting - so drop it explicitly instead.
        if (_source is not null && _sourceDocumentVersion != c.DocumentVersion) _source = null;

        if (c.CtrlHeld)
        {
            _source = (c.IX, c.IY);
            _sourceDocumentVersion = c.DocumentVersion;
            _painting = false;
            return;
        }

        if (_source is not { } src) { _painting = false; return; }   // no source set yet

        _offsetX = src.X - c.IX;
        _offsetY = src.Y - c.IY;
        _painting = true;
        _lx = c.X; _ly = c.Y;

        c.PushHistory();
        BrushOps.CloneDisc(c.Layer.Surface, c.PreStroke, c.IX, c.IY, _offsetX, _offsetY, c.BrushWidth / 2, c.Antialias);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        if (!_painting) return;
        BrushOps.CloneLine(c.Layer.Surface, c.PreStroke, _lx, _ly, c.X, c.Y, _offsetX, _offsetY, c.BrushWidth / 2, c.Antialias);
        _lx = c.X; _ly = c.Y;
        c.Composite();
    }

    public void PointerUp(ToolContext c) => _painting = false;
}

/// <summary>
/// Recolor: brushes areas close to the background color over to the foreground color, adding the
/// Bg→Fg offset onto each pixel's actual value rather than flattening to a flat color, so
/// shading/antialiasing at the edge of what's being recolored carries through unscathed. Tolerance
/// is capped at the Fg/Bg color difference so a second pass over already-recolored pixels can't
/// keep "recoloring" them and drift - same guard paint.net's original Recolor tool uses.
/// </summary>
public sealed class RecolorTool : ITool
{
    private double _lx, _ly;
    public string Name => "Recolor";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _lx = c.X; _ly = c.Y;
        BrushOps.RecolorDisc(c.Layer.Surface, c.IX, c.IY, c.BrushWidth / 2, c.SecondaryColor, c.PrimaryColor, EffectiveTolerance(c), c.Antialias);
        c.Composite();
    }

    public void PointerMove(ToolContext c)
    {
        BrushOps.RecolorLine(c.Layer.Surface, _lx, _ly, c.X, c.Y, c.BrushWidth / 2, c.SecondaryColor, c.PrimaryColor, EffectiveTolerance(c), c.Antialias);
        _lx = c.X; _ly = c.Y;
        c.Composite();
    }

    public void PointerUp(ToolContext c) { }

    private static int EffectiveTolerance(ToolContext c)
    {
        ColorBgra a = c.PrimaryColor, b = c.SecondaryColor;
        int selfDiff = Math.Max(Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.A - b.A)));
        return Math.Min(c.FillTolerance, selfDiff);
    }
}

public sealed class RoundedRectangleTool : ShapeToolBase
{
    public override string Name => "Rounded Rectangle";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
    {
        double corner = Math.Max(8, c.BrushWidth * 2);
        if (c.FillShapes) ShapeOps.FillRoundedRectangle(c.Layer.Surface, x0, y0, x1, y1, corner, c.PrimaryColor);
        else ShapeOps.DrawRoundedRectangle(c.Layer.Surface, x0, y0, x1, y1, corner, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
    }
}

/// <summary>Freeform shape: accumulates points while dragging, like the lasso, but stamps a
/// filled/outlined polygon onto the layer at pointer-up instead of selecting.</summary>
public sealed class FreeformShapeTool : ITool
{
    private readonly List<(double X, double Y)> _points = new();
    private bool _pushed;
    public string Name => "Freeform Shape";

    public void PointerDown(ToolContext c)
    {
        _points.Clear();
        _points.Add((c.X, c.Y));
        _pushed = false;
    }

    public void PointerMove(ToolContext c)
    {
        _points.Add((c.X, c.Y));
        if (_points.Count < 2) return;

        if (!_pushed) { c.PushHistory(); _pushed = true; }
        c.Layer.Surface.CopyFrom(c.PreStroke);
        if (c.FillShapes) ShapeOps.FillPolygon(c.Layer.Surface, _points, c.PrimaryColor);
        else ShapeOps.DrawPolygon(c.Layer.Surface, _points, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
        c.Composite();
    }

    public void PointerUp(ToolContext c) => _points.Clear();
}

public sealed class StarTool : ShapeToolBase
{
    public override string Name => "Star";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
    {
        var points = StarPoints(x0, y0, x1, y1);
        if (c.FillShapes) ShapeOps.FillPolygon(c.Layer.Surface, points, c.PrimaryColor);
        else ShapeOps.DrawPolygon(c.Layer.Surface, points, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
    }

    /// <summary>Five-point star inscribed in the drag's bounding box, alternating outer/inner
    /// vertices at the golden-ratio radius that gives a regular star its points.</summary>
    private static List<(double X, double Y)> StarPoints(double x0, double y0, double x1, double y1)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double rx = Math.Abs(x1 - x0) / 2, ry = Math.Abs(y1 - y0) / 2;
        const int spikes = 5;
        const double innerRatio = 0.382;

        var points = new List<(double, double)>(spikes * 2);
        for (int i = 0; i < spikes * 2; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / spikes;
            double r = i % 2 == 0 ? 1.0 : innerRatio;
            points.Add((cx + Math.Cos(angle) * rx * r, cy + Math.Sin(angle) * ry * r));
        }
        return points;
    }
}

public sealed class ArrowTool : ShapeToolBase
{
    public override string Name => "Arrow";
    protected override void Draw(ToolContext c, double x0, double y0, double x1, double y1)
    {
        var points = ArrowPoints(x0, y0, x1, y1, c.BrushWidth);
        if (c.FillShapes) ShapeOps.FillPolygon(c.Layer.Surface, points, c.PrimaryColor);
        else ShapeOps.DrawPolygon(c.Layer.Surface, points, c.BrushWidth / 2, c.PrimaryColor, c.Antialias);
    }

    /// <summary>Seven-point arrow polygon: a shaft of half-width scaled to the brush, capped by a
    /// triangular head sized off that same shaft so thicker brushes draw proportionally bigger heads.</summary>
    private static List<(double X, double Y)> ArrowPoints(double x0, double y0, double x1, double y1, int brushWidth)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;

        double shaftHalf = Math.Max(2, brushWidth * 0.6);
        double headHalf = shaftHalf * 2.5;
        double headLen = Math.Min(len * 0.5, headHalf * 2);
        double bx = x1 - ux * headLen, by = y1 - uy * headLen;

        return new List<(double, double)>
        {
            (x0 + nx * shaftHalf, y0 + ny * shaftHalf),
            (bx + nx * shaftHalf, by + ny * shaftHalf),
            (bx + nx * headHalf, by + ny * headHalf),
            (x1, y1),
            (bx - nx * headHalf, by - ny * headHalf),
            (bx - nx * shaftHalf, by - ny * shaftHalf),
            (x0 - nx * shaftHalf, y0 - ny * shaftHalf),
        };
    }
}
