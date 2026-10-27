// KawaPaint - asks for an angle to turn the whole image by, and whether the canvas grows to fit.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class RotateImageDialog : Window
{
    private readonly NumericUpDown _angle;
    private readonly CheckBox _expand;
    private readonly TextBlock _size;
    private readonly int _width, _height;

    /// <summary>Clockwise, in degrees.</summary>
    public double ResultAngle => (double)(_angle.Value ?? 0);
    public bool ResultExpand => _expand.IsChecked == true;

    public RotateImageDialog(int currentWidth, int currentHeight)
    {
        _width = currentWidth;
        _height = currentHeight;
        Title = "Rotate Image";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _angle = new NumericUpDown
        {
            Name = "RotateAngle", Minimum = -360, Maximum = 360, Value = 15, Increment = 1, FormatString = "0.##"
        };
        _expand = new CheckBox { Name = "RotateExpand", Content = "Enlarge canvas to fit", IsChecked = true };
        _size = new TextBlock { Name = "RotateSize", FontSize = 12 };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var label = new TextBlock { Text = "Angle (° clockwise)", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_angle, 1);
        Grid.SetRow(_expand, 1); Grid.SetColumn(_expand, 1);
        grid.Children.Add(label); grid.Children.Add(_angle); grid.Children.Add(_expand);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        _angle.ValueChanged += (_, _) => UpdateSize();
        _expand.IsCheckedChanged += (_, _) => UpdateSize();
        UpdateSize();
        _size.Margin = new Thickness(0, 8, 0, 0);
        Content = new StackPanel { Margin = new Thickness(16), Children = { grid, _size, buttons } };
    }

    private void UpdateSize()
    {
        var (w, h) = ResultExpand ? Surface.RotatedBounds(_width, _height, ResultAngle) : (_width, _height);
        _size.Text = $"New size: {w} × {h}";
    }
}
