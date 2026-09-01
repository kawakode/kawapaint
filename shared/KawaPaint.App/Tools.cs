// KawaPaint - pluggable tools. Each tool receives a ToolContext per pointer event with the
// active layer, image-space coordinates, current color/size, and callbacks back into the view
// (history snapshot, recomposite, color sampling). Engine algorithms do the actual pixel work.

using System;
using System.Collections.Generic;
using KawaPaint.Engine;

namespace KawaPaint.App;

[Flags]
public enum PressureMapping
{
    None = 0,
    Size = 1,
    Opacity = 2,
    SizeAndOpacity = Size | Opacity
}

public enum ToolPointerKind { Mouse, Pen, Touch }

public readonly record struct ToolPointerSample(double X, double Y, double Pressure = 1,
    double XTilt = 0, double YTilt = 0, double Twist = 0,
    ToolPointerKind Kind = ToolPointerKind.Mouse, bool IsEraser = false);

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
    public required PressureMapping PressureResponse { get; init; }
    public required ToolPointerKind PointerKind { get; init; }
    public required bool IsEraser { get; init; }

    /// <summary>Identifies which document these coordinates belong to. Changes whenever a different
    /// document is loaded or a canvas-level op (crop/resize/rotate/flatten) replaces it, so a tool
    /// holding image coordinates across gestures can tell they've gone stale. See CloneStampTool.</summary>
    public required int DocumentVersion { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Pressure { get; set; } = 1;
    public double XTilt { get; set; }
    public double YTilt { get; set; }
    public double Twist { get; set; }
    public int IX => (int)Math.Round(X);
    public int IY => (int)Math.Round(Y);

    public double PressureRadius => Math.Max(0.5, BrushWidth / 2.0 *
        (PressureResponse.HasFlag(PressureMapping.Size) ? Math.Clamp(Pressure, 0.01, 1) : 1));

    public double PressureOpacity => PressureResponse.HasFlag(PressureMapping.Opacity)
        ? Math.Clamp(Pressure, 0.01, 1) : 1;

    public required Action PushHistory { get; init; }
    public required Action Composite { get; init; }
    public required Action<int, int, int, int> CompositeRect { get; init; }
    public required Func<int, int, ColorBgra> SampleComposite { get; init; }
    public required Action<ColorBgra> SetPrimaryColor { get; init; }

    /// <summary>Image pixels per screen pixel (1/zoom). A tool that hit-tests against on-screen
    /// furniture - the pen's anchors and handles - scales its click radius by this so a node is
    /// exactly as easy to grab zoomed out to 10% as it is at 800%.</summary>
    public required double ViewScale { get; init; }

    /// <summary>Repaints the canvas overlay without recompositing any pixels. For tools that draw
    /// editing furniture over the image rather than into it.</summary>
    public required Action InvalidateOverlay { get; init; }

    public required Selection Selection { get; init; }
    public required Action SelectionChanged { get; init; }
    public required Action<int, int> RequestText { get; init; }
    public required Action<int, int> RequestDynamicText { get; init; }
    public required SelectionCombineMode CombineMode { get; init; }

    public void CompositeStroke(double x0, double y0, double x1, double y1, int extra = 0)
    {
        int margin = Math.Max(2, BrushWidth / 2 + 2 + extra);
        int left = (int)Math.Floor(Math.Min(x0, x1)) - margin;
        int top = (int)Math.Floor(Math.Min(y0, y1)) - margin;
        int right = (int)Math.Ceiling(Math.Max(x0, x1)) + margin + 1;
        int bottom = (int)Math.Ceiling(Math.Max(y0, y1)) + margin + 1;
        CompositeRect(left, top, right - left, bottom - top);
    }
}

public interface ITool
{
    string Name { get; }
    void PointerDown(ToolContext c);
    void PointerMove(ToolContext c);
    void PointerUp(ToolContext c);
}

