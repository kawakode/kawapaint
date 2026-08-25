// KawaPaint - Avalonia canvas control. Displays a Document (flattened to a composite Surface)
// with zoom/pan, paints the pencil onto the active layer, and records undo/redo history.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.App;

public sealed class SurfaceView : Control
{
    private Document? _document;
    private Surface? _composite;
    private WriteableBitmap? _bitmap;
    private readonly Dictionary<DocumentFrame, int> _frameContentVersions = new();

    // Bumped every time a different document (or a crop/resize/rotate/flatten result) is adopted.
    // Tools that anchor state to image coordinates across gestures - Clone Stamp's source point -
    // compare against this to notice their anchor now refers to a canvas that no longer exists.
    private int _documentVersion;

    private int _antPhase;
    private DispatcherTimer? _antTimer;
    private StreamGeometry[] _antGeometry = Array.Empty<StreamGeometry>();

    public Selection? Selection { get; private set; }

    private double _zoom = 1.0;
    private Point _origin;
    private bool _panning;
    private Point _lastPointer;
    private bool _fitPending = true;

    private bool _drawing;
    private Point? _cursorScreen;

    public ColorBgra BrushColor { get; set; } = ColorBgra.Black;
    public ColorBgra SecondaryColor { get; set; } = ColorBgra.White;
    public int BrushWidth { get; set; } = 3;

    /// <summary>Paintbrush edge falloff, 0 (fully soft) to 1 (hard). See PaintbrushTool.</summary>
    public double BrushHardness { get; set; } = 0.75;

    public bool Antialias { get; set; } = true;
    public int FillTolerance { get; set; } = 32;
    public bool GlobalFill { get; set; }
    public bool FillShapes { get; set; }
    public SelectionCombineMode SelectionCombineMode { get; set; } = SelectionCombineMode.Replace;

    public ITool CurrentTool { get; set; } = new PencilTool();

    /// <summary>Raised by the color-picker tool with the sampled color.</summary>
    public event Action<ColorBgra>? PrimaryColorPicked;

    /// <summary>Raised by the text tool at the clicked image point (x,y).</summary>
    public event Action<int, int>? TextRequested;
    public event Action<int, int>? DynamicTextRequested;

    /// <summary>Raised as the pointer moves, with the image-space coordinate under it. Also raised
    /// once with <see cref="CursorGone"/> in both components when the pointer leaves the canvas.</summary>
    public event Action<int, int>? CursorMoved;

    /// <summary>Coordinate reported by <see cref="CursorMoved"/> when the pointer is no longer over
    /// the canvas at all. Subscribers need no special case for it: it is outside every possible
    /// document, which is already the "not over a pixel" state they render.</summary>
    public const int CursorGone = int.MinValue;

    private Surface? _preStroke;
    private ToolContext? _toolCtx;

    // A tool asks for history at the start of its gesture, before it has touched a pixel.
    // The step is only built at pointer-up, when the changed region is known and can be
    // captured as a tile delta rather than a whole-surface clone.
    private Layer? _pendingHistoryLayer;
    private string _pendingHistoryName = "Edit";

    public HistoryStack History { get; } = new();
    public Document? Document => _document;
    public Layer? ActiveLayer { get; private set; }

    /// <summary>Raised when the document or its layer list changes (for panels / status).</summary>
    public event EventHandler? DocumentChanged;

    public SurfaceView()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public double Zoom => _zoom;

    /// <summary>Screen-space position of image pixel (0,0). Combined with Zoom this is the whole
    /// image-to-screen transform; RulerBar uses it to place its ticks.</summary>
    public Point Origin => _origin;

