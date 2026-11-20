// KawaPaint - asks for the settings of Image > Stroke: width, position against the edge, a colour or
// a two-colour gradient, opacity, and whether to draw on a new layer. Every change goes to the
// caller's preview callback (debounced), and the caller undoes the preview on Cancel.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class StrokeDialog : AppDialog
{
    private static readonly (StrokePosition Value, string Label)[] Positions =
    {
        (StrokePosition.Outside, "Outside"), (StrokePosition.Center, "Centered"), (StrokePosition.Inside, "Inside")
    };

    private static readonly (StrokeFill Value, string Label)[] Fills =
    {
        (StrokeFill.Solid, "Solid color"),
        (StrokeFill.Horizontal, "Gradient: left to right"),
        (StrokeFill.Vertical, "Gradient: top to bottom"),
        (StrokeFill.Diagonal, "Gradient: diagonal"),
        (StrokeFill.Radial, "Gradient: from the center"),
        (StrokeFill.Across, "Gradient: inner to outer edge")
    };

    private readonly NumericUpDown _width;
    private readonly Slider _widthSlider;
    private readonly ComboBox _position;
    private readonly ComboBox _fill;
    private readonly ColorPicker _color1;
    private readonly ColorPicker _color2;
    private readonly Control _color2Row;
    private readonly Slider _opacity;
    private readonly CheckBox _newLayer;
    private readonly Action<StrokeOptions, bool> _preview;
    private DispatcherTimer? _previewTimer;
    private bool _syncingWidth;

    public StrokeOptions Result => new()
    {
        Width = Math.Clamp((double)(_width.Value ?? 1), 1, StrokeOps.MaxWidth),
        Position = Positions[Math.Max(0, _position.SelectedIndex)].Value,
        Fill = Fills[Math.Max(0, _fill.SelectedIndex)].Value,
        Color1 = ToBgra(_color1.Color),
        Color2 = ToBgra(_color2.Color),
        Opacity = _opacity.Value / 100
    };

    public bool ResultOnNewLayer => _newLayer.IsChecked == true;

    public StrokeDialog(string sourceDescription, StrokeOptions initial, bool onNewLayer, Action<StrokeOptions, bool> preview)
    {
        _preview = preview;
        Title = "Stroke";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        root.Children.Add(new TextBlock { Text = sourceDescription, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        double width = Math.Clamp(initial.Width, 1, StrokeOps.MaxWidth);
        _width = new NumericUpDown
        {
            Name = "StrokeWidth", Minimum = 1, Maximum = StrokeOps.MaxWidth, Increment = 1,
            Value = (decimal)width, FormatString = "0", MinWidth = 130
        };
        // The slider covers the usual range for a quick drag; the box takes anything up to the max.
        _widthSlider = new Slider { Minimum = 1, Maximum = 100, Value = Math.Min(width, 100) };
        _width.ValueChanged += (_, _) =>
        {
            if (_syncingWidth) return;
            _syncingWidth = true;
            _widthSlider.Value = Math.Min((double)(_width.Value ?? 1), 100);
            _syncingWidth = false;
            SchedulePreview();
        };
        _widthSlider.ValueChanged += (_, e) =>
        {
            if (_syncingWidth) return;
            _syncingWidth = true;
            _width.Value = (decimal)Math.Round(e.NewValue);
            _syncingWidth = false;
            SchedulePreview();
        };
        var widthRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_width, 1);
        widthRow.Children.Add(_widthSlider);
        widthRow.Children.Add(_width);
        root.Children.Add(Row("Width (pixels)", widthRow));

        _position = new ComboBox { Name = "StrokePosition", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in Positions) _position.Items.Add(label);
        _position.SelectedIndex = Math.Max(0, Array.FindIndex(Positions, p => p.Value == initial.Position));
        _position.SelectionChanged += (_, _) => SchedulePreview();
        root.Children.Add(Row("Position", _position));

        _fill = new ComboBox { Name = "StrokeFill", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in Fills) _fill.Items.Add(label);
        _fill.SelectedIndex = Math.Max(0, Array.FindIndex(Fills, f => f.Value == initial.Fill));
        root.Children.Add(Row("Fill", _fill));

        _color1 = new ColorPicker { Color = ToColor(initial.Color1), HorizontalAlignment = HorizontalAlignment.Left };
        _color1.ColorChanged += (_, _) => SchedulePreview();
        root.Children.Add(Row("Color", _color1));

        _color2 = new ColorPicker { Color = ToColor(initial.Color2), HorizontalAlignment = HorizontalAlignment.Left };
        _color2.ColorChanged += (_, _) => SchedulePreview();
        _color2Row = Row("Second color", _color2);
        root.Children.Add(_color2Row);

        _fill.SelectionChanged += (_, _) => { UpdateColorRows(); SchedulePreview(); };
        UpdateColorRows();

        var opacityValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Right };
        _opacity = new Slider { Minimum = 0, Maximum = 100, Value = Math.Clamp(initial.Opacity * 100, 0, 100) };
        opacityValue.Text = $"{_opacity.Value:0}%";
        _opacity.ValueChanged += (_, e) => { opacityValue.Text = $"{e.NewValue:0}%"; SchedulePreview(); };
        var opacityRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(opacityValue, 1);
        opacityRow.Children.Add(_opacity);
        opacityRow.Children.Add(opacityValue);
        root.Children.Add(Row("Opacity", opacityRow));

        _newLayer = new CheckBox
        {
            Content = "Draw on a new layer",
            IsChecked = onNewLayer
        };
        ToolTip.SetTip(_newLayer, "Keeps the contour apart from the image, so it can be moved, hidden or deleted later");
        _newLayer.IsCheckedChanged += (_, _) => SchedulePreview();
        root.Children.Add(_newLayer);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { _previewTimer?.Stop(); Close(true); };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root };
        Opened += (_, _) => _preview(Result, ResultOnNewLayer);
        Closed += (_, _) => _previewTimer?.Stop();
    }

    private void UpdateColorRows()
    {
        bool gradient = Fills[Math.Max(0, _fill.SelectedIndex)].Value != StrokeFill.Solid;
        _color2Row.IsVisible = gradient;
    }

    private static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 8 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Coalesces a burst of changes (a slider drag) into one preview ~60 ms after the last.</summary>
    private void SchedulePreview()
    {
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _previewTimer.Tick += (_, _) => { _previewTimer!.Stop(); _preview(Result, ResultOnNewLayer); };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private static Color ToColor(ColorBgra c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    private static ColorBgra ToBgra(Color c) => ColorBgra.FromBgra(c.B, c.G, c.R, c.A);
}