/// <summary>
/// A tool whose on-canvas overlay follows the pointer even with no button down. Ordinary tools
/// only ever see a pointer that is mid-gesture; the pen needs the plain hover as well, to trail a
/// rubber band from the open end of its path to the cursor. The view drives this and repaints
/// whenever a call returns true.
/// </summary>
public interface IHoverTool : ITool
{
    /// <summary>Pointer moved over the canvas to image-space (x,y), no button down.
    /// <paramref name="viewScale"/> is image pixels per screen pixel. Returns true when the
    /// overlay changed and needs redrawing.</summary>
    bool PointerHover(double x, double y, double viewScale);

    /// <summary>The pointer left the canvas. Returns true when the overlay changed.</summary>
    bool PointerHoverExited();
}

/// <summary>Freehand pencil: alpha-blended round stroke on the active layer.</summary>
public sealed class PencilTool : ITool
{
    private SoftBrushStroke? _stroke;
    private double _lx, _ly, _lastRadius, _lastOpacity;
    public string Name => "Pencil";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _stroke = new SoftBrushStroke(c.Layer.Surface.Width, c.Layer.Surface.Height);
        _lx = c.X; _ly = c.Y;
        _lastRadius = c.PressureRadius;
        _lastOpacity = c.PressureOpacity;
        _stroke.Dab(c.X, c.Y, _lastRadius, 1, c.Antialias, _lastOpacity);
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.CompositeStroke(c.X, c.Y, c.X, c.Y);
    }

    public void PointerMove(ToolContext c)
    {
        if (_stroke is null) return;
        double oldX = _lx, oldY = _ly;
        double radius = c.PressureRadius, opacity = c.PressureOpacity;
        _stroke.DabLine(oldX, oldY, c.X, c.Y, _lastRadius, radius, 1, c.Antialias,
            _lastOpacity, opacity);
        _lastRadius = radius; _lastOpacity = opacity;
        _lx = c.X; _ly = c.Y;
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.CompositeStroke(oldX, oldY, c.X, c.Y);
    }

    public void PointerUp(ToolContext c) => _stroke = null;
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
    private double _lx, _ly, _lastRadius, _lastOpacity;
    public string Name => "Paintbrush";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _stroke = new SoftBrushStroke(c.Layer.Surface.Width, c.Layer.Surface.Height);
        _lx = c.X; _ly = c.Y;
        _lastRadius = c.PressureRadius;
        _lastOpacity = c.PressureOpacity;
        _stroke.Dab(c.X, c.Y, _lastRadius, c.BrushHardness, opacity: _lastOpacity);
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.CompositeStroke(c.X, c.Y, c.X, c.Y);
    }

    public void PointerMove(ToolContext c)
    {
        if (_stroke is null) return;   // move without a preceding down (tool switched mid-drag)

        double oldX = _lx, oldY = _ly;
        double radius = c.PressureRadius, opacity = c.PressureOpacity;
        _stroke.DabLine(oldX, oldY, c.X, c.Y, _lastRadius, radius, c.BrushHardness, true,
            _lastOpacity, opacity);
        _lastRadius = radius; _lastOpacity = opacity;
        _lx = c.X; _ly = c.Y;
        _stroke.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
        c.CompositeStroke(oldX, oldY, c.X, c.Y);
    }

    // The mask is a canvas-sized allocation; dropping it here keeps it off the heap between
    // strokes rather than for as long as the tool stays selected.
    public void PointerUp(ToolContext c) => _stroke = null;
}

/// <summary>Eraser: overwrites the active layer with transparency.</summary>
public sealed class EraserTool : ITool
{
    private SoftBrushStroke? _stroke;
    private double _lx, _ly, _lastRadius, _lastOpacity;
    public string Name => "Eraser";

    public void PointerDown(ToolContext c)
    {
        c.PushHistory();
        _stroke = new SoftBrushStroke(c.Layer.Surface.Width, c.Layer.Surface.Height);
        _lx = c.X; _ly = c.Y;
        _lastRadius = c.PressureRadius;
        _lastOpacity = c.PressureOpacity;
        _stroke.Dab(c.X, c.Y, _lastRadius, 1, c.Antialias, _lastOpacity);
        _stroke.FlushErase(c.Layer.Surface, c.PreStroke);
        c.CompositeStroke(c.X, c.Y, c.X, c.Y);
    }

