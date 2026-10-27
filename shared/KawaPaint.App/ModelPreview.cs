// KawaPaint - live 3D view of a model wearing the document as its texture. Drag to turn it, wheel
// to zoom; in paint mode a left drag reports the texture coordinates under the pointer instead, and
// MainView paints there. Renders on the UI thread, coalesced to one render per dispatcher pass.

using System;
using Vector2 = System.Numerics.Vector2;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

public sealed class ModelPreview : Control
{
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
    private static readonly Typeface HintFace = new("Inter");

    private ObjMesh? _mesh;
    private WriteableBitmap? _bitmap;
    private UvMap? _uvMap;
    private bool _renderQueued;
    private bool _interacting;
    private Point? _dragFrom;
    private bool _painting;

    public float Yaw { get; private set; } = -35;
    public float Pitch { get; private set; } = -20;
    public float Zoom { get; private set; } = 1;

    /// <summary>Supplies the texture at render time (the canvas composite); null shows the bare model.</summary>
    public Func<Surface?>? TextureSource { get; set; }

    /// <summary>Left drag paints instead of turning the model. Right or middle drag always turns it.</summary>
    public bool PaintMode { get; set; }

    /// <summary>A paint drag began; the handler snapshots the layer for undo.</summary>
    public event Action? PaintStarted;

    /// <summary>A texture coordinate under the paint drag, one per screen pixel travelled.</summary>
    public event Action<Vector2>? PaintSample;

    public event Action? PaintEnded;

    public ObjMesh? Mesh
    {
        get => _mesh;
        set
        {
            _mesh = value;
            Yaw = -35; Pitch = -20; Zoom = 1;
            RequestRender();
        }
    }

    public ModelPreview()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void ResetView()
    {
        Yaw = -35; Pitch = -20; Zoom = 1;
        RequestRender();
    }

    /// <summary>Schedules a re-render. Many calls in one dispatcher pass (a brush stroke changes
    /// the composite on every pointer move) collapse into one.</summary>
    public void RequestRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderQueued = false;
            RenderModel();
        }, DispatcherPriority.Background);
    }

    /// <summary>Renders now. Public so tests can render without pumping the dispatcher.</summary>
    public void RenderModel()
    {
        int width = (int)Math.Floor(Bounds.Width), height = (int)Math.Floor(Bounds.Height);
        if (_mesh is null || width < 8 || height < 8)
        {
            _uvMap = null;
            InvalidateVisual();
            return;
        }

        var options = new ReferenceRenderOptions
        {
            YawDegrees = Yaw,
            PitchDegrees = Pitch,
            Zoom = Zoom,
            Texture = TextureSource?.Invoke(),
            // Full quality once the pointer lets go; plain sampling keeps drags responsive.
            Supersampling = _interacting ? 1 : 2
        };
        using Surface rendered = ReferenceRenderer.Render(_mesh, width, height, options, out UvMap map);
        _uvMap = map;
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        }
        CopyToBitmap(rendered, _bitmap);
        InvalidateVisual();
    }

    private static unsafe void CopyToBitmap(Surface source, WriteableBitmap target)
    {
        using ILockedFramebuffer fb = target.Lock();
        int rowBytes = source.Width * ColorBgra.SizeOf;
        for (int y = 0; y < source.Height; y++)
            Buffer.MemoryCopy(source.GetRowPointer(y), (byte*)fb.Address + (long)y * fb.RowBytes, rowBytes, rowBytes);
    }

    /// <summary>The texture coordinate shown at a point of this control, if it is on a UV-mapped face.</summary>
    public bool TryGetTexCoord(Point point, out Vector2 uv)
    {
        uv = default;
        return _uvMap is { } map && map.TryGet(point.X, point.Y, out uv);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RequestRender();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Backdrop, new Rect(Bounds.Size));
        if (_mesh is not null && _bitmap is not null)
        {
            var size = _bitmap.PixelSize;
            context.DrawImage(_bitmap, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
            return;
        }
        var hint = new FormattedText("Open a model to paint on it", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, HintFace, 12, HintBrush);
        context.DrawText(hint, new Point((Bounds.Width - hint.Width) / 2, (Bounds.Height - hint.Height) / 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_mesh is null) return;
        var point = e.GetCurrentPoint(this);
        _dragFrom = point.Position;
        _interacting = true;
        _painting = PaintMode && point.Properties.IsLeftButtonPressed && _mesh.HasTexCoords;
        e.Pointer.Capture(this);
        e.Handled = true;
        if (!_painting) return;
        PaintStarted?.Invoke();
        if (TryGetTexCoord(point.Position, out Vector2 uv)) PaintSample?.Invoke(uv);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        Point to = e.GetPosition(this);
        if (_painting)
        {
            // One sample per screen pixel travelled, so fast drags leave no gaps. Each sample is
            // looked up on its own, which keeps a stroke crossing a UV seam on both sides of it.
            double distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(distance));
            for (int i = 1; i <= steps; i++)
            {
                Point p = from + (to - from) * (i / (double)steps);
                if (TryGetTexCoord(p, out Vector2 uv)) PaintSample?.Invoke(uv);
            }
        }
        else
        {
            Yaw = (float)(Yaw + (to.X - from.X) * 0.5);
            Pitch = (float)Math.Clamp(Pitch - (to.Y - from.Y) * 0.5, -89, 89);
            RequestRender();
        }
        _dragFrom = to;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        _interacting = false;
        if (_painting)
        {
            _painting = false;
            PaintEnded?.Invoke();
        }
        RequestRender();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_mesh is null || e.Delta.Y == 0) return;
        Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.15f : 1 / 1.15f), 0.2f, 20f);
        RequestRender();
        e.Handled = true;
    }
}
