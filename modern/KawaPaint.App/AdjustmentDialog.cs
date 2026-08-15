// KawaPaint — a reusable modal for parametric effects with live preview. Given a set of slider
// specs and a factory that turns their values into an IEffect, it previews on the active layer
// (reverting to a snapshot each change, clipped to any selection) and commits one undo step.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class AdjustmentDialog : Window
{
    public sealed record SliderSpec(string Label, double Min, double Max, double Default, string Format);

    private readonly SurfaceView _canvas;
    private readonly Layer? _layer;
    private readonly Slider[] _sliders;
    private readonly Func<double[], IEffect> _build;
    private readonly string _effectName;
    private Surface? _snapshot;
    private bool _committed;

    public AdjustmentDialog(SurfaceView canvas, string title, SliderSpec[] specs, Func<double[], IEffect> build)
    {
        _canvas = canvas;
        _build = build;
        _effectName = title;

        Title = title;
        Width = 400;
        Height = 90 + specs.Length * 46 + 52;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _layer = canvas.ActiveLayer;
        if (_layer is not null) _snapshot = _layer.Surface.Clone();

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        _sliders = new Slider[specs.Length];

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
            slider.ValueChanged += (_, e) => { value.Text = e.NewValue.ToString(fmt); Preview(); };

            _sliders[i] = slider;
            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            root.Children.Add(grid);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) => { for (int i = 0; i < specs.Length; i++) _sliders[i].Value = specs[i].Default; };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { Commit(); Close(); };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        Closed += (_, _) => { if (!_committed) Revert(); };
    }

    private double[] Values()
    {
        var v = new double[_sliders.Length];
        for (int i = 0; i < _sliders.Length; i++) v[i] = _sliders[i].Value;
        return v;
    }

    private void Preview()
    {
        if (_snapshot is null || _layer is null) return;
        _layer.Surface.CopyFrom(_snapshot);
        _build(Values()).Apply(_layer.Surface);
        if (_canvas.Selection is { IsActive: true }) _canvas.Selection.Clip(_layer.Surface, _snapshot);
        _canvas.RenderComposite();
        _canvas.InvalidateVisual();
    }

    private void Commit()
    {
        if (_snapshot is not null && _layer is not null)
        {
            _canvas.History.Push(LayerSurfaceMemento.FromSnapshot(_layer, _snapshot, _effectName));
            _snapshot = null;
            _committed = true;
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