    public void PointerMove(ToolContext c)
    {
        double oldX = _lx, oldY = _ly;
        if (_stroke is null) return;
        double radius = c.PressureRadius, opacity = c.PressureOpacity;
        _stroke.DabLine(oldX, oldY, c.X, c.Y, _lastRadius, radius, 1, c.Antialias,
            _lastOpacity, opacity);
        _lastRadius = radius; _lastOpacity = opacity;
        _stroke.FlushErase(c.Layer.Surface, c.PreStroke);
        _lx = c.X; _ly = c.Y;
        c.CompositeStroke(oldX, oldY, c.X, c.Y);
    }

    public void PointerUp(ToolContext c) => _stroke = null;
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
    private (int X, int Y, int Width, int Height)? _previousBounds;
    public abstract string Name { get; }
    protected virtual bool PreviewCoversWholeSurface => false;

    public void PointerDown(ToolContext c)
    {
        _sx = c.X; _sy = c.Y;
        _pushed = false;
        _previousBounds = null;
    }

    public void PointerMove(ToolContext c)
    {
        // History is taken on the first drag rather than on the press: a click that never moves
        // draws nothing, and would otherwise leave an undo step that reverses nothing. The layer
        // is still untouched at this point, so the snapshot is the true pre-shape state.
        if (!_pushed) { c.PushHistory(); _pushed = true; }

        if (PreviewCoversWholeSurface)
        {
            c.Layer.Surface.CopyFrom(c.PreStroke);
        }
        else if (_previousBounds is { } previous)
        {
            c.Layer.Surface.CopyRectFrom(c.PreStroke,
                previous.X, previous.Y, previous.Width, previous.Height);
        }

        Draw(c, _sx, _sy, c.X, c.Y);
        (int X, int Y, int Width, int Height)? currentBounds = PreviewCoversWholeSurface
            ? null : BoundsFor(c, _sx, _sy, c.X, c.Y);
        if (PreviewCoversWholeSurface)
        {
            c.Composite();
        }
        else if (currentBounds is { } current)
        {
            int left = _previousBounds is { } previous ? Math.Min(previous.X, current.X) : current.X;
            int top = _previousBounds is { } previousTop ? Math.Min(previousTop.Y, current.Y) : current.Y;
            int right = _previousBounds is { } previousRight
                ? Math.Max(previousRight.X + previousRight.Width, current.X + current.Width)
                : current.X + current.Width;
            int bottom = _previousBounds is { } previousBottom
                ? Math.Max(previousBottom.Y + previousBottom.Height, current.Y + current.Height)
                : current.Y + current.Height;
            c.CompositeRect(left, top, right - left, bottom - top);
        }
        _previousBounds = currentBounds;
    }

    public void PointerUp(ToolContext c) { }

    protected abstract void Draw(ToolContext c, double x0, double y0, double x1, double y1);

    private static (int X, int Y, int Width, int Height) BoundsFor(
        ToolContext c, double x0, double y0, double x1, double y1)
    {
        int margin = Math.Max(2, (int)Math.Ceiling(c.BrushWidth / 2.0) + 2);
        int left = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1)) - margin);
        int top = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1)) - margin);
        int right = Math.Min(c.Layer.Surface.Width, (int)Math.Ceiling(Math.Max(x0, x1)) + margin + 1);
        int bottom = Math.Min(c.Layer.Surface.Height, (int)Math.Ceiling(Math.Max(y0, y1)) + margin + 1);
        return (left, top, right - left, bottom - top);
    }
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
    protected override bool PreviewCoversWholeSurface => true;
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

/// <summary>Places or edits a non-destructive CSV-backed text zone.</summary>
public sealed class DynamicTextTool : ITool
{
    public string Name => "Dynamic Text";
    public void PointerDown(ToolContext c) => c.RequestDynamicText(c.IX, c.IY);
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
        Select(_shape, x0, y0, x1, y1, c.Antialias);

        c.Selection.CopyFrom(_base);
        c.Selection.Combine(c.CombineMode, _shape);
        c.SelectionChanged();
    }

    protected abstract void Select(Selection sel, double x0, double y0, double x1, double y1, bool antialias);
}

