// KawaPaint - prompts for a new canvas size (image resize/scale).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class ResizeDialog : Window
{
    private readonly NumericUpDown _width;
    private readonly NumericUpDown _height;

    public int ResultWidth => (int)(_width.Value ?? 1);
    public int ResultHeight => (int)(_height.Value ?? 1);

    public ResizeDialog(int currentWidth, int currentHeight)
    {
        Title = "Resize Image";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _width = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = currentWidth, Increment = 1 };
        _height = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = currentHeight, Increment = 1 };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var wl = new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center };
        var hl = new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hl, 1); Grid.SetRow(_height, 1); Grid.SetColumn(_width, 1); Grid.SetColumn(_height, 1);
        grid.Children.Add(wl); grid.Children.Add(_width); grid.Children.Add(hl); grid.Children.Add(_height);

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

        Content = new StackPanel { Margin = new Thickness(16), Children = { grid, buttons } };
    }
}
