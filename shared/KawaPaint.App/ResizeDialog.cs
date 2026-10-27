// KawaPaint - prompts for a new canvas size (image resize/scale), by pixels or by percentage,
// optionally keeping the width/height ratio.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class ResizeDialog : Window
{
    private readonly NumericUpDown _width;
    private readonly NumericUpDown _height;
    private readonly NumericUpDown _percent;
    private readonly CheckBox _keepProportions;
    private readonly int _currentWidth;
    private readonly int _currentHeight;
    private bool _syncing;

    public int ResultWidth => (int)(_width.Value ?? 1);
    public int ResultHeight => (int)(_height.Value ?? 1);

    public ResizeDialog(int currentWidth, int currentHeight, int layerCount = 1)
    {
        Title = "Resize Image";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _currentWidth = Math.Max(1, currentWidth);
        _currentHeight = Math.Max(1, currentHeight);
        _percent = new NumericUpDown { Name = "ResizePercent", Minimum = 1, Maximum = 1000, Value = 100, Increment = 5, FormatString = "0.##" };
        _width = new NumericUpDown { Name = "ResizeWidth", Minimum = 1, Maximum = 20000, Value = currentWidth, Increment = 1 };
        _height = new NumericUpDown { Name = "ResizeHeight", Minimum = 1, Maximum = 20000, Value = currentHeight, Increment = 1 };
        _keepProportions = new CheckBox { Name = "ResizeKeepProportions", Content = "Keep proportions", IsChecked = true };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        var pl = new TextBlock { Text = "Percentage", VerticalAlignment = VerticalAlignment.Center };
        var wl = new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center };
        var hl = new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_percent, 1);
        Grid.SetRow(wl, 1); Grid.SetRow(_width, 1); Grid.SetColumn(_width, 1);
        Grid.SetRow(hl, 2); Grid.SetRow(_height, 2); Grid.SetColumn(_height, 1);
        Grid.SetRow(_keepProportions, 3); Grid.SetColumn(_keepProportions, 1);
        grid.Children.Add(pl); grid.Children.Add(_percent);
        grid.Children.Add(wl); grid.Children.Add(_width); grid.Children.Add(hl); grid.Children.Add(_height);
        grid.Children.Add(_keepProportions);

        _percent.ValueChanged += (_, _) => Sync(() =>
        {
            double scale = (double)(_percent.Value ?? 100) / 100;
            _width.Value = Scaled(_currentWidth, scale);
            _height.Value = Scaled(_currentHeight, scale);
        });
        _width.ValueChanged += (_, _) => Sync(() =>
        {
            double scale = (double)(_width.Value ?? 1) / _currentWidth;
            if (_keepProportions.IsChecked == true) _height.Value = Scaled(_currentHeight, scale);
            UpdatePercent();
        });
        _height.ValueChanged += (_, _) => Sync(() =>
        {
            double scale = (double)(_height.Value ?? 1) / _currentHeight;
            if (_keepProportions.IsChecked == true) _width.Value = Scaled(_currentWidth, scale);
            UpdatePercent();
        });
        // Turning proportions back on re-derives the height from the width.
        _keepProportions.IsCheckedChanged += (_, _) =>
        {
            if (_keepProportions.IsChecked == true)
                Sync(() =>
                {
                    _height.Value = Scaled(_currentHeight, (double)(_width.Value ?? 1) / _currentWidth);
                    UpdatePercent();
                });
        };

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
        var memory = new ImageMemoryNote(_width, _height, ok, layerCount, currentWidth, currentHeight);
        memory.Text.Margin = new Thickness(0, 8, 0, 0);

        Content = new StackPanel { Margin = new Thickness(16), Children = { grid, memory.Text, buttons } };
    }

    private void Sync(Action update)
    {
        if (_syncing) return;
        _syncing = true;
        try { update(); }
        finally { _syncing = false; }
    }

    /// <summary>The percentage field shows the common scale, or is cleared when width and height
    /// were scaled differently or the scale is out of its range.</summary>
    private void UpdatePercent()
    {
        decimal percent = Math.Round((decimal)(_width.Value ?? 1) * 100 / _currentWidth, 2);
        double scale = (double)percent / 100;
        bool uniform = Scaled(_currentWidth, scale) == (_width.Value ?? 1)
            && Scaled(_currentHeight, scale) == (_height.Value ?? 1);
        _percent.Value = uniform && percent is >= 1 and <= 1000 ? percent : null;
    }

    private static decimal Scaled(int size, double scale) => Math.Clamp((decimal)Math.Round(size * scale), 1, 20000);
}
