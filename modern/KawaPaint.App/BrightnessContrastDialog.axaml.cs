using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class BrightnessContrastDialog : Window
{
    private readonly SurfaceView _canvas;
    private readonly Layer _layer;
    private Surface? _snapshot;   // pre-edit state; owned here until OK transfers it to history
    private bool _committed;

    // Parameterless ctor for the XAML designer / loader.
    public BrightnessContrastDialog() : this(null!) { }

    public BrightnessContrastDialog(SurfaceView canvas)
    {
        InitializeComponent();
        _canvas = canvas;
        _layer = canvas?.ActiveLayer!;
        if (_layer is not null)
            _snapshot = _layer.Surface.Clone();

        Closed += (_, _) => { if (!_committed) RevertAndCleanup(); };
    }

    private void OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_snapshot is null || _layer is null) return;

        int brightness = (int)Math.Round(BrightnessSlider.Value);
        double contrast = ContrastSlider.Value / 100.0;
        BrightnessLabel.Text = brightness.ToString();
        ContrastLabel.Text = contrast.ToString("0.00");

        _layer.Surface.CopyFrom(_snapshot);
        new BrightnessContrastEffect(brightness, contrast).Apply(_layer.Surface);
        _canvas.RenderComposite();
        _canvas.InvalidateVisual();
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 100;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_snapshot is not null && _layer is not null)
        {
            // The layer already holds the previewed result; record the pre-edit snapshot for undo.
            _canvas.History.Push(LayerSurfaceMemento.FromSnapshot(_layer, _snapshot, "Brightness / Contrast"));
            _snapshot = null;   // ownership transferred to the memento
            _committed = true;
        }
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void RevertAndCleanup()
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