public sealed class RectSelectTool : SelectToolBase
{
    public override string Name => "Rectangle Select";
    protected override void Select(Selection sel, double x0, double y0, double x1, double y1, bool antialias)
        => sel.ReplaceWithRectangle(x0, y0, x1, y1, antialias);
}

public sealed class EllipseSelectTool : SelectToolBase
{
    public override string Name => "Ellipse Select";
    protected override void Select(Selection sel, double x0, double y0, double x1, double y1, bool antialias)
        => sel.ReplaceWithEllipse(x0, y0, x1, y1, antialias);
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
        var previous = _points[^1];
        _points.Add((c.X, c.Y));
        if (_points.Count >= 3 && _shape is not null)
        {
            _shape.TogglePolygon(new[] { _points[0], previous, _points[^1] });
            Apply(c);
        }
    }

    public void PointerUp(ToolContext c)
    {
        if (_points.Count < 3 && c.CombineMode == SelectionCombineMode.Replace)
        {
            c.Selection.SelectNone();
        }
        else if (c.Antialias && _points.Count >= 3 && _base is not null && _shape is not null)
        {
            // The incremental fan is an XOR parity trick (see Selection.TogglePolygon) and has no
            // way to carry coverage, so the drag previews a hard edge and the finished outline is
            // re-rasterized once here - the only pass that actually needs to be antialiased.
            _shape.ReplaceWithPolygon(_points, antialias: true);
            c.Selection.CopyFrom(_base);
            c.Selection.Combine(c.CombineMode, _shape);
        }
        // Otherwise every vertex was already incorporated incrementally by PointerMove.

        c.SelectionChanged();
        _base = null;
        _shape = null;
    }

    private void Apply(ToolContext c)
    {
        if (_base is null || _shape is null) return;

        c.Selection.CopyFrom(_base);
        c.Selection.Combine(c.CombineMode, _shape);
        c.SelectionChanged();
    }
}

/// <summary>
/// One node of a pen path, in image space: the on-curve anchor plus the two off-curve control
/// points that shape the segments arriving at and leaving it. A corner is simply a node whose
/// controls sit on top of the anchor, so a path of pure corners flattens to a plain polygon.
/// </summary>
public readonly record struct PenAnchor(double X, double Y, double InX, double InY, double OutX, double OutY)
{
    public static PenAnchor Corner(double x, double y) => new(x, y, x, y, x, y);

    public bool IsCorner => InX == X && InY == Y && OutX == X && OutY == Y;

    /// <summary>Pulls the outgoing control to (x,y) and mirrors the incoming one through the
    /// anchor - the symmetric node a handle drag produces, which is what keeps a curve smooth
    /// across the anchor.</summary>
    public PenAnchor WithSmoothHandle(double x, double y)
        => this with { OutX = x, OutY = y, InX = 2 * X - x, InY = 2 * Y - y };

    /// <summary>Moves one control on its own, leaving the other where it is, so the node becomes
    /// a cusp. This is what Ctrl-dragging a handle does.</summary>
    public PenAnchor WithBrokenHandle(bool outgoing, double x, double y)
        => outgoing ? this with { OutX = x, OutY = y } : this with { InX = x, InY = y };

    /// <summary>Drags the anchor and both its controls together, so moving a node reshapes its two
    /// segments without changing the curvature it was given.</summary>
    public PenAnchor MovedTo(double x, double y)
    {
        double dx = x - X, dy = y - Y;
        return new PenAnchor(x, y, InX + dx, InY + dy, OutX + dx, OutY + dy);
    }
}

/// <summary>
/// Pen ("Plume" in Photoshop): a Bezier outline laid down point by point and then turned into a
/// selection - the precise cut-out the freehand lasso can't give you. Click for a corner, or
/// click-drag to pull a smooth node's handles out of it; clicking the first anchor again closes
/// the outline and combines it into the selection through the usual replace/add/subtract/intersect
/// mode. An anchor that is already down can be dragged to move it, its handles re-dragged to
/// re-shape the curve, and Ctrl-clicking one deletes it.
///
/// Unlike every other tool here the path deliberately outlives the pointer gesture that started it
/// - placing points one at a time and correcting them before committing is the whole point - so
/// the state lives on the tool instance rather than in the per-gesture ToolContext, and SurfaceView
/// draws it as an overlay between gestures. Selecting another tool drops an unfinished path.
/// </summary>
public sealed class PenTool : ITool, IHoverTool
{
    private enum Grab { None, Anchor, InHandle, OutHandle }

