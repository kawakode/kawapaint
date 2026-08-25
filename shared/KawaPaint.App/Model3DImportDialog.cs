using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

public sealed class Model3DImportDialog : Window
{
    private readonly NumericUpDown _yaw = Angle(35);
    private readonly NumericUpDown _pitch = Angle(-25);
    private readonly NumericUpDown _roll = Angle(0);
    private readonly TextBox _color = new() { Text = "#D7D7E2", Width = 110 };
    private readonly CheckBox _edges = new() { Content = "Draw mesh edges", IsChecked = true };

    public Model3DImportDialog(string name)
    {
        Title = "Import 3D Reference";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var import = new Button { Content = "Rasterize", IsDefault = true };
        import.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 9,
            Children =
            {
                new TextBlock { Text = name, FontWeight = Avalonia.Media.FontWeight.Bold,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "The model is fit to the current canvas and becomes an ordinary raster layer.",
                    Opacity = 0.72, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                Row("Yaw", _yaw, "degrees"),
                Row("Pitch", _pitch, "degrees"),
                Row("Roll", _roll, "degrees"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 7,
                    Children = { new TextBlock { Text = "Surface colour", VerticalAlignment = VerticalAlignment.Center }, _color }
                },
                _edges,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, import }
                }
            }
        };
    }

    public ReferenceRenderOptions Options
    {
        get
        {
            ColorBgra color = ColorBgra.TryParseHexString(_color.Text, out var parsed)
                ? parsed : ColorBgra.FromBgr(215, 215, 226);
            return new ReferenceRenderOptions
            {
                YawDegrees = Number(_yaw, 35),
                PitchDegrees = Number(_pitch, -25),
                RollDegrees = Number(_roll, 0),
                Color = color,
                ShowEdges = _edges.IsChecked ?? true
            };
        }
    }

    private static NumericUpDown Angle(decimal value) => new()
    {
        Minimum = -360, Maximum = 360, Increment = 5, Value = value, Width = 110
    };

    private static double Number(NumericUpDown field, double fallback)
        => field.Value is { } value ? (double)value : fallback;

    private static Control Row(string label, NumericUpDown input, string suffix) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 7,
        Children =
        {
            new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center },
            input,
            new TextBlock { Text = suffix, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center }
        }
    };
}
