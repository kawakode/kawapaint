// KawaPaint - a reusable modal for parametric effects with live preview. Given a set of slider
// specs and a factory that turns their values into an IEffect, it previews on the active layer
// (reverting to a snapshot each change, clipped to any selection) and commits one undo step.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class AdjustmentDialog : Window
{
    public sealed record SliderSpec(string Label, double Min, double Max, double Default, string Format);
    public sealed record CheckboxSpec(string Label, bool Default = false);

    private readonly SurfaceView _canvas;
    private readonly Layer? _layer;
    private readonly Slider[] _sliders;
    private readonly CheckBox[] _checkboxes;
    private readonly CheckboxSpec[] _checkboxSpecs;
    private readonly double[] _replayArgs;
    private readonly Func<double[], IEffect> _build;
    private readonly string _effectName;
    private Surface? _snapshot;
    private bool _committed;
    private DispatcherTimer? _previewTimer;
    private Action<bool>? _canvasClose;
    private EffectBounds _previewBounds;

    /// <summary>The slider values this dialog was committed with, or null if it was cancelled/
    /// closed without OK. Read by OnAdjust to record the exact effect parameters in demos and
    /// scripts; hidden replay-only arguments such as a random seed are appended after controls.</summary>
    public double[]? CommittedValues { get; private set; }

    public AdjustmentDialog(SurfaceView canvas, string title, SliderSpec[] specs, Func<double[], IEffect> build,
        CheckboxSpec[]? checkboxes = null, double[]? replayArgs = null)
    {
        _canvas = canvas;
        _build = build;
        _effectName = title;
        _checkboxSpecs = checkboxes ?? Array.Empty<CheckboxSpec>();
        _replayArgs = replayArgs ?? Array.Empty<double>();

        Title = title;
        Width = 400;
        // Was a hand-tuned "90 + sliders*46 + 52" guess at the chrome; let the layout say instead,
        // so a font or DPI that measures differently can't clip the button row.
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _layer = canvas.ActiveLayer;
        if (_layer is not null) _snapshot = _layer.Surface.Clone();

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        _sliders = new Slider[specs.Length];
        _checkboxes = new CheckBox[_checkboxSpecs.Length];

        for (int i = 0; i < specs.Length; i++)
        {
            SliderSpec spec = specs[i];
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,52") };

            var label = new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);

            var slider = new Slider { Minimum = spec.Min, Maximum = spec.Max, Value = spec.Default };
            Grid.SetColumn(slider, 1);

            var value = new TextBlock
            {
                Text = spec.Default.ToString(spec.Format),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(value, 2);

            string fmt = spec.Format;
            // The numeric readout updates immediately (cheap); the actual pixel preview is
            // debounced (see SchedulePreview) so dragging doesn't queue a viewport Apply for
            // every intermediate value a fast drag passes through.
            slider.ValueChanged += (_, e) => { value.Text = e.NewValue.ToString(fmt); SchedulePreview(); };

            _sliders[i] = slider;
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            root.Children.Add(grid);
        }

        for (int i = 0; i < _checkboxSpecs.Length; i++)
        {
            CheckboxSpec spec = _checkboxSpecs[i];
            var checkbox = new CheckBox { Content = spec.Label, IsChecked = spec.Default };
            checkbox.IsCheckedChanged += (_, _) => SchedulePreview();
            _checkboxes[i] = checkbox;
            root.Children.Add(checkbox);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) =>
        {
            for (int i = 0; i < specs.Length; i++) _sliders[i].Value = specs[i].Default;
            for (int i = 0; i < _checkboxSpecs.Length; i++) _checkboxes[i].IsChecked = _checkboxSpecs[i].Default;
        };
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
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;

        // Most of these effects have a non-neutral default (Add Noise 25, Pixelate 8, Gaussian
        // Blur 5, ...). Without this the canvas showed the *unmodified* image until a slider was
        // touched, so a straight OK applied a result the user had never actually been shown - and
        // any nudge made the image jump. Preview on open so what is on screen always matches what
        // OK will commit. Deferred to Opened so the dialog is up before the first viewport-bounded
        // Apply, rather than the window appearing to hang on a large image.
        Opened += (_, _) => Preview();

        Closed += (_, _) => { _previewTimer?.Stop(); if (!_committed) Revert(); };
    }

    public void UseCanvasHost(Action<bool> close) => _canvasClose = close;
    public void BeginCanvasHost() => Preview();
    public void CancelCanvasHost()
    {
        _previewTimer?.Stop();
        if (!_committed) Revert();
    }

    private double[] Values()
    {
        var v = new double[_sliders.Length + _checkboxes.Length];
        for (int i = 0; i < _sliders.Length; i++) v[i] = _sliders[i].Value;
        for (int i = 0; i < _checkboxes.Length; i++) v[_sliders.Length + i] = _checkboxes[i].IsChecked == true ? 1 : 0;
        return v;
    }

    /// <summary>
    /// Coalesces a burst of ValueChanged events (a slider drag can fire dozens per second) into one
    /// Preview() call ~60ms after the last change - short enough to still feel live, but it caps a
    /// fast drag to at most ~16 viewport recomputations/sec instead of one per intermediate
    /// value, which is what used to queue seconds of work on a large image.
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
        EffectBounds bounds = _canvas.VisibleImageBounds;
        EffectBounds dirty = _previewBounds.Union(bounds).Clip(_layer.Surface);
        _layer.Surface.CopyRectFrom(_snapshot, dirty.X, dirty.Y, dirty.Width, dirty.Height);
        _build(Values()).Apply(_layer.Surface, bounds);
        if (_canvas.Selection is { IsActive: true })
            _canvas.Selection.Clip(_layer.Surface, _snapshot, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _canvas.RenderComposite(dirty.X, dirty.Y, dirty.Width, dirty.Height);
        _previewBounds = bounds;
        _canvas.InvalidateVisual();
    }

    private void Commit()
    {
        if (_snapshot is not null && _layer is not null)
        {
            // The debounce above means the visible preview can lag the sliders by up to ~60ms.
            // Rebuild the complete result synchronously so OK commits every pixel at final values.
            _previewTimer?.Stop();
            _layer.Surface.CopyFrom(_snapshot);
            _build(Values()).Apply(_layer.Surface);
            if (_canvas.Selection is { IsActive: true }) _canvas.Selection.Clip(_layer.Surface, _snapshot);
            _canvas.RenderComposite();
            CommittedValues = Values().Concat(_replayArgs).ToArray();

            _canvas.History.Push(TileDeltaMemento.Consume(_layer, _snapshot, _effectName));
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
