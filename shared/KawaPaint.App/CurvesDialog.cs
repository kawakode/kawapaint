// KawaPaint - interactive tone-curve editor. CurveControl lets you drag/add/remove control
// points; the dialog builds a 256-entry LUT and previews CurvesEffect live on the active layer.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class CurveControl : Control
{
    // Control points in data space: X,Y in [0,255], Y=0 is output black.
    private readonly List<Point> _points = new() { new Point(0, 0), new Point(255, 255) };
    private int _dragIndex = -1;
    private readonly IBrush _bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public event Action? Changed;

    public CurveControl()
    {
        Width = 260;
        Height = 260;
    }

    public byte[] BuildLut()
    {
        var pts = _points.OrderBy(p => p.X).ToList();
        var lut = new byte[256];
        for (int x = 0; x < 256; x++)
        {
            double y;
            if (x <= pts[0].X) y = pts[0].Y;
            else if (x >= pts[^1].X) y = pts[^1].Y;
            else
            {
                int i = 0;
                while (i < pts.Count - 1 && pts[i + 1].X < x) i++;
                var a = pts[i];
                var b = pts[i + 1];
                double t = (x - a.X) / Math.Max(1e-6, b.X - a.X);
                y = a.Y + t * (b.Y - a.Y);
            }
            lut[x] = (byte)Math.Clamp((int)Math.Round(y), 0, 255);
        }
        return lut;
    }

    public void Reset()
    {
        _points.Clear();
        _points.Add(new Point(0, 0));
        _points.Add(new Point(255, 255));
        InvalidateVisual();
        Changed?.Invoke();
    }

    private Point DataToScreen(Point d) => new(d.X / 255.0 * Bounds.Width, (1 - d.Y / 255.0) * Bounds.Height);
    private Point ScreenToData(Point s) => new(
        Math.Clamp(s.X / Bounds.Width * 255.0, 0, 255),
        Math.Clamp((1 - s.Y / Bounds.Height) * 255.0, 0, 255));

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(_bg, new Rect(Bounds.Size));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 1);
        for (int i = 1; i < 4; i++)
        {
            double gx = Bounds.Width * i / 4, gy = Bounds.Height * i / 4;
            ctx.DrawLine(grid, new Point(gx, 0), new Point(gx, Bounds.Height));
            ctx.DrawLine(grid, new Point(0, gy), new Point(Bounds.Width, gy));
        }

        var lut = BuildLut();
        var curve = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), 1.5);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(DataToScreen(new Point(0, lut[0])), false);
            for (int x = 1; x < 256; x++) g.LineTo(DataToScreen(new Point(x, lut[x])));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, curve, geo);

        var handle = new SolidColorBrush(Colors.White);
        foreach (var p in _points)
        {
            var s = DataToScreen(p);
            ctx.DrawEllipse(handle, null, s, 4, 4);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point s = e.GetPosition(this);
        Point d = ScreenToData(s);

        // Hit-test existing points.
        for (int i = 0; i < _points.Count; i++)
        {
            if (Distance(DataToScreen(_points[i]), s) <= 10)
            {
                if (e.ClickCount == 2 && i != 0 && i != _points.Count - 1)
                {
                    _points.RemoveAt(i);
                    InvalidateVisual();
                    Changed?.Invoke();
                    return;
                }
                _dragIndex = i;
                e.Pointer.Capture(this);
                return;
            }
        }

        // Otherwise add a new interior point and start dragging it.
        _points.Add(d);
        _points.Sort((a, b) => a.X.CompareTo(b.X));
        _dragIndex = _points.FindIndex(p => p == d);
        e.Pointer.Capture(this);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0) return;

        Point d = ScreenToData(e.GetPosition(this));
        double x = d.X;
        // Endpoints keep their X; interior points stay between neighbours.
        if (_dragIndex == 0) x = 0;
        else if (_dragIndex == _points.Count - 1) x = 255;
        else x = Math.Clamp(x, _points[_dragIndex - 1].X + 1, _points[_dragIndex + 1].X - 1);

        _points[_dragIndex] = new Point(x, d.Y);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragIndex = -1;
        e.Pointer.Capture(null);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed class CurvesDialog : Window
{
    private readonly SurfaceView _canvas;
    private readonly Layer? _layer;
    private readonly CurveControl _curve;
    private Surface? _snapshot;
    private bool _committed;
    private DispatcherTimer? _previewTimer;
    private Action<bool>? _canvasClose;

    public CurvesDialog(SurfaceView canvas)
    {
        _canvas = canvas;
        _layer = canvas.ActiveLayer;
        if (_layer is not null) _snapshot = _layer.Surface.Clone();

        Title = "Curves";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _curve = new CurveControl { HorizontalAlignment = HorizontalAlignment.Center };
        _curve.Changed += SchedulePreview;

        var hint = new TextBlock
        {
            Text = "Drag points • click to add • double-click to remove",
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            // Longer than the dialog is wide, so it was silently cut off at "double-click to".
            // Wrapping plus SizeToContent gives it the second line it needs at any font size.
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) => _curve.Reset();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) =>
        {
            if (_canvasClose is null) Close();
            else { CancelCanvasHost(); _canvasClose(false); }
        };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            Commit();
            if (_canvasClose is null) Close(); else _canvasClose(true);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Content = new StackPanel { Margin = new Thickness(16), Children = { _curve, hint, buttons } };
        Closed += (_, _) => { _previewTimer?.Stop(); if (!_committed) Revert(); };
    }

    public void UseCanvasHost(Action<bool> close) => _canvasClose = close;
    public void BeginCanvasHost() { }
    public void CancelCanvasHost()
    {
        _previewTimer?.Stop();
        if (!_committed) Revert();
    }

    /// <summary>
    /// Coalesces a burst of Changed events into one Preview() ~60ms after the last one. Dragging a
    /// control point raises Changed on every pointer move, and each Preview is a full-surface
    /// CurvesEffect.Apply plus a recomposite - on a large image that is far more work than the
    /// frame budget allows, and the drag visibly stutters. Same treatment, and the same interval,
    /// as AdjustmentDialog's slider debounce.
    /// </summary>
    private void SchedulePreview()
    {
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _previewTimer.Tick += (_, _) => { _previewTimer!.Stop(); Preview(); };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Preview()
    {
        if (_snapshot is null || _layer is null) return;
        _layer.Surface.CopyFrom(_snapshot);
        new CurvesEffect(_curve.BuildLut()).Apply(_layer.Surface);
        if (_canvas.Selection is { IsActive: true }) _canvas.Selection.Clip(_layer.Surface, _snapshot);
        _canvas.RenderComposite();
        _canvas.InvalidateVisual();
    }

    private void Commit()
    {
        if (_snapshot is not null && _layer is not null)
        {
            // The debounce means the layer's pixels can lag the curve by up to ~60ms; flush so OK
            // commits the curve that is actually drawn, not a stale in-flight preview.
            _previewTimer?.Stop();
            Preview();

            _canvas.History.Push(TileDeltaMemento.Consume(_layer, _snapshot, "Curves"));
            _snapshot = null;
            _committed = true;
            _canvas.NotifyLayersChanged();
        }
    }

    private void Revert()
    {
        if (_snapshot is not null && _layer is not null)
        {
            _layer.Surface.CopyFrom(_snapshot);
            _canvas.RenderComposite();
            _canvas.InvalidateVisual();
            _snapshot.Dispose();
            _snapshot = null;
        }
    }
}
