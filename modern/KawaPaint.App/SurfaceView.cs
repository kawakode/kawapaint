// KawaPaint — Avalonia canvas control that displays an engine Surface with zoom + pan.
// The Surface is BGRA/unpremultiplied, identical to Avalonia's WriteableBitmap format,
// so refreshing the view is a straight row copy (no pixel conversion).

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
    private Surface? _surface;
    private WriteableBitmap? _bitmap;

    private double _zoom = 1.0;
    private Point _origin;          // top-left of the image in control space
    private bool _panning;
    private Point _lastPointer;
    private bool _fitPending = true;

    private bool _drawing;
    private Point _lastImage;        // last painted point, in image space

    /// <summary>Color laid down by the pencil tool.</summary>
    public ColorBgra BrushColor { get; set; } = ColorBgra.Black;

    /// <summary>Pencil width in pixels.</summary>
    public int BrushWidth { get; set; } = 3;

    public SurfaceView()
    {
        ClipToBounds = true;
        Focusable = true;
        // Keep pixels crisp when zoomed in.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public double Zoom => _zoom;

    public void SetSurface(Surface surface)
    {
        _surface = surface;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(surface.Width, surface.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        RefreshBitmap();
        _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>Re-copies the Surface pixels into the display bitmap. Call after edits.</summary>
    public unsafe void RefreshBitmap()
    {
        if (_surface is null || _bitmap is null) return;

        using ILockedFramebuffer fb = _bitmap.Lock();
        int rowBytes = _surface.Width * ColorBgra.SizeOf;
        byte* dst = (byte*)fb.Address;
        for (int y = 0; y < _surface.Height; y++)
        {
            byte* src = _surface.GetRowPointer(y);
            System.Buffer.MemoryCopy(src, dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
        }
    }

    private void FitToView()
    {
        if (_surface is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        double zx = Bounds.Width / _surface.Width;
        double zy = Bounds.Height / _surface.Height;
        _zoom = Math.Min(Math.Min(zx, zy), 1.0);
        double w = _surface.Width * _zoom;
        double h = _surface.Height * _zoom;
        _origin = new Point((Bounds.Width - w) / 2, (Bounds.Height - h) / 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Workspace backdrop.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), new Rect(Bounds.Size));

        if (_surface is null || _bitmap is null) return;

        if (_fitPending && Bounds.Width > 0)
        {
            FitToView();
            _fitPending = false;
        }

        double w = _surface.Width * _zoom;
        double h = _surface.Height * _zoom;
        var dest = new Rect(_origin.X, _origin.Y, w, h);

        DrawCheckerboard(context, dest);
        context.DrawImage(_bitmap, new Rect(0, 0, _surface.Width, _surface.Height), dest);

        // 1px frame around the image.
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
            {
                for (int x = 0; x * cell < dest.Width; x++)
                {
                    if (((x + y) & 1) == 0) continue;
                    var r = new Rect(dest.X + x * cell, dest.Y + y * cell, cell, cell);
                    context.FillRectangle(dark, r);
                }
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_surface is null) return;

        Point p = e.GetPosition(this);
        // Image coordinate under the cursor before zoom.
        double ix = (p.X - _origin.X) / _zoom;
        double iy = (p.Y - _origin.Y) / _zoom;

        double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        _zoom = Math.Clamp(_zoom * factor, 0.05, 32.0);

        // Keep the same image point under the cursor.
        _origin = new Point(p.X - ix * _zoom, p.Y - iy * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    private Point ControlToImage(Point p) =>
        new((p.X - _origin.X) / _zoom, (p.Y - _origin.Y) / _zoom);

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
        else if (pt.Properties.IsLeftButtonPressed && _surface is not null)
        {
            _drawing = true;
            _lastImage = ControlToImage(pt.Position);
            BrushOps.FillDisc(_surface, (int)Math.Round(_lastImage.X), (int)Math.Round(_lastImage.Y),
                BrushWidth / 2, BrushColor);
            RefreshBitmap();
            InvalidateVisual();
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
        else if (_drawing && _surface is not null)
        {
            Point img = ControlToImage(p);
            BrushOps.DrawLine(_surface, _lastImage.X, _lastImage.Y, img.X, img.Y, BrushWidth / 2, BrushColor);
            _lastImage = img;
            RefreshBitmap();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning || _drawing)
        {
            _panning = false;
            _drawing = false;
            e.Pointer.Capture(null);
        }
    }
}