    private readonly List<PenAnchor> _anchors = new();
    private Grab _grab;
    private int _grabIndex = -1;
    private bool _breakHandle;
    private (double X, double Y)? _hover;

    /// <summary>Click radius for anchors and handles, in screen pixels: scaled by the view so a
    /// node stays exactly as easy to grab zoomed out to 10% as it is at 800%.</summary>
    private const double GrabScreenRadius = 7;

    public string Name => "Pen";

    public IReadOnlyList<PenAnchor> Anchors => _anchors;

    public bool HasPath => _anchors.Count > 0;

    /// <summary>An outline needs three nodes before it encloses anything.</summary>
    public bool CanClose => _anchors.Count >= 3;

    /// <summary>Raised whenever an outline is committed to the selection. The host uses it for the
    /// status line: closing by clicking the first anchor happens entirely inside PointerDown, so
    /// there is otherwise no moment the view could notice it and say so.</summary>
    public Action? PathClosed { get; set; }

    /// <summary>Last hovered image-space point, for the rubber band drawn from the open end of the
    /// path to the cursor. Null while the pointer is off the canvas.</summary>
    public (double X, double Y)? Hover => _hover;

    /// <summary>True while the cursor sits over the first anchor of a closable path - the overlay
    /// highlights it so the click that closes the outline is visible before it happens.</summary>
    public bool HoverClosesPath { get; private set; }

    public void PointerDown(ToolContext c)
    {
        double grab = GrabScreenRadius * c.ViewScale;
        _grab = Grab.None;
        _grabIndex = -1;
        _breakHandle = c.CtrlHeld;

        // Closing takes priority over grabbing that same first anchor: with the path complete,
        // clicking where it started means "finish", not "nudge the point I started from".
        if (CanClose && Near(_anchors[0].X, _anchors[0].Y, c.X, c.Y, grab))
        {
            ApplyAsSelection(c.Selection, c.CombineMode, c.Antialias);
            c.SelectionChanged();
            c.InvalidateOverlay();
            return;
        }

        // Handles before anchors: a handle pulled right out of its anchor would otherwise be
        // unreachable, buried under the anchor's own hit circle.
        for (int i = _anchors.Count - 1; i >= 0; i--)
        {
            PenAnchor a = _anchors[i];
            if (!a.IsCorner && Near(a.OutX, a.OutY, c.X, c.Y, grab)) { Begin(Grab.OutHandle, i, c); return; }
            if (!a.IsCorner && Near(a.InX, a.InY, c.X, c.Y, grab)) { Begin(Grab.InHandle, i, c); return; }
        }

        for (int i = _anchors.Count - 1; i >= 0; i--)
        {
            if (!Near(_anchors[i].X, _anchors[i].Y, c.X, c.Y, grab)) continue;

            if (c.CtrlHeld) _anchors.RemoveAt(i);
            else Begin(Grab.Anchor, i, c);
            c.InvalidateOverlay();
            return;
        }

        // Nothing under the cursor: extend the path. The gesture that placed the node keeps
        // dragging its handle, so a click is a corner and a click-drag is a smooth node.
        _anchors.Add(PenAnchor.Corner(c.X, c.Y));
        Begin(Grab.OutHandle, _anchors.Count - 1, c);
    }

    public void PointerMove(ToolContext c)
    {
        if (_grab == Grab.None || (uint)_grabIndex >= (uint)_anchors.Count) return;

        PenAnchor a = _anchors[_grabIndex];
        _anchors[_grabIndex] = _grab switch
        {
            Grab.Anchor => a.MovedTo(c.X, c.Y),
            Grab.InHandle when _breakHandle => a.WithBrokenHandle(outgoing: false, c.X, c.Y),
            // Dragging the incoming handle of a symmetric node is the mirror of dragging its
            // outgoing one, so it goes through the same helper reflected about the anchor.
            Grab.InHandle => a.WithSmoothHandle(2 * a.X - c.X, 2 * a.Y - c.Y),
            Grab.OutHandle when _breakHandle => a.WithBrokenHandle(outgoing: true, c.X, c.Y),
            _ => a.WithSmoothHandle(c.X, c.Y)
        };

        _hover = (c.X, c.Y);
        c.InvalidateOverlay();
    }

