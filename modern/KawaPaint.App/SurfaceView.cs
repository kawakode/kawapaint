// KawaPaint — Avalonia canvas control. Displays a Document (flattened to a composite Surface)
// with zoom/pan, paints the pencil onto the active layer, and records undo/redo history.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class SurfaceView : Control
{
    private Document? _document;
    private Surface? _composite;
    private WriteableBitmap? _bitmap;

    private double _zoom = 1.0;
    private Point _origin;
    private bool _panning;
    private Point _lastPointer;
    private bool _fitPending = true;

    private bool _drawing;

    public ColorBgra BrushColor { get; set; } = ColorBgra.Black;
    public int BrushWidth { get; set; } = 3;

    public ITool CurrentTool { get; set; } = new PencilTool();

    /// <summary>Raised by the color-picker tool with the sampled color.</summary>
    public event Action<ColorBgra>? PrimaryColorPicked;

    private Surface? _preStroke;
    private ToolContext? _toolCtx;

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

    public void SetDocument(Document document)
    {
        _document?.Dispose();
        _composite?.Dispose();
        _bitmap?.Dispose();

        _document = document;
        ActiveLayer = document.LayerCount > 0 ? document.Layers[^1] : document.AddLayer();
        _composite = new Surface(document.Width, document.Height);
        _bitmap = new WriteableBitmap(
            new PixelSize(document.Width, document.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        History.Clear();
        _fitPending = true;
        RenderComposite();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

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

    public void Undo()
    {
        History.Undo();
        RenderComposite();
        InvalidateVisual();
    }

    public void Redo()
    {
        History.Redo();
        RenderComposite();
        InvalidateVisual();
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), new Rect(Bounds.Size));

        if (_composite is null || _bitmap is null) return;

        if (_fitPending && Bounds.Width > 0)
        {
            FitToView();
            _fitPending = false;
        }

        double w = _composite.Width * _zoom;
        double h = _composite.Height * _zoom;
        var dest = new Rect(_origin.X, _origin.Y, w, h);

        DrawCheckerboard(context, dest);
        context.DrawImage(_bitmap, new Rect(0, 0, _composite.Width, _composite.Height), dest);
        context.DrawRectangle(null, new Pen(Brushes.Black, 1), dest);
    }

    private static void DrawCheckerboard(DrawingContext context, Rect dest)
    {
        const int cell = 8;
        var light = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
        var dark = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        context.FillRectangle(light, dest);
        using (context.PushClip(dest))
        {
            for (int y = 0; y * cell < dest.Height; y++)
                for (int x = 0; x * cell < dest.Width; x++)
                    if (((x + y) & 1) != 0)
                        context.FillRectangle(dark, new Rect(dest.X + x * cell, dest.Y + y * cell, cell, cell));
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
        _zoom = Math.Clamp(_zoom * factor, 0.05, 32.0);
        _origin = new Point(p.X - ix * _zoom, p.Y - iy * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    private Point ControlToImage(Point p) => new((p.X - _origin.X) / _zoom, (p.Y - _origin.Y) / _zoom);

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
                BrushWidth = BrushWidth,
                X = img.X,
                Y = img.Y,
                PushHistory = () => History.Push(new LayerSurfaceMemento(layer, CurrentTool.Name)),
                Composite = () => { RenderComposite(); InvalidateVisual(); },
                SampleComposite = (x, y) =>
                    (uint)x < (uint)_composite.Width && (uint)y < (uint)_composite.Height
                        ? _composite[x, y] : ColorBgra.Transparent,
                SetPrimaryColor = c => { BrushColor = c; PrimaryColorPicked?.Invoke(c); }
            };

            CurrentTool.PointerDown(_toolCtx);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);

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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drawing)
        {
            if (_toolCtx is not null) CurrentTool.PointerUp(_toolCtx);
            _toolCtx = null;
            _preStroke?.Dispose();
            _preStroke = null;
        }

        if (_panning || _drawing)
        {
            _panning = false;
            _drawing = false;
            e.Pointer.Capture(null);
        }
    }
}
