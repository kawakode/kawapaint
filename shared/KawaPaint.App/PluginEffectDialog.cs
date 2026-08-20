// KawaPaint — a data-driven modal for plugin effects, deliberately parallel to (not sharing code
// with) AdjustmentDialog: same snapshot/Preview/Commit/Revert lifecycle and one undo step on OK,
// but the per-row control is chosen from the runtime type of each PluginParameterSpec instead of
// AdjustmentDialog's numeric-only SliderSpec, so a plugin can expose a checkbox/dropdown/color in
// addition to a slider. AdjustmentDialog itself is untouched — built-in effects keep working
// exactly as they do today.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.App;

public sealed class PluginEffectDialog : Window
{
    private readonly SurfaceView _canvas;
    private readonly Layer? _layer;
    private readonly PluginEffectDescriptor _descriptor;
    private readonly Dictionary<string, Func<object>> _readers = new();
    private Surface? _snapshot;
    private bool _committed;
    private DispatcherTimer? _previewTimer;

    public PluginEffectDialog(SurfaceView canvas, PluginEffectDescriptor descriptor)
    {
        _canvas = canvas;
        _descriptor = descriptor;

        Title = descriptor.DisplayName;
        Width = 400;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _layer = canvas.ActiveLayer;
        if (_layer is not null) _snapshot = _layer.Surface.Clone();

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };

        foreach (var spec in descriptor.Parameters)
            root.Children.Add(BuildRow(spec));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { Commit(); Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Height = 90 + descriptor.Parameters.Count * 46 + 52;
        Content = root;
        Closed += (_, _) => { _previewTimer?.Stop(); if (!_committed) Revert(); };
    }

    private Control BuildRow(PluginParameterSpec spec) => spec switch
    {
        NumericParameterSpec n => BuildSliderRow(n),
        BoolParameterSpec b => BuildCheckBoxRow(b),
        ChoiceParameterSpec c => BuildComboRow(c),
        ColorParameterSpec c => BuildColorRow(c),
        _ => throw new NotSupportedException($"Unknown parameter spec type {spec.GetType()}")
    };

    private Control BuildSliderRow(NumericParameterSpec spec)
    {
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
        // The numeric readout updates immediately (cheap); the pixel preview is debounced (see
        // SchedulePreview) so dragging doesn't queue a full-surface Apply per intermediate value.
        slider.ValueChanged += (_, e) => { value.Text = e.NewValue.ToString(fmt); SchedulePreview(); };

        _readers[spec.Key] = () => slider.Value;
        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private Control BuildCheckBoxRow(BoolParameterSpec spec)
    {
        var check = new CheckBox { Content = spec.Label, IsChecked = spec.Default };
        check.IsCheckedChanged += (_, _) => SchedulePreview();
        _readers[spec.Key] = () => check.IsChecked ?? false;
        return check;
    }

    private Control BuildComboRow(ChoiceParameterSpec spec)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };

        var label = new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);

        var combo = new ComboBox { ItemsSource = spec.Options, SelectedIndex = spec.DefaultIndex };
        Grid.SetColumn(combo, 1);
        combo.SelectionChanged += (_, _) => SchedulePreview();

        _readers[spec.Key] = () => combo.SelectedIndex;
        grid.Children.Add(label);
        grid.Children.Add(combo);
        return grid;
    }

    private Control BuildColorRow(ColorParameterSpec spec)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };

        var label = new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);

        var button = new ColorPicker
        {
            Color = Color.FromArgb(spec.Default.A, spec.Default.R, spec.Default.G, spec.Default.B),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(button, 1);
        button.ColorChanged += (_, _) => SchedulePreview();

        _readers[spec.Key] = () =>
        {
            var c = button.Color;
            return ColorBgra.FromBgra(c.B, c.G, c.R, c.A);
        };
        grid.Children.Add(label);
        grid.Children.Add(button);
        return grid;
    }

    private PluginParameterValues Values()
    {
        var values = new Dictionary<string, object>();
        foreach (var (key, read) in _readers) values[key] = read();
        return new PluginParameterValues(values);
    }

    /// <summary>
    /// Coalesces a burst of parameter changes (a slider drag, or dragging within the color picker's
    /// wheel) into one Preview() call ~60ms after the last change, capping a fast drag to at most
    /// ~16 full-surface recomputations/sec instead of one per intermediate value.
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
        _descriptor.Build(Values()).Apply(_layer.Surface);
        if (_canvas.Selection is { IsActive: true }) _canvas.Selection.Clip(_layer.Surface, _snapshot);
        _canvas.RenderComposite();
        _canvas.InvalidateVisual();
    }

    private void Commit()
    {
        if (_snapshot is not null && _layer is not null)
        {
            // The debounce above means the layer's current pixels can lag the controls' final
            // values by up to ~60ms — flush synchronously so OK always commits what's actually
            // shown, not a stale in-flight preview.
            _previewTimer?.Stop();
            Preview();

            _canvas.History.Push(TileDeltaMemento.Consume(_layer, _snapshot, _descriptor.DisplayName));
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