    public void PointerUp(ToolContext c)
    {
        _grab = Grab.None;
        _grabIndex = -1;
        c.InvalidateOverlay();
    }

    public bool PointerHover(double x, double y, double viewScale)
    {
        _hover = (x, y);
        HoverClosesPath = CanClose && Near(_anchors[0].X, _anchors[0].Y, x, y, GrabScreenRadius * viewScale);
        // Any hover over a live path moves the rubber band, so the overlay always needs redrawing;
        // with no path there is nothing on screen that tracks the cursor.
        return HasPath;
    }

    public bool PointerHoverExited()
    {
        if (_hover is null && !HoverClosesPath) return false;
        _hover = null;
        HoverClosesPath = false;
        return true;
    }

    /// <summary>Throws the unfinished path away. Called when Escape is pressed.</summary>
    public void Clear()
    {
        _anchors.Clear();
        _grab = Grab.None;
        _grabIndex = -1;
        HoverClosesPath = false;
    }

    /// <summary>Drops the most recently placed node, so a misplaced point can be taken back
    /// without restarting the outline. Returns false when there was nothing left to drop.</summary>
    public bool RemoveLastAnchor()
    {
        if (_anchors.Count == 0) return false;
        _anchors.RemoveAt(_anchors.Count - 1);
        _grab = Grab.None;
        _grabIndex = -1;
        return true;
    }

    /// <summary>
    /// Closes the path, rasterizes it into <paramref name="selection"/> under
    /// <paramref name="mode"/>, and clears it ready for the next outline. Returns false - leaving
    /// both the path and the selection alone - when there are too few nodes to enclose an area.
    /// </summary>
    public bool ApplyAsSelection(Selection selection, SelectionCombineMode mode, bool antialias)
    {
        if (!CanClose) return false;

        var shape = new Selection(selection.Width, selection.Height);
        shape.ReplaceWithPolygon(Flatten(), antialias);
        selection.Combine(mode, shape);
        Clear();
        PathClosed?.Invoke();
        return true;
    }

    /// <summary>
    /// The closed outline as a polygon, each curved segment subdivided finely enough that its
    /// chords land within about a pixel of the true curve. Public because the smoke test measures
    /// the flattened area, and because it is the same geometry the overlay traces.
    /// </summary>
    public IReadOnlyList<(double X, double Y)> Flatten()
    {
        var points = new List<(double X, double Y)>();
        if (_anchors.Count == 0) return points;

        points.Add((_anchors[0].X, _anchors[0].Y));
        for (int i = 0; i < _anchors.Count; i++)
            AppendSegment(points, _anchors[i], _anchors[(i + 1) % _anchors.Count]);

        // The wrap-around segment ends back on the start point the list already opens with.
        if (points.Count > 1) points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>Point on the cubic between two nodes at parameter t. Shared by the flattener and
    /// anything else that needs to walk the curve.</summary>
    public static (double X, double Y) CurvePoint(PenAnchor a, PenAnchor b, double t)
    {
        double u = 1 - t;
        double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return (w0 * a.X + w1 * a.OutX + w2 * b.InX + w3 * b.X,
                w0 * a.Y + w1 * a.OutY + w2 * b.InY + w3 * b.Y);
    }

    private void Begin(Grab grab, int index, ToolContext c)
    {
        _grab = grab;
        _grabIndex = index;
        _hover = (c.X, c.Y);
        HoverClosesPath = false;
        c.InvalidateOverlay();
    }

    private static void AppendSegment(List<(double X, double Y)> into, PenAnchor a, PenAnchor b)
    {
        // Two corners in a row describe a straight edge; subdividing it would only add collinear
        // points for the polygon rasterizer to chew through.
        if (a.OutX == a.X && a.OutY == a.Y && b.InX == b.X && b.InY == b.Y)
        {
            into.Add((b.X, b.Y));
            return;
        }

        // The control polygon is an upper bound on the curve's length, so stepping it at roughly
        // one point per pixel is enough to keep every chord under a pixel of the real curve.
        double hull = Distance(a.X, a.Y, a.OutX, a.OutY)
                    + Distance(a.OutX, a.OutY, b.InX, b.InY)
                    + Distance(b.InX, b.InY, b.X, b.Y);
        int steps = Math.Clamp((int)Math.Ceiling(hull), 8, 256);

        for (int i = 1; i <= steps; i++) into.Add(CurvePoint(a, b, (double)i / steps));
    }

    private static bool Near(double x0, double y0, double x1, double y1, double radius)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static double Distance(double x0, double y0, double x1, double y1)
        => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
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
        c.CompositeStroke(c.X, c.Y, c.X, c.Y);
    }

