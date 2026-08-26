using System;
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

/// <summary>Camera pose picker with a real renderer preview; the final import uses the same options.</summary>
public sealed class ThreeDImportDialog : Window
{
    private const int PreviewWidth = 420;
    private const int PreviewHeight = 320;

    private readonly ObjMesh _mesh;
    private readonly NumericUpDown _yaw;
    private readonly NumericUpDown _pitch;
    private readonly NumericUpDown _roll;
    private readonly Image _preview;
    private readonly TextBlock _status;
    private WriteableBitmap? _previewBitmap;
    private int _previewRevision;
    private bool _closed;

    public ReferenceRenderOptions ResultOptions => new()
    {
        YawDegrees = (float)(_yaw.Value ?? -35),
        PitchDegrees = (float)(_pitch.Value ?? -25),
        RollDegrees = (float)(_roll.Value ?? 0)
    };

    public ThreeDImportDialog(ObjMesh mesh, string fileName)
    {
        _mesh = mesh;
        Title = "Import 3D Reference — " + fileName;
        Width = 650;
        Height = 540;
        MinWidth = 520;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _yaw = Angle(-35);
        _pitch = Angle(-25, -90, 90);
        _roll = Angle(0);
        _preview = new Image { Stretch = Stretch.Uniform };
        _status = new TextBlock
        {
            Text = $"{mesh.Vertices.Count:N0} vertices · {mesh.Triangles.Count:N0} triangles",
            Foreground = Brushes.Gray
        };

        var camera = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*"),
            ColumnSpacing = 8
        };
        AddField(camera, "Yaw", _yaw, 0);
        AddField(camera, "Pitch", _pitch, 2);
        AddField(camera, "Roll", _roll, 4);

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(75, 75, 75)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = _preview
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var import = new Button { Content = "Import as Layer", IsDefault = true };
        import.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);

        Content = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
            Children = { camera, previewBorder, _status, buttons }
        };
        Grid.SetRow(previewBorder, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(buttons, 3);

        _yaw.ValueChanged += (_, _) => QueuePreview();
        _pitch.ValueChanged += (_, _) => QueuePreview();
        _roll.ValueChanged += (_, _) => QueuePreview();
        Closed += (_, _) =>
        {
            _closed = true;
            _previewRevision++;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
        };
        QueuePreview();
    }

    private static NumericUpDown Angle(decimal value, decimal min = -180, decimal max = 180) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        Increment = 5,
        FormatString = "0°",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static void AddField(Grid grid, string label, Control control, int column)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, column);
        Grid.SetColumn(control, column + 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private async void QueuePreview()
    {
        int revision = ++_previewRevision;
        ReferenceRenderOptions options = ResultOptions;
        _status.Text = "Rendering preview…";
        try
        {
            using Surface rendered = await Task.Run(() =>
                ReferenceRenderer.Render(_mesh, PreviewWidth, PreviewHeight, options));
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