    /// <summary>The canvas-pixel rectangle currently visible through this control. Live effects
    /// use it as their destination ROI; kernels remain free to sample outside it.</summary>
    public EffectBounds VisibleImageBounds
    {
        get
        {
            if (_document is null || _zoom <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
                return default;
            int left = (int)Math.Floor((0 - _origin.X) / _zoom);
            int top = (int)Math.Floor((0 - _origin.Y) / _zoom);
            int right = (int)Math.Ceiling((Bounds.Width - _origin.X) / _zoom);
            int bottom = (int)Math.Ceiling((Bounds.Height - _origin.Y) / _zoom);
            return new EffectBounds(left, top, right - left, bottom - top)
                .Clip(ActiveLayer?.Surface ?? _composite!);
        }
    }

    /// <summary>Raised whenever Zoom or Origin changes (fit, zoom in/out/actual, wheel-zoom, pan
    /// drag) - a ruler bar redraws on this rather than polling every frame.</summary>
    public event Action? ViewChanged;

    /// <summary>Loads a document as a fresh editing session: the old one is disposed and undo history is dropped.</summary>
    public void SetDocument(Document document)
    {
        Document? old = Adopt(document);
        old?.Dispose();
        History.Clear();
    }

    /// <summary>
    /// Swaps in a document produced by a canvas-level operation (crop/resize/rotate/flatten),
    /// keeping undo history. The outgoing document is returned, NOT disposed, so a memento can
    /// put it back.
    /// </summary>
    public Document? ReplaceDocument(Document document) => Adopt(document);

    private Document? Adopt(Document document)
    {
        Document? old = _document;
        _composite?.Dispose();
        _bitmap?.Dispose();

        _documentVersion++;
        _document = document;
        _frameContentVersions.Clear();
        foreach (DocumentFrame frame in document.Frames) _frameContentVersions[frame] = 0;
        ActiveLayer = document.LayerCount > 0 ? document.Layers[^1] : document.AddLayer();
        _composite = new Surface(document.Width, document.Height);
        Selection = new Selection(document.Width, document.Height);
        _bitmap = new WriteableBitmap(
            new PixelSize(document.Width, document.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        NotifySelectionChanged();   // stops the marching-ants timer for the discarded selection
        _fitPending = true;
        RenderCompositeCore(contentChanged: false);
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        return old;
    }

    /// <summary>Announces that layer pixels/order changed, so panels can refresh (thumbnails etc.).</summary>
    public void NotifyLayersChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

    public void SetActiveLayer(Layer layer)
    {
        if (_document is not null && _document.IndexOf(layer) >= 0)
        {
            ActiveLayer = layer;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Bumped every time the composite is re-rendered. Panels that derive something from layer
    /// pixels - the Layers panel's thumbnails - use it to tell a rebuild that needs new pixel work
    /// from one that does not: a DocumentChanged raised purely by selecting a different row leaves
    /// this alone. Safe as a cache key in that direction, because layer pixels cannot change
    /// *visibly* without a recomposite; the reverse is not true (a blend-mode or opacity change
    /// recomposites without touching any layer's own pixels), which costs a redundant refresh and
    /// never a stale one.
    /// </summary>
    public int CompositeVersion { get; private set; }

    /// <summary>Preview-cache key that is stable while frames are merely selected or played.</summary>
    public int FrameContentVersion(DocumentFrame frame) =>
        _frameContentVersions.TryGetValue(frame, out int version) ? version : 0;

    /// <summary>Re-flattens the document and pushes the result to the display bitmap.</summary>
    public void RenderComposite() => RenderCompositeCore(contentChanged: true);

    private void RenderCompositeCore(bool contentChanged)
    {
        if (_document is null || _composite is null) return;
        CompositeVersion++;
        if (contentChanged)
            _frameContentVersions[_document.ActiveFrame] = FrameContentVersion(_document.ActiveFrame) + 1;
        _document.RenderTo(_composite);
        RefreshBitmap();
    }

    /// <summary>Re-flattens and uploads only a changed canvas rectangle.</summary>
    public void RenderComposite(int x, int y, int width, int height)
    {
        if (_document is null || _composite is null || width <= 0 || height <= 0) return;
        int left = Math.Clamp(x, 0, _document.Width);
        int top = Math.Clamp(y, 0, _document.Height);
        int right = (int)Math.Clamp((long)x + width, 0, _document.Width);
        int bottom = (int)Math.Clamp((long)y + height, 0, _document.Height);
        if (right <= left || bottom <= top) return;
        CompositeVersion++;
        _frameContentVersions[_document.ActiveFrame] = FrameContentVersion(_document.ActiveFrame) + 1;
        _document.RenderTo(_composite, left, top, right - left, bottom - top);
        RefreshBitmap(left, top, right - left, bottom - top);
    }

    private unsafe void RefreshBitmap()
        => RefreshBitmap(0, 0, _composite?.Width ?? 0, _composite?.Height ?? 0);

    private unsafe void RefreshBitmap(int x, int y, int width, int height)
    {
        if (_composite is null || _bitmap is null) return;
        using ILockedFramebuffer fb = _bitmap.Lock();
        int rowBytes = width * ColorBgra.SizeOf;
        byte* dst = (byte*)fb.Address;
        for (int row = y; row < y + height; row++)
        {
            byte* src = _composite.GetRowPointer(row) + (long)x * ColorBgra.SizeOf;
            byte* target = dst + (long)row * fb.RowBytes + (long)x * ColorBgra.SizeOf;
            System.Buffer.MemoryCopy(src, target, rowBytes, rowBytes);
        }
    }

    /// <summary>Starts/stops the marching-ants animation and repaints. Called by selection tools.</summary>
    public void NotifySelectionChanged()
    {
        bool active = Selection is { IsActive: true };
        RebuildMarchingAntGeometry();

        if (active && _antTimer is null)
        {
            _antTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _antTimer.Tick += (_, _) => { _antPhase++; InvalidateVisual(); };
            _antTimer.Start();
        }
        else if (!active && _antTimer is not null)
        {
            _antTimer.Stop();
            _antTimer = null;
        }

        InvalidateVisual();
    }

    private void ClipToSelection()
    {
        if (Selection is { IsActive: true } && ActiveLayer is not null && _preStroke is not null)
            Selection.Clip(ActiveLayer.Surface, _preStroke);
    }

    private void ClipToSelection(int x, int y, int width, int height)
    {
        if (Selection is { IsActive: true } && ActiveLayer is not null && _preStroke is not null)
            Selection.Clip(ActiveLayer.Surface, _preStroke, x, y, width, height);
    }

    public void Undo()
    {
        History.Undo();
        AfterHistoryChange();
    }

    public void Redo()
    {
        History.Redo();
        AfterHistoryChange();
    }

    /// <summary>Moves the undo caret directly to a position - what clicking a History panel row does.</summary>
    public void JumpToHistory(int position)
    {
        History.JumpTo(position);
        AfterHistoryChange();
    }

    /// <summary>
    /// Drops a step and everything after it (see HistoryStack.TruncateFrom for why this can't be
    /// a single arbitrary item instead).
    /// </summary>
    public void TruncateHistoryFrom(int index)
    {
        History.TruncateFrom(index);
        AfterHistoryChange();
    }

    public void ClearHistory()
    {
        History.Clear();
        AfterHistoryChange();
    }

    private void AfterHistoryChange()
    {
        // A structural memento may have added/removed/reordered layers, so re-sync the
        // active layer and the panel in addition to recompositing.
        if (_document is not null && (ActiveLayer is null || _document.IndexOf(ActiveLayer) < 0))
            ActiveLayer = _document.LayerCount > 0 ? _document.Layers[^1] : null;
        RenderComposite();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FitToView()
    {
        if (_composite is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        double zx = Bounds.Width / _composite.Width;
        double zy = Bounds.Height / _composite.Height;
        _zoom = Math.Min(Math.Min(zx, zy), 1.0);
        double w = _composite.Width * _zoom;
        double h = _composite.Height * _zoom;
        _origin = new Point((Bounds.Width - w) / 2, (Bounds.Height - h) / 2);
    }

    // Render-time brushes/pens are fixed, so they are built once instead of per frame.
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
    private static readonly IBrush CheckLight = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
    private static readonly IBrush CheckDark = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly IBrush Checkerboard = CreateCheckerboardBrush();
    private static readonly IBrush AntBlack = new SolidColorBrush(Colors.Black);
    private static readonly IBrush AntWhite = new SolidColorBrush(Colors.White);
    private static readonly IPen EdgePen = new Pen(Brushes.Black, 1);
    private static readonly IPen CursorLight = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1);
    private static readonly IPen CursorDark = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);
    private static readonly IBrush DynamicZoneFill = new SolidColorBrush(Color.FromArgb(28, 30, 210, 230));
    private static readonly IBrush DynamicZoneLabel = new SolidColorBrush(Color.FromArgb(245, 210, 252, 255));
    private static readonly Typeface DynamicZoneFace = new("Inter");
    private static readonly IPen DynamicZonePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 30, 210, 230)), 1.5,
        dashStyle: new DashStyle(new[] { 5d, 3d }, 0));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Backdrop, new Rect(Bounds.Size));

        if (_composite is null || _bitmap is null) return;

        if (_fitPending && Bounds.Width > 0)
        {
            FitToView();
            _fitPending = false;
            // The status bar's zoom readout is driven by this event; posted rather than raised
            // inline so nothing mutates the visual tree during a render pass.
            Dispatcher.UIThread.Post(() => { ZoomChanged?.Invoke(_zoom); ViewChanged?.Invoke(); });
        }

        double w = _composite.Width * _zoom;
        double h = _composite.Height * _zoom;
        var dest = new Rect(_origin.X, _origin.Y, w, h);
        DrawCheckerboard(context, dest);
        context.DrawImage(_bitmap, new Rect(0, 0, _composite.Width, _composite.Height), dest);

        if (Selection is { IsActive: true } sel)
            DrawMarchingAnts(context, sel);

        foreach (DynamicTextZone zone in _document!.DynamicTextZones)
        {
            var zoneRect = new Rect(_origin.X + zone.X * _zoom, _origin.Y + zone.Y * _zoom,
                zone.Width * _zoom, zone.Height * _zoom);
            context.DrawRectangle(DynamicZoneFill, DynamicZonePen, zoneRect);

            if (zoneRect.Width > 24 && zoneRect.Height > 14)
            {
                string label = $"{zone.Name}: {zone.Template}";
                if (label.Length > 80) label = label[..77] + "...";
                var formatted = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, DynamicZoneFace, 10, DynamicZoneLabel);
                using (context.PushClip(zoneRect.Deflate(3)))
                    context.DrawText(formatted, zoneRect.TopLeft + new Vector(4, 3));
            }
        }