    public void PointerMove(ToolContext c)
    {
        if (!_painting) return;
        double oldX = _lx, oldY = _ly;
        BrushOps.CloneLine(c.Layer.Surface, c.PreStroke, oldX, oldY, c.X, c.Y, _offsetX, _offsetY, c.BrushWidth / 2, c.Antialias);
        _lx = c.X; _ly = c.Y;
        c.CompositeStroke(oldX, oldY, c.X, c.Y);
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
        c.CompositeStroke(c.X, c.Y, c.X, c.Y);
    }

    public void PointerMove(ToolContext c)
    {
        double oldX = _lx, oldY = _ly;
        BrushOps.RecolorLine(c.Layer.Surface, oldX, oldY, c.X, c.Y, c.BrushWidth / 2, c.SecondaryColor, c.PrimaryColor, EffectiveTolerance(c), c.Antialias);
        _lx = c.X; _ly = c.Y;
        c.CompositeStroke(oldX, oldY, c.X, c.Y);
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
    private Selection? _fillMask;
    private SoftBrushStroke? _outline;
    private bool _pushed;
    public string Name => "Freeform Shape";

    public void PointerDown(ToolContext c)
    {
        _points.Clear();
        _points.Add((c.X, c.Y));
        _pushed = false;
        _fillMask = c.FillShapes ? new Selection(c.Layer.Surface.Width, c.Layer.Surface.Height) : null;
        _outline = c.FillShapes ? null : new SoftBrushStroke(c.Layer.Surface.Width, c.Layer.Surface.Height);
    }

    public void PointerMove(ToolContext c)
    {
        var previous = _points[^1];
        var current = (X: c.X, Y: c.Y);
        _points.Add(current);

        if (!_pushed) { c.PushHistory(); _pushed = true; }
        if (_fillMask is not null && _points.Count >= 3)
        {
            var dirty = _fillMask.TogglePolygon(new[] { _points[0], previous, current });
            _fillMask.PaintMask(c.Layer.Surface, c.PreStroke, c.PrimaryColor,
                dirty.X, dirty.Y, dirty.W, dirty.H);
            c.CompositeRect(dirty.X, dirty.Y, dirty.W, dirty.H);
        }
        else if (_outline is not null)
        {
            _outline.DabLine(previous.X, previous.Y, current.X, current.Y,
                Math.Max(0.5, c.BrushWidth / 2.0), 1, c.Antialias);
            _outline.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
            c.CompositeStroke(previous.X, previous.Y, current.X, current.Y);
        }
    }

    public void PointerUp(ToolContext c)
    {
        if (_outline is not null && _points.Count >= 3)
        {
            var last = _points[^1];
            var first = _points[0];
            _outline.DabLine(last.X, last.Y, first.X, first.Y,
                Math.Max(0.5, c.BrushWidth / 2.0), 1, c.Antialias);
            _outline.Flush(c.Layer.Surface, c.PreStroke, c.PrimaryColor);
            c.CompositeStroke(last.X, last.Y, first.X, first.Y);
        }
        _points.Clear();
        _fillMask = null;
        _outline = null;
    }
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
