// KawaPaint - the settings of Image > Remove Background. A picture of the layer doubles as the
// eyedropper: clicking it adds the colour under the pointer to the background colours (the dialog
// is modal, so the canvas itself can't be clicked meanwhile). Every change goes to the caller's
// preview callback (debounced); "Defaults" puts every setting back to the automatic ones.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class RemoveBackgroundDialog : AppDialog
{
    /// <summary>More picked colours than this replace the oldest.</summary>
    public const int MaxColors = 8;

    private const int PictureSize = 300;

    private readonly Surface _picture;          // the layer, scaled to fit the picture box
    private readonly Image _image;
    private readonly ComboBox _mode;
    private readonly WrapPanel _swatches;
    private readonly TextBlock _colorsNote;
    private readonly Button _clearColors;
    private readonly Slider _tolerance;
    private readonly TextBlock _toleranceValue;
    private readonly CheckBox _enclosed;
    private readonly CheckBox _cleanEdges;
    private readonly Slider _shrink;
    private readonly TextBlock _shrinkValue;
    private readonly Slider _feather;
    private readonly TextBlock _featherValue;
    private readonly TextBlock _status;
    private readonly List<ColorBgra> _colors = new();
    private readonly Action<BackgroundOptions> _preview;
    private DispatcherTimer? _previewTimer;
    private bool _toleranceSet;      // false: the automatic tolerance, whatever the slider shows
    private bool _resetting;
    private double _autoTolerance;

    public BackgroundOptions Result => new()
    {
        Mode = _mode.SelectedIndex == 1 ? BackgroundMode.ColorsOnly : BackgroundMode.Subject,
        Colors = _colors.ToArray(),
        Tolerance = _toleranceSet ? Math.Round(_tolerance.Value) : null,
        RemoveEnclosed = _enclosed.IsChecked == true,
        CleanEdges = _cleanEdges.IsChecked == true,
        ShrinkEdge = (int)Math.Round(_shrink.Value),
        Feather = (int)Math.Round(_feather.Value)
    };

    /// <param name="source">The layer's pixels; only read while the dialog is built.</param>
    /// <param name="autoTolerance">The tolerance the automatic setting stands for, to show on the slider.</param>
    public RemoveBackgroundDialog(string description, Surface source, double autoTolerance, Action<BackgroundOptions> preview)
    {
        _preview = preview;
        _autoTolerance = autoTolerance;
        Title = "Remove Background";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        root.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        // --- The picture / eyedropper ---------------------------------------------------------
        double scale = Math.Min(1.0, Math.Min((double)PictureSize / source.Width, (double)PictureSize / source.Height));
        int pw = Math.Max(1, (int)Math.Round(source.Width * scale)), ph = Math.Max(1, (int)Math.Round(source.Height * scale));
        _picture = source.Resized(pw, ph);
        _image = new Image
        {
            Name = "BackgroundPicker",
            Source = ToBitmap(_picture),
            Width = pw, Height = ph,
            Stretch = Stretch.Fill,
            Cursor = new Cursor(StandardCursorType.Cross)
        };
        ToolTip.SetTip(_image, "Click the background to pick its color; click again to add more colors");
        _image.PointerPressed += OnPick;
        var frame = new Border
        {
            Child = _image,
            Background = Checkerboard(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        root.Children.Add(frame);

        // --- Background colours ---------------------------------------------------------------
        _swatches = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _colorsNote = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        _clearColors = new Button { Content = "Automatic", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(_clearColors, "Forget the picked colors and find the background from the image's borders again");
        _clearColors.Click += (_, _) => { _colors.Clear(); UpdateSwatches(); SchedulePreview(); };
        var colorRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var colorStack = new StackPanel { Spacing = 4 };
        colorStack.Children.Add(_swatches);
        colorStack.Children.Add(_colorsNote);
        Grid.SetColumn(_clearColors, 1);
        colorRow.Children.Add(colorStack);
        colorRow.Children.Add(_clearColors);
        root.Children.Add(Row("Background", colorRow));

        _mode = new ComboBox { Name = "BackgroundMode", HorizontalAlignment = HorizontalAlignment.Stretch };
        _mode.Items.Add("Find the subject");
        _mode.Items.Add("Background colors only");
        _mode.SelectedIndex = 0;
        ToolTip.SetTip(_mode, "Find the subject: keeps the main subject(s) and clears what is left of the background around and inside them.\n"
            + "Background colors only: removes every pixel of the background colors, without looking for a subject (like a magic eraser on the whole image).");
        _mode.SelectionChanged += (_, _) => SchedulePreview();
        root.Children.Add(Row("Method", _mode));

        // --- Tolerance --------------------------------------------------------------------------
        _toleranceValue = ValueText();
        _tolerance = new Slider { Name = "BackgroundTolerance", Minimum = 0, Maximum = 100, Value = autoTolerance };
        _tolerance.ValueChanged += (_, e) =>
        {
            if (!_resetting) _toleranceSet = true;
            UpdateToleranceText();
            SchedulePreview();
        };
        ToolTip.SetTip(_tolerance, "How different from the background colors a pixel may be and still be removed. Higher removes more.");
        root.Children.Add(Row("Tolerance", SliderRow(_tolerance, _toleranceValue)));

        _enclosed = new CheckBox { Content = "Also clear enclosed areas and specks", IsChecked = true };
        ToolTip.SetTip(_enclosed, "Removes the background showing inside the subject: inside letters, between lines, through a worn stamp");
        _enclosed.IsCheckedChanged += (_, _) => SchedulePreview();
        root.Children.Add(_enclosed);

        _cleanEdges = new CheckBox { Content = "Clean edge colors", IsChecked = true };
        ToolTip.SetTip(_cleanEdges, "Takes the background color out of the soft edge pixels, so no pale or dark fringe is left");
        _cleanEdges.IsCheckedChanged += (_, _) => SchedulePreview();
        root.Children.Add(_cleanEdges);

        _shrinkValue = ValueText();
        _shrink = new Slider { Minimum = 0, Maximum = 10, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true };
        _shrink.ValueChanged += (_, _) => { _shrinkValue.Text = $"{_shrink.Value:0} px"; SchedulePreview(); };
        ToolTip.SetTip(_shrink, "Pulls the edge in, to drop a halo that remains");
        root.Children.Add(Row("Shrink edge", SliderRow(_shrink, _shrinkValue)));

        _featherValue = ValueText();
        _feather = new Slider { Minimum = 0, Maximum = 10, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true };
        _feather.ValueChanged += (_, _) => { _featherValue.Text = $"{_feather.Value:0} px"; SchedulePreview(); };
        ToolTip.SetTip(_feather, "Softens the edge");
        root.Children.Add(Row("Feather", SliderRow(_feather, _featherValue)));
        _shrinkValue.Text = "0 px";
        _featherValue.Text = "0 px";

        _status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MinHeight = 16 };
        root.Children.Add(_status);

        var defaults = new Button { Content = "Defaults", Name = "BackgroundDefaults" };
        ToolTip.SetTip(defaults, "Back to the automatic settings");
        defaults.Click += (_, _) => ResetToDefaults();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { _previewTimer?.Stop(); Close(true); };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(ok, 3);
        buttons.Children.Add(defaults);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        UpdateSwatches();
        UpdateToleranceText();
        Content = new ScrollViewer { Content = root };
        Opened += (_, _) => _preview(Result);
        Closed += (_, _) => { _previewTimer?.Stop(); _picture.Dispose(); };
    }

    /// <summary>Shown under the settings: what the preview found, or that it is still working.</summary>
    public void SetStatus(string text) => _status.Text = text;

    /// <summary>The automatic tolerance, once known for the current background colours.</summary>
    public void SetAutoTolerance(double value)
    {
        _autoTolerance = value;
        if (_toleranceSet) return;
        _resetting = true;
        _tolerance.Value = value;
        _resetting = false;
        UpdateToleranceText();
    }

    /// <summary>Adds a background colour, as a click on the picture does.</summary>
    public void AddColor(ColorBgra color)
    {
        color.A = 255;
        if (_colors.Any(c => c.R == color.R && c.G == color.G && c.B == color.B)) return;
        if (_colors.Count >= MaxColors) _colors.RemoveAt(0);
        _colors.Add(color);
        UpdateSwatches();
        SchedulePreview();
    }

    public void ResetToDefaults()
    {
        _resetting = true;
        _colors.Clear();
        _mode.SelectedIndex = 0;
        _toleranceSet = false;
        _tolerance.Value = _autoTolerance;
        _enclosed.IsChecked = BackgroundOptions.Default.RemoveEnclosed;
        _cleanEdges.IsChecked = BackgroundOptions.Default.CleanEdges;
        _shrink.Value = BackgroundOptions.Default.ShrinkEdge;
        _feather.Value = BackgroundOptions.Default.Feather;
        _resetting = false;
        UpdateSwatches();
        UpdateToleranceText();
        SchedulePreview();
    }

    private void OnPick(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_image);
        int x = Math.Clamp((int)(p.X / Math.Max(1, _image.Bounds.Width) * _picture.Width), 0, _picture.Width - 1);
        int y = Math.Clamp((int)(p.Y / Math.Max(1, _image.Bounds.Height) * _picture.Height), 0, _picture.Height - 1);
        // A 3x3 average, so a click on noise or JPEG grain still gets the backdrop's colour.
        int r = 0, g = 0, b = 0, n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if ((uint)xx >= (uint)_picture.Width || (uint)yy >= (uint)_picture.Height) continue;
                var c = _picture[xx, yy];
                if (c.A == 0) continue;
                r += c.R; g += c.G; b += c.B; n++;
            }
        if (n == 0) return;   // already transparent: nothing to pick
        AddColor(ColorBgra.FromBgr((byte)(b / n), (byte)(g / n), (byte)(r / n)));
        e.Handled = true;
    }

    private void UpdateSwatches()
    {
        _swatches.Children.Clear();
        foreach (var color in _colors.ToArray())
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(swatch, $"#{color.R:X2}{color.G:X2}{color.B:X2} - click to remove");
            var c = color;
            swatch.Click += (_, _) =>
            {
                _colors.RemoveAll(k => k.R == c.R && k.G == c.G && k.B == c.B);
                UpdateSwatches();
                SchedulePreview();
            };
            _swatches.Children.Add(swatch);
        }
        _swatches.IsVisible = _colors.Count > 0;
        _clearColors.IsVisible = _colors.Count > 0;
        _colorsNote.Text = _colors.Count == 0
            ? "Automatic, from the image's borders. Click the picture to pick it yourself."
            : "Click the picture to add a color, a swatch to remove it.";
    }

    private void UpdateToleranceText()
        => _toleranceValue.Text = _toleranceSet ? $"{_tolerance.Value:0}" : $"Auto ({_tolerance.Value:0})";

    private static TextBlock ValueText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right
    };

    private static Control SliderRow(Slider slider, TextBlock value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(value, 1);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }

    private static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = 8 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Coalesces a burst of changes (a slider drag) into one preview ~150 ms after the last.</summary>
    private void SchedulePreview()
    {
        if (_resetting) return;
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _previewTimer.Tick += (_, _) => { _previewTimer!.Stop(); _preview(Result); };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private static unsafe WriteableBitmap ToBitmap(Surface s)
    {
        var bitmap = new WriteableBitmap(new PixelSize(s.Width, s.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bitmap.Lock();
        for (int y = 0; y < s.Height; y++)
            Buffer.MemoryCopy(s.GetRowPointer(y), (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, s.Width * 4);
        return bitmap;
    }

    private static IBrush Checkerboard()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = Brushes.White, Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) });
        group.Children.Add(new GeometryDrawing { Brush = light, Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) });
        group.Children.Add(new GeometryDrawing { Brush = light, Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) });
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute)
        };
    }
}
