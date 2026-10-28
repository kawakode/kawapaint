using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

/// <summary>A layer a 3D model can wear as its texture, as offered in <see cref="ThreeDLayerDialog"/>.</summary>
public sealed record TextureChoice(Guid Id, string Name, Surface Surface)
{
    public override string ToString() => Name;
}

/// <summary>
/// Camera, placement and look of a 3D layer, with a real renderer preview framed like the canvas.
/// Used both to import a model (as a live 3D layer or as plain pixels) and to edit a live 3D
/// layer's properties; the final render uses exactly the settings previewed.
/// </summary>
public sealed class ThreeDLayerDialog : Window
{
    private const int PreviewWidth = 420;
    private const int PreviewHeight = 320;

    private readonly ObjMesh _mesh;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly LiveModelSettings _initial;
    private readonly NumericUpDown _yaw;
    private readonly NumericUpDown _pitch;
    private readonly NumericUpDown _roll;
    private readonly NumericUpDown _size;
    private readonly NumericUpDown _x;
    private readonly NumericUpDown _y;
    private readonly NumericUpDown _light;
    private readonly ComboBox _texture;
    private readonly CheckBox? _keepLive;
    private readonly Image _preview;
    private readonly TextBlock _status;
    private WriteableBitmap? _previewBitmap;
    private int _previewRevision;
    private bool _closed;
    private Action<bool>? _canvasClose;