        context.DrawRectangle(null, EdgePen, dest);

        if (_cursorScreen is Point cs && ShowsBrushCursor && !_panning)
        {
            double r = Math.Max(1.0, BrushWidth / 2.0) * _zoom;
            context.DrawEllipse(null, CursorLight, cs, r + 1, r + 1);
            context.DrawEllipse(null, CursorDark, cs, r, r);
        }

        if (_demoCursorImage is Point dc)
        {
            Point p = ImageToControl(dc.X, dc.Y);
            double r = Math.Max(4.0, Math.Max(1.0, BrushWidth / 2.0) * _zoom);
            context.DrawEllipse(_demoCursorDown ? DemoCursorFill : null, DemoCursorPen, p, r, r);
            context.DrawEllipse(null, DemoCursorPen, p, 1.5, 1.5);
        }
    }

    private void DrawMarchingAnts(DrawingContext context, Selection sel)
    {
        if (_antGeometry.Length == 0) return;
        using (context.PushTransform(new Matrix(_zoom, 0, 0, _zoom, _origin.X, _origin.Y)))
        {
            for (int group = 0; group < _antGeometry.Length; group++)
            {
                IBrush brush = (((group + _antPhase) >> 2) & 1) == 0 ? AntBlack : AntWhite;
                context.DrawGeometry(brush, null, _antGeometry[group]);
            }
        }
    }

    /// <summary>Switches the editable layer stack to another animation frame.</summary>
    public void SetActiveFrame(int index)
    {
        if (_document is null) return;
        bool changed = index != _document.ActiveFrameIndex;
        _document.SetActiveFrame(index);
        ActiveLayer = _document.LayerCount > 0 ? _document.Layers[^1] : _document.AddLayer();
        if (changed)
        {
            Selection?.SelectNone();
            NotifySelectionChanged();
        }
        RenderCompositeCore(contentChanged: false);
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildMarchingAntGeometry()
    {
        if (Selection is not { IsActive: true } selection)
        {
            _antGeometry = Array.Empty<StreamGeometry>();
            return;
        }

        var geometry = new StreamGeometry[8];
        var writers = new StreamGeometryContext[8];
        for (int i = 0; i < geometry.Length; i++)
        {
            geometry[i] = new StreamGeometry();
            writers[i] = geometry[i].Open();
            writers[i].SetFillRule(FillRule.NonZero);
        }

        try
        {
            ReadOnlySpan<byte> mask = selection.Mask;
            int width = selection.Width, height = selection.Height;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (mask[index] == 0) continue;
                bool interior = x > 0 && x < width - 1 && y > 0 && y < height - 1
                    && mask[index - 1] != 0 && mask[index + 1] != 0
                    && mask[index - width] != 0 && mask[index + width] != 0;
                if (interior) continue;

                StreamGeometryContext writer = writers[(x + y) & 7];
                writer.BeginFigure(new Point(x, y), true);
                writer.LineTo(new Point(x + 1, y));
                writer.LineTo(new Point(x + 1, y + 1));
                writer.LineTo(new Point(x, y + 1));
                writer.EndFigure(true);
            }
        }
        finally
        {
            foreach (StreamGeometryContext writer in writers) writer.Dispose();
        }
        _antGeometry = geometry;
    }

    private static IBrush CreateCheckerboardBrush()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing
        {
            Brush = CheckLight,
            Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16))
        });
        drawing.Children.Add(new GeometryDrawing
        {
            Brush = CheckDark,
            Geometry = new RectangleGeometry(new Rect(8, 0, 8, 8))
        });
        drawing.Children.Add(new GeometryDrawing
        {
            Brush = CheckDark,
            Geometry = new RectangleGeometry(new Rect(0, 8, 8, 8))
        });

        return new DrawingBrush(drawing)
        {
            SourceRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            Stretch = Stretch.None,
            TileMode = TileMode.Tile,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
    }

    private static void DrawCheckerboard(DrawingContext context, Rect dest) =>
        context.FillRectangle(Checkerboard, dest);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (SuppressPointerInput || _composite is null) return;

        if (e.Delta.Y == 0)
        {
            // A pure horizontal wheel gesture (trackpad swipe, shift+wheel) has no vertical
            // component to read a zoom direction from - `e.Delta.Y > 0 ? in : out` below used to
            // treat that as "out" unconditionally. Pan horizontally instead, reusing the same
            // _origin the mouse-drag pan already uses.
            if (e.Delta.X != 0)
            {
                // Match Avalonia's ScrollContentPresenter convention: positive wheel X reduces
                // scroll offset, which moves the content origin to the right. The old subtraction
                // made a two-finger horizontal gesture run opposite to every native scroller.
                _origin += new Point(e.Delta.X * 60, 0);
                InvalidateVisual();
                ViewChanged?.Invoke();
            }
            e.Handled = true;
            return;
        }

        Point p = e.GetPosition(this);
        double ix = (p.X - _origin.X) / _zoom;
        double iy = (p.Y - _origin.Y) / _zoom;
        double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 64.0);
        _origin = new Point(p.X - ix * _zoom, p.Y - iy * _zoom);
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    private Point ControlToImage(Point p) => new((p.X - _origin.X) / _zoom, (p.Y - _origin.Y) / _zoom);

    private Point ImageToControl(double ix, double iy) => new(_origin.X + ix * _zoom, _origin.Y + iy * _zoom);

    // The paintbrush belongs here more than any of the others: it is the one tool with both a size
    // and a hardness control, so its footprint is the hardest to predict without seeing it.
    private bool ShowsBrushCursor =>
        CurrentTool is PencilTool or PaintbrushTool or EraserTool or CloneStampTool or RecolorTool;

    /// <summary>
    /// Restores an exact zoom/pan, as recorded in a demo's View events. Every other zoom path is
    /// relative to an anchor, which cannot reproduce a recorded view on a differently sized window.
    /// </summary>
    public void SetView(double zoom, double originX, double originY)
    {
        if (_composite is null) return;
        _fitPending = false;   // an explicit view beats the pending initial fit
        _zoom = Math.Clamp(zoom, 0.02, 64.0);
        _origin = new Point(originX, originY);
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        ViewChanged?.Invoke();
    }

    // ---- demo playback cursor --------------------------------------------
    //
    // A replay with no visible pointer is hard to read: strokes appear from nowhere and the pauses
    // between them look like the app hung. This is a separate overlay from the brush cursor above
    // because it has to show for every tool, and has to show the button state.

    private Point? _demoCursorImage;
    private bool _demoCursorDown;

    private static readonly IPen DemoCursorPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 220, 80)), 1.5);
    private static readonly IBrush DemoCursorFill = new SolidColorBrush(Color.FromArgb(110, 255, 220, 80));

    /// <summary>Places the replay cursor at an image-space point. Null hides it.</summary>
    public void SetDemoCursor(double? ix, double? iy, bool down)
    {
        Point? next = ix is null || iy is null ? null : new Point(ix.Value, iy.Value);
        if (next == _demoCursorImage && down == _demoCursorDown) return;
        _demoCursorImage = next;
        _demoCursorDown = down;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_cursorScreen is not null) { _cursorScreen = null; InvalidateVisual(); }

        // CursorMoved is what drives the status-bar coordinate readout and the two ruler markers.
        // Without a final off-canvas report they all freeze at wherever the pointer last was and
        // keep pointing there while it is somewhere else entirely. Skipped mid-gesture, where the
        // pointer is captured and "left the control" does not mean the user stopped drawing.
        if (!_panning && !_drawing) CursorMoved?.Invoke(CursorGone, CursorGone);
    }

    /// <summary>Raised whenever the zoom factor changes.</summary>
    public event Action<double>? ZoomChanged;

    private void ZoomAround(Point anchor, double factor)
    {
        if (_composite is null) return;
        double ix = (anchor.X - _origin.X) / _zoom;
        double iy = (anchor.Y - _origin.Y) / _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 64.0);
        _origin = new Point(anchor.X - ix * _zoom, anchor.Y - iy * _zoom);
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        ViewChanged?.Invoke();
    }

    private Point ViewportCenter => new(Bounds.Width / 2, Bounds.Height / 2);

    public void ZoomIn() => ZoomAround(ViewportCenter, 1.25);
    public void ZoomOut() => ZoomAround(ViewportCenter, 1 / 1.25);

    public void ZoomToFit()
    {
        _fitPending = false;
        FitToView();
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        ViewChanged?.Invoke();
    }

    public void ZoomActual() => ZoomAround(ViewportCenter, 1.0 / _zoom);

    /// <summary>
    /// While true, real pointer and wheel input on the canvas is ignored. Demo playback sets this:
    /// it drives strokes through BeginStroke/ExtendStroke, and a stray mouse move over the canvas
    /// would otherwise be handed to the very same in-flight gesture and drawn into the replay.
    /// (Found by moving the mouse during a replay and watching a line shoot off to the pointer.)
    /// </summary>
    public bool SuppressPointerInput { get; set; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (SuppressPointerInput) return;
        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _panning = true;
            _lastPointer = pt.Position;
            e.Pointer.Capture(this);
        }
        else if (pt.Properties.IsLeftButtonPressed && ActiveLayer is not null && _composite is not null)
        {
            Point img = ControlToImage(pt.Position);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (BeginStroke(img.X, img.Y, ctrl)) StrokeBegan?.Invoke(img.X, img.Y, ctrl);
            e.Pointer.Capture(this);
        }
    }

    /// <summary>
    /// Raised when a stroke starts from real pointer input, with the image-space coordinate and
    /// whether Ctrl was held. Deliberately *not* raised by <see cref="BeginStroke"/> itself, so a
    /// demo being replayed through that method cannot re-record itself. Same for the two below.
    /// </summary>
    public event Action<double, double, bool>? StrokeBegan;

    public event Action<double, double>? StrokeExtended;

    public event Action? StrokeEnded;

    /// <summary>
    /// Starts a tool gesture at an image-space point. Shared by the pointer handler and by demo
    /// playback, which is the reason it takes image coordinates rather than a control-space Point:
    /// a replay must land on the same pixels regardless of the window size or zoom it runs at.
    /// Returns false when there is nothing to draw on.
    /// </summary>
    public bool BeginStroke(double ix, double iy, bool ctrl)
    {
        if (ActiveLayer is null || _composite is null) return false;
        if (_drawing) EndStroke();

        _drawing = true;
        Layer layer = ActiveLayer;

        _preStroke?.Dispose();
        _preStroke = layer.Surface.Clone();

        {
            _toolCtx = new ToolContext
            {
                Layer = layer,
                PreStroke = _preStroke,
                PrimaryColor = BrushColor,
                SecondaryColor = SecondaryColor,
                BrushWidth = BrushWidth,
                BrushHardness = BrushHardness,
                Antialias = Antialias,
                FillTolerance = FillTolerance,
                GlobalFill = GlobalFill,
                FillShapes = FillShapes,
                CtrlHeld = ctrl,
                DocumentVersion = _documentVersion,
                X = ix,
                Y = iy,
                PushHistory = () =>
                {
                    _pendingHistoryLayer = layer;
                    _pendingHistoryName = CurrentTool.Name;
                },
                Composite = () => { ClipToSelection(); RenderComposite(); InvalidateVisual(); },
                CompositeRect = (x, y, width, height) =>
                {
                    ClipToSelection(x, y, width, height);
                    RenderComposite(x, y, width, height);
                    InvalidateVisual();
                },
                SampleComposite = (x, y) =>
                    (uint)x < (uint)_composite.Width && (uint)y < (uint)_composite.Height
                        ? _composite[x, y] : ColorBgra.Transparent,
                SetPrimaryColor = c => { BrushColor = c; PrimaryColorPicked?.Invoke(c); },
                Selection = Selection!,
                SelectionChanged = NotifySelectionChanged,
                RequestText = (x, y) => TextRequested?.Invoke(x, y),
                RequestDynamicText = (x, y) => DynamicTextRequested?.Invoke(x, y),
                CombineMode = SelectionCombineMode
            };

            CurrentTool.PointerDown(_toolCtx);
        }

        return true;
    }

    public void NotifyDynamicZonesChanged()
    {
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Continues the in-flight gesture at an image-space point. No-op when none is.</summary>
    public void ExtendStroke(double ix, double iy)
    {
        if (!_drawing || _toolCtx is null) return;
        _toolCtx.X = ix;
        _toolCtx.Y = iy;
        CurrentTool.PointerMove(_toolCtx);
    }

    /// <summary>Finishes the in-flight gesture and commits its undo step. No-op when none is.</summary>
    public void EndStroke()
    {
        if (!_drawing) return;
        FinishGesture();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (SuppressPointerInput) return;
        Point p = e.GetPosition(this);

        if (CursorMoved is not null)
        {
            Point ip = ControlToImage(p);
            CursorMoved((int)Math.Floor(ip.X), (int)Math.Floor(ip.Y));
        }

        if (ShowsBrushCursor)
        {
            _cursorScreen = p;
            InvalidateVisual();
        }

        if (_panning)
        {
            _origin += p - _lastPointer;
            _lastPointer = p;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
        else if (_drawing && _toolCtx is not null)
        {
            Point img = ControlToImage(p);
            ExtendStroke(img.X, img.Y);
            StrokeExtended?.Invoke(img.X, img.Y);
        }
    }

    /// <summary>
    /// Turns the pre-stroke snapshot and the layer's current state into one undo step. Push
    /// ignores a null delta, so a gesture that ended up changing nothing leaves no step behind.
    /// </summary>
    private void CommitPendingHistory()
    {
        if (_pendingHistoryLayer is null || _preStroke is null) return;

        var layer = _pendingHistoryLayer;
        _pendingHistoryLayer = null;

        // A tool that resized the layer (none do today) would invalidate the tile comparison, so
        // fall back to the whole-surface snapshot rather than throwing.
        if (layer.Surface.Width != _preStroke.Width || layer.Surface.Height != _preStroke.Height)
        {
            History.Push(LayerSurfaceMemento.FromSnapshot(layer, _preStroke.Clone(), _pendingHistoryName));
            return;
        }

        History.Push(TileDeltaMemento.Create(layer, _preStroke, _pendingHistoryName));
    }

    /// <summary>
    /// Wraps up whatever gesture is in progress: the stroke's tool finalize + undo commit, or just
    /// clearing the pan flag. Shared by a normal release and an involuntary capture loss (see
    /// OnPointerCaptureLost), so neither path can leave _drawing/_preStroke stuck mid-stroke.
    /// </summary>
    private void FinishGesture()
    {
        if (_drawing)
        {
            if (_toolCtx is not null) CurrentTool.PointerUp(_toolCtx);
            _toolCtx = null;
            CommitPendingHistory();
            _preStroke?.Dispose();
            _preStroke = null;
            NotifyLayersChanged();   // the stroke is final: let the layer thumbnails catch up
        }

        _panning = false;
        _drawing = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (SuppressPointerInput) return;
        bool wasActive = _panning || _drawing;
        bool wasDrawing = _drawing;
        FinishGesture();
        if (wasDrawing) StrokeEnded?.Invoke();
        if (wasActive) e.Pointer.Capture(null);
    }

    /// <summary>
    /// Avalonia calls this when capture is lost involuntarily rather than via a normal release -
    /// alt-tab, the window losing focus, or another element stealing capture mid-drag. Without this,
    /// _drawing/_preStroke stayed set forever: the next press would dispose and silently replace
    /// _preStroke, dropping the in-progress stroke's history with no undo step recorded.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (SuppressPointerInput) return;   // a replay's gesture is not the pointer's to lose
        bool wasDrawing = _drawing;
        FinishGesture();
        if (wasDrawing) StrokeEnded?.Invoke();
    }
}
