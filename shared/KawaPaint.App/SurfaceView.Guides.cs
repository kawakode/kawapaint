// KawaPaint - the pixel grid and ruler guides drawn over the canvas. Guides are dragged out of a
// ruler (RulerBar forwards its drag here), moved with the Move tool, and removed by dropping them
// back off the canvas. Selection and shape tools snap to them.

using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace KawaPaint.App;

public sealed partial class SurfaceView
{
    /// <summary>The pixel grid only shows from this zoom up; below it the lines would swamp the image.</summary>
    public const double PixelGridMinZoom = 8;

    /// <summary>How close, in screen pixels, a pointer must come to a guide to snap to or grab it.</summary>
    private const double GuideReach = 6;

    private static readonly IPen PixelGridPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 72, 72, 72)), 1);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xC0, 0xFF)), 1);
    private static readonly IPen GuideDragPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x50, 0xC8)), 1);

    private bool _showPixelGrid = true;
    private bool _showGuides = true;
    private Guide? _draggedGuide;

    public GuideSet Guides { get; } = new();

    /// <summary>Snap selection and shape tools to guides.</summary>
    public bool SnapToGuides { get; set; } = true;

    public bool ShowPixelGrid
    {
        get => _showPixelGrid;
        set { _showPixelGrid = value; InvalidateVisual(); }
    }

    public bool ShowGuides
    {
        get => _showGuides;
        set
        {
            if (_showGuides == value) return;
            _showGuides = value;
            InvalidateVisual();
            GuidesChanged?.Invoke();
        }
    }

    /// <summary>Raised when guides are added, moved, removed, cleared or shown/hidden.</summary>
    public event Action? GuidesChanged;

    /// <summary>The guide a drag is moving right now, if any.</summary>
    public Guide? DraggedGuide => _draggedGuide;

    public void ClearGuides()
    {
        if (Guides.Count == 0) return;
        Guides.Clear();
        InvalidateVisual();
        GuidesChanged?.Invoke();
    }

    /// <summary>Starts dragging a new guide out of a ruler. <paramref name="controlPoint"/> is in
    /// this control's coordinates. Shows guides if they were hidden, since a new one would
    /// otherwise vanish the moment it is made.</summary>
    public void BeginGuideDrag(bool horizontal, Point controlPoint)
    {
        if (_document is null || _zoom <= 0) return;
        EndPointerGesture();
        ShowGuides = true;
        _draggedGuide = Guides.Add(horizontal, GuidePositionAt(horizontal, controlPoint));
        InvalidateVisual();
    }

    public void MoveGuideDrag(Point controlPoint)
    {
        if (_draggedGuide is null) return;
        _draggedGuide.Position = GuidePositionAt(_draggedGuide.Horizontal, controlPoint);
        InvalidateVisual();
    }

    /// <summary>Drops the dragged guide. Released outside this control (back on a ruler, say),
    /// it is removed.</summary>
    public void EndGuideDrag(Point controlPoint)
    {
        if (_draggedGuide is not { } guide) return;
        _draggedGuide = null;
        if (new Rect(Bounds.Size).Contains(controlPoint))
            guide.Position = GuidePositionAt(guide.Horizontal, controlPoint);
        else
            Guides.Remove(guide);
        InvalidateVisual();
        GuidesChanged?.Invoke();
    }

    /// <summary>Ends a drag that was interrupted (capture lost), leaving the guide where it is.</summary>
    public void FinishGuideDrag()
    {
        if (_draggedGuide is null) return;
        _draggedGuide = null;
        InvalidateVisual();
        GuidesChanged?.Invoke();
    }

    private int GuidePositionAt(bool horizontal, Point controlPoint)
    {
        Point image = ControlToImage(controlPoint);
        return (int)Math.Round(horizontal ? image.Y : image.X);
    }

    /// <summary>The guide under a control-space point that the Move tool would pick up.</summary>
    private Guide? GuideUnder(Point controlPoint)
    {
        if (!_showGuides || _zoom <= 0 || CurrentTool is not MoveTool) return null;
        Point image = ControlToImage(controlPoint);
        return Guides.HitTest(image.X, image.Y, GuideReach / _zoom);
    }

    /// <summary>Pulls an image-space point onto nearby guides for the tools that draw edges.</summary>
    private Point SnapToGuide(Point image)
    {
        if (!SnapToGuides || !_showGuides || Guides.Count == 0 || _zoom <= 0 ||
            CurrentTool is not (SelectToolBase or ShapeToolBase))
            return image;
        var (x, y) = Guides.Snap(image.X, image.Y, GuideReach / _zoom);
        return new Point(x, y);
    }

    private void UpdateGuideCursor(Point controlPoint)
    {
        Cursor? wanted = GuideUnder(controlPoint) is { } guide
            ? new Cursor(guide.Horizontal ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
            : null;
        if (wanted is null && Cursor is null) return;
        Cursor = wanted;
    }

    private void DrawPixelGrid(DrawingContext context, int width, int height)
    {
        if (!_showPixelGrid || _zoom < PixelGridMinZoom) return;
        int left = Math.Max(0, (int)Math.Floor(-_origin.X / _zoom));
        int top = Math.Max(0, (int)Math.Floor(-_origin.Y / _zoom));
        int right = Math.Min(width, (int)Math.Ceiling((Bounds.Width - _origin.X) / _zoom));
        int bottom = Math.Min(height, (int)Math.Ceiling((Bounds.Height - _origin.Y) / _zoom));
        if (right <= left || bottom <= top) return;

        double y0 = _origin.Y + top * _zoom, y1 = _origin.Y + bottom * _zoom;
        for (int x = left + 1; x < right; x++)
        {
            double sx = Math.Round(_origin.X + x * _zoom) + 0.5;
            context.DrawLine(PixelGridPen, new Point(sx, y0), new Point(sx, y1));
        }
        double x0 = _origin.X + left * _zoom, x1 = _origin.X + right * _zoom;
        for (int y = top + 1; y < bottom; y++)
        {
            double sy = Math.Round(_origin.Y + y * _zoom) + 0.5;
            context.DrawLine(PixelGridPen, new Point(x0, sy), new Point(x1, sy));
        }
    }

    private void DrawGuides(DrawingContext context)
    {
        if (!_showGuides) return;
        foreach (Guide guide in Guides.All)
        {
            IPen pen = ReferenceEquals(guide, _draggedGuide) ? GuideDragPen : GuidePen;
            if (guide.Horizontal)
            {
                double sy = Math.Round(_origin.Y + guide.Position * _zoom) + 0.5;
                context.DrawLine(pen, new Point(0, sy), new Point(Bounds.Width, sy));
            }
            else
            {
                double sx = Math.Round(_origin.X + guide.Position * _zoom) + 0.5;
                context.DrawLine(pen, new Point(sx, 0), new Point(sx, Bounds.Height));
            }
        }
    }
}
