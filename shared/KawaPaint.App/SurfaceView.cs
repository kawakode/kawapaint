// KawaPaint — Avalonia canvas control. Displays a Document (flattened to a composite Surface)
// with zoom/pan, paints the pencil onto the active layer, and records undo/redo history.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class SurfaceView : Control
{
    private Document? _document;
    private Surface? _composite;
    private WriteableBitmap? _bitmap;

    private int _antPhase;
    private DispatcherTimer? _antTimer;

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
    public bool Antialias { get; set; } = true;
    public int FillTolerance { get; set; } = 32;
    public bool GlobalFill { get; set; }
    public bool FillShapes { get; set; }

    public ITool CurrentTool { get; set; } = new PencilTool();

    /// <summary>Raised by the color-picker tool with the sampled color.</summary>
    public event Action<ColorBgra>? PrimaryColorPicked;

    /// <summary>Raised by the text tool at the clicked image point (x,y).</summary>
    public event Action<int, int>? TextRequested;

    /// <summary>Raised as the pointer moves, with the image-space coordinate under it.</summary>
    public event Action<int, int>? CursorMoved;

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

        _document = document;
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
        RenderComposite();
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

    /// <summary>Re-flattens the document and pushes the result to the display bitmap.</summary>
    public void RenderComposite()
    {
        if (_document is null || _composite is null) return;
        _document.RenderTo(_composite);
        RefreshBitmap();
    }

    private unsafe void RefreshBitmap()
    {
        if (_composite is null || _bitmap is null) return;
        using ILockedFramebuffer fb = _bitmap.Lock();
        int rowBytes = _composite.Width * ColorBgra.SizeOf;
        byte* dst = (byte*)fb.Address;
        for (int y = 0; y < _composite.Height; y++)
        {
            byte* src = _composite.GetRowPointer(y);
            System.Buffer.MemoryCopy(src, dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
        }
    }

    /// <summary>Starts/stops the marching-ants animation and repaints. Called by selection tools.</summary>
    public void NotifySelectionChanged()
    {
        bool active = Selection is { IsActive: true };

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
    private static readonly IBrush AntBlack = new SolidColorBrush(Colors.Black);
    private static readonly IBrush AntWhite = new SolidColorBrush(Colors.White);
    private static readonly IPen EdgePen = new Pen(Brushes.Black, 1);
    private static readonly IPen CursorLight = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1);
    private static readonly IPen CursorDark = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);

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
            Dispatcher.UIThread.Post(() => ZoomChanged?.Invoke(_zoom));
        }

        double w = _composite.Width * _zoom;
        double h = _composite.Height * _zoom;
        var dest = new Rect(_origin.X, _origin.Y, w, h);
        var viewport = new Rect(Bounds.Size);

        DrawCheckerboard(context, dest, viewport);
        context.DrawImage(_bitmap, new Rect(0, 0, _composite.Width, _composite.Height), dest);

        if (Selection is { IsActive: true } sel)
            DrawMarchingAnts(context, sel);

        context.DrawRectangle(null, EdgePen, dest);

        if (_cursorScreen is Point cs && ShowsBrushCursor && !_panning)
        {
            double r = Math.Max(1.0, BrushWidth / 2.0) * _zoom;
            context.DrawEllipse(null, CursorLight, cs, r + 1, r + 1);
            context.DrawEllipse(null, CursorDark, cs, r, r);
        }
    }

    private void DrawMarchingAnts(DrawingContext context, Selection sel)
    {
        var mask = sel.Mask;
        int w = sel.Width, h = sel.Height;

        // Only walk the pixels currently on screen, and — when zoomed out far enough that many
        // image pixels share one screen pixel — sample every Nth so the cost stays bound to the
        // viewport instead of the image size.
        int step = Math.Max(1, (int)Math.Ceiling(1.0 / _zoom));
        double size = Math.Max(1.0, _zoom * step);

        int xMin = Math.Max(0, (int)Math.Floor(-_origin.X / _zoom));
        int xMax = Math.Min(w - 1, (int)Math.Ceiling((Bounds.Width - _origin.X) / _zoom));
        int yMin = Math.Max(0, (int)Math.Floor(-_origin.Y / _zoom));
        int yMax = Math.Min(h - 1, (int)Math.Ceiling((Bounds.Height - _origin.Y) / _zoom));

        for (int y = yMin; y <= yMax; y += step)
        {
            for (int x = xMin; x <= xMax; x += step)
            {
                int idx = y * w + x;
                if (mask[idx] == 0) continue;

                // Boundary pixel: a selected pixel touching an unselected one (or the image edge).
                bool interior = x > 0 && x < w - 1 && y > 0 && y < h - 1
                    && mask[idx - 1] != 0 && mask[idx + 1] != 0
                    && mask[idx - w] != 0 && mask[idx + w] != 0;
                if (interior) continue;

                var brush = (((x + y + _antPhase) >> 2) & 1) == 0 ? AntBlack : AntWhite;
                context.FillRectangle(brush, new Rect(_origin.X + x * _zoom, _origin.Y + y * _zoom, size, size));
            }
        }
    }

    private static void DrawCheckerboard(DrawingContext context, Rect dest, Rect viewport)
    {
        const int cell = 8;

        // Cells are only emitted for the on-screen part of the canvas: at high zoom the canvas
        // rect is far larger than the window, and tiling all of it would stall the render thread.
        Rect vis = dest.Intersect(viewport);
        if (vis.Width <= 0 || vis.Height <= 0) return;

        context.FillRectangle(CheckLight, vis);

        int x0 = (int)Math.Floor((vis.X - dest.X) / cell);
        int x1 = (int)Math.Ceiling((vis.Right - dest.X) / cell);
        int y0 = (int)Math.Floor((vis.Y - dest.Y) / cell);
        int y1 = (int)Math.Ceiling((vis.Bottom - dest.Y) / cell);

        using (context.PushClip(vis))
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (((x + y) & 1) != 0)
                        context.FillRectangle(CheckDark, new Rect(dest.X + x * cell, dest.Y + y * cell, cell, cell));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_composite is null) return;

        Point p = e.GetPosition(this);
        double ix = (p.X - _origin.X) / _zoom;
        double iy = (p.Y - _origin.Y) / _zoom;
        double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 64.0);
        _origin = new Point(p.X - ix * _zoom, p.Y - iy * _zoom);
        InvalidateVisual();
        ZoomChanged?.Invoke(_zoom);
        e.Handled = true;
    }

    private Point ControlToImage(Point p) => new((p.X - _origin.X) / _zoom, (p.Y - _origin.Y) / _zoom);

    private bool ShowsBrushCursor => CurrentTool is PencilTool or EraserTool;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_cursorScreen is not null) { _cursorScreen = null; InvalidateVisual(); }
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
    }

    public void ZoomActual() => ZoomAround(ViewportCenter, 1.0 / _zoom);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _panning = true;
            _lastPointer = pt.Position;
            e.Pointer.Capture(this);
        }
        else if (pt.Properties.IsLeftButtonPressed && ActiveLayer is not null && _composite is not null)
        {
            _drawing = true;
            Layer layer = ActiveLayer;

            _preStroke?.Dispose();
            _preStroke = layer.Surface.Clone();

            Point img = ControlToImage(pt.Position);
            _toolCtx = new ToolContext
            {
                Layer = layer,
                PreStroke = _preStroke,
                PrimaryColor = BrushColor,
                SecondaryColor = SecondaryColor,
                BrushWidth = BrushWidth,
                Antialias = Antialias,
                FillTolerance = FillTolerance,
                GlobalFill = GlobalFill,
                FillShapes = FillShapes,
                X = img.X,
                Y = img.Y,
                PushHistory = () =>
                {
                    _pendingHistoryLayer = layer;
                    _pendingHistoryName = CurrentTool.Name;
                },
                Composite = () => { ClipToSelection(); RenderComposite(); InvalidateVisual(); },
                SampleComposite = (x, y) =>
                    (uint)x < (uint)_composite.Width && (uint)y < (uint)_composite.Height
                        ? _composite[x, y] : ColorBgra.Transparent,
                SetPrimaryColor = c => { BrushColor = c; PrimaryColorPicked?.Invoke(c); },
                Selection = Selection!,
                SelectionChanged = NotifySelectionChanged,
                RequestText = (x, y) => TextRequested?.Invoke(x, y)
            };

            CurrentTool.PointerDown(_toolCtx);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
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
        }
        else if (_drawing && _toolCtx is not null)
        {
            Point img = ControlToImage(p);
            _toolCtx.X = img.X;
            _toolCtx.Y = img.Y;
            CurrentTool.PointerMove(_toolCtx);
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drawing)
        {
            if (_toolCtx is not null) CurrentTool.PointerUp(_toolCtx);
            _toolCtx = null;
            CommitPendingHistory();
            _preStroke?.Dispose();
            _preStroke = null;
            NotifyLayersChanged();   // the stroke is final: let the layer thumbnails catch up
        }

        if (_panning || _drawing)
        {
            _panning = false;
            _drawing = false;
            e.Pointer.Capture(null);
        }
    }
}
