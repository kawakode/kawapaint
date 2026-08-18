// KawaPaint — prompts for a new image's size and background.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class NewImageDialog : Window
{
    private readonly NumericUpDown _width;
    private readonly NumericUpDown _height;
    private readonly NumericUpDown _dpi;
    private readonly CheckBox _transparent;

    public int ResultWidth => (int)(_width.Value ?? 1);
    public int ResultHeight => (int)(_height.Value ?? 1);
    public double ResultDpi => (double)(_dpi.Value ?? 96);
    public bool Transparent => _transparent.IsChecked ?? false;

    public NewImageDialog()
    {
        Title = "New Image";
        Width = 320;
        Height = 250;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _width = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = 800 };
        _height = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = 600 };
        _dpi = new NumericUpDown { Minimum = 1, Maximum = 2400, Value = 96 };
        _transparent = new CheckBox { Content = "Transparent background" };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var wl = new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center };
        var hl = new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center };
        var dl = new TextBlock { Text = "DPI", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hl, 1); Grid.SetRow(_height, 1); Grid.SetColumn(_width, 1); Grid.SetColumn(_height, 1);
        Grid.SetRow(dl, 2); Grid.SetRow(_dpi, 2); Grid.SetColumn(_dpi, 1);
        grid.Children.Add(wl); grid.Children.Add(_width); grid.Children.Add(hl); grid.Children.Add(_height);
        grid.Children.Add(dl); grid.Children.Add(_dpi);

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { grid, _transparent, buttons } };
    }
}