    /// <param name="importing">True for File ▸ Import 3D Model (offers "Keep editable"); false
    /// to edit an existing live layer.</param>
    public ThreeDLayerDialog(ObjMesh mesh, string fileName, int canvasWidth, int canvasHeight,
        LiveModelSettings initial, IReadOnlyList<TextureChoice> textures, bool importing)
    {
        _mesh = mesh;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _initial = initial;
        Title = (importing ? "Import 3D Model — " : "3D Layer — ") + fileName;
        Width = 680;
        Height = 640;
        MinWidth = 540;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _yaw = Field(initial.YawDegrees, -180, 180, 5, "0°");
        _pitch = Field(initial.PitchDegrees, -90, 90, 5, "0°");
        _roll = Field(initial.RollDegrees, -180, 180, 5, "0°");
        float shortSide = Math.Min(canvasWidth, canvasHeight);
        _size = Field(initial.Diameter / shortSide * 100, 1, 2000, 5, "0'%'");
        _x = Field(initial.CenterX, -canvasWidth, canvasWidth * 2, 10, "0");
        _y = Field(initial.CenterY, -canvasHeight, canvasHeight * 2, 10, "0");
        _light = Field(initial.Ambient * 100, 0, 100, 5, "0'%'");
        ToolTip.SetTip(_size, "Width of the model's bounding sphere, as a share of the canvas's shorter side");
        ToolTip.SetTip(_light, "Ambient light: 100% shows the colour or texture without shading");

        var choices = new List<object> { "None" };
        int selected = 0;
        foreach (TextureChoice choice in textures)
        {
            if (choice.Id == initial.TextureLayerId) selected = choices.Count;
            choices.Add(choice);
        }
        _texture = new ComboBox
        {
            ItemsSource = choices,
            SelectedIndex = selected,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = mesh.HasTexCoords && textures.Count > 0
        };
        ToolTip.SetTip(_texture, mesh.HasTexCoords
            ? "A layer the model wears as its texture; the 3D layer follows its changes"
            : "This model has no texture coordinates");

        _preview = new Image { Stretch = Stretch.Uniform };
        _status = new TextBlock
        {
            Text = $"{mesh.Vertices.Count:N0} vertices · {mesh.Triangles.Count:N0} triangles",
            Foreground = Brushes.Gray
        };

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 6
        };
        AddField(fields, "Yaw", _yaw, 0, 0);
        AddField(fields, "Pitch", _pitch, 0, 2);
        AddField(fields, "Roll", _roll, 0, 4);
        AddField(fields, "Size", _size, 1, 0);
        AddField(fields, "X", _x, 1, 2);
        AddField(fields, "Y", _y, 1, 4);
        AddField(fields, "Light", _light, 2, 0);
        AddField(fields, "Texture", _texture, 2, 2);
        Grid.SetColumnSpan(_texture, 3);

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 75, 75)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            MinHeight = 200,
            Child = _preview
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Finish(false);
        var ok = new Button { Content = importing ? "Import" : "OK", IsDefault = true };
        ok.Click += (_, _) => Finish(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (importing)
        {
            _keepLive = new CheckBox
            {
                Content = "Keep editable (live 3D layer)",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(_keepLive, "Keep the model in the project so its pose, size and texture can be " +
                "changed later. Off: plain pixels, like any other layer.");
            buttons.Children.Add(_keepLive);
        }
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Content = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
            Children = { fields, previewBorder, _status, buttons }
        };
        Grid.SetRow(previewBorder, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(buttons, 3);

        foreach (NumericUpDown field in new[] { _yaw, _pitch, _roll, _size, _x, _y, _light })
            field.ValueChanged += (_, _) => QueuePreview();
        _texture.SelectionChanged += (_, _) => QueuePreview();
        Closed += (_, _) => StopPreview();
        QueuePreview();
    }

    /// <summary>True once OK/Import was pressed (either host).</summary>
    public bool Accepted { get; private set; }

    /// <summary>Whether an import should create a live 3D layer rather than plain pixels.</summary>
    public bool KeepLive => _keepLive?.IsChecked ?? true;

    public LiveModelSettings ResultSettings
    {
        get
        {
            float shortSide = Math.Min(_canvasWidth, _canvasHeight);
            return _initial with
            {
                YawDegrees = Value(_yaw, _initial.YawDegrees),
                PitchDegrees = Value(_pitch, _initial.PitchDegrees),
                RollDegrees = Value(_roll, _initial.RollDegrees),
                Diameter = Math.Max(1, Value(_size, _initial.Diameter / shortSide * 100) / 100f * shortSide),
                CenterX = Value(_x, _initial.CenterX),
                CenterY = Value(_y, _initial.CenterY),
                Ambient = Value(_light, _initial.Ambient * 100) / 100f,
                TextureLayerId = (_texture.SelectedItem as TextureChoice)?.Id
            };
        }
    }

    /// <summary>Surface of the chosen texture layer, or null.</summary>
    public Surface? ResultTexture => (_texture.SelectedItem as TextureChoice)?.Surface;

    public void UseCanvasHost(Action<bool> close) => _canvasClose = close;
    public void BeginCanvasHost() { }
    public void CancelCanvasHost() => StopPreview();

    private void Finish(bool accepted)
    {
        Accepted = accepted;
        if (_canvasClose is null) Close(accepted);
        else
        {
            StopPreview();
            _canvasClose(accepted);
        }
    }

    private void StopPreview()
    {
        _closed = true;
        _previewRevision++;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }

    /// <summary>The field's value, or exactly <paramref name="initial"/> while the field still shows
    /// it rounded, so OK without a change reproduces the current settings bit for bit.</summary>
    private static float Value(NumericUpDown field, float initial)
    {
        if (field.Value is not { } value || value == Shown(initial, field.Minimum, field.Maximum)) return initial;
        return (float)value;
    }

    private static decimal Shown(float value, decimal min, decimal max) =>
        Math.Clamp((decimal)Math.Round(value, 2), min, max);

    private static NumericUpDown Field(float value, decimal min, decimal max, decimal increment, string format) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Shown(value, min, max),
        Increment = increment,
        FormatString = format,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static void AddField(Grid grid, string label, Control control, int row, int column)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, column);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column + 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private async void QueuePreview()
    {
        if (_closed) return;
        int revision = ++_previewRevision;
        // The preview is the canvas scaled into the preview box, so placement reads 1:1.
        float scale = Math.Min((float)PreviewWidth / _canvasWidth, (float)PreviewHeight / _canvasHeight);
        int width = Math.Max(1, (int)Math.Round(_canvasWidth * scale));
        int height = Math.Max(1, (int)Math.Round(_canvasHeight * scale));
        LiveModelSettings settings = ResultSettings;
        settings = settings with
        {
            CenterX = settings.CenterX * scale,
            CenterY = settings.CenterY * scale,
            Diameter = settings.Diameter * scale
        };
        Surface? texture = ResultTexture?.Clone();
        _status.Text = "Rendering preview…";
        try
        {
            using Surface rendered = await Task.Run(() =>
            {
                try { return LiveModelRenderer.Render(new LiveModel(_mesh, "", settings), width, height, texture); }
                finally { texture?.Dispose(); }
            });
            if (_closed || revision != _previewRevision) return;

            WriteableBitmap bitmap = CopyToBitmap(rendered);
            WriteableBitmap? old = _previewBitmap;
            _previewBitmap = bitmap;
            _preview.Source = bitmap;
            if (old is not null)
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
            _status.Text = $"{_mesh.Vertices.Count:N0} vertices · {_mesh.Triangles.Count:N0} triangles";
        }
        catch (Exception ex)
        {
            if (!_closed && revision == _previewRevision) _status.Text = "Preview failed: " + ex.Message;
        }
    }

    private static unsafe WriteableBitmap CopyToBitmap(Surface source)
    {
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer target = bitmap.Lock();
        int bytes = source.Width * ColorBgra.SizeOf;
        for (int y = 0; y < source.Height; y++)
            Buffer.MemoryCopy(source.GetRowPointer(y), (byte*)target.Address + (long)y * target.RowBytes,
                target.RowBytes, bytes);
        return bitmap;
    }
}
