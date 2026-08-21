// KawaPaint - prompts for a new canvas size with a 9-point anchor (Paint.NET's "Canvas Size",
// distinct from Resize: content is placed and padded/cropped, never rescaled).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class CanvasSizeDialog : Window
{
    private readonly NumericUpDown _width;
    private readonly NumericUpDown _height;
    private readonly Button[] _anchorButtons = new Button[9];
    private int _anchorIndex = 4;   // MiddleCenter

    private static readonly CanvasAnchor[] AnchorOrder =
    {
        CanvasAnchor.TopLeft, CanvasAnchor.TopCenter, CanvasAnchor.TopRight,
        CanvasAnchor.MiddleLeft, CanvasAnchor.MiddleCenter, CanvasAnchor.MiddleRight,
        CanvasAnchor.BottomLeft, CanvasAnchor.BottomCenter, CanvasAnchor.BottomRight
    };

    public int ResultWidth => (int)(_width.Value ?? 1);
    public int ResultHeight => (int)(_height.Value ?? 1);
    public CanvasAnchor ResultAnchor => AnchorOrder[_anchorIndex];

    public CanvasSizeDialog(int currentWidth, int currentHeight)
    {
        Title = "Canvas Size";
        Width = 340;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _width = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = currentWidth, Increment = 1 };
        _height = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = currentHeight, Increment = 1 };

        var sizeGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("70,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        var wl = new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center };
        var hl = new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(hl, 1); Grid.SetRow(_height, 1); Grid.SetColumn(_width, 1); Grid.SetColumn(_height, 1);
        sizeGrid.Children.Add(wl); sizeGrid.Children.Add(_width); sizeGrid.Children.Add(hl); sizeGrid.Children.Add(_height);

        var anchorLabel = new TextBlock { Text = "Anchor", Margin = new Thickness(0, 12, 0, 4) };
        var anchorGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,36,36"),
            RowDefinitions = new RowDefinitions("36,36,36"),
            ColumnSpacing = 4,
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        for (int i = 0; i < 9; i++)
        {
            int captured = i;
            var btn = new Button
            {
                Content = new Border { Background = Brushes.White, Width = 10, Height = 10 },
                Padding = new Thickness(0)
            };
            btn.Click += (_, _) => SetAnchor(captured);
            Grid.SetRow(btn, i / 3);
            Grid.SetColumn(btn, i % 3);
            anchorGrid.Children.Add(btn);
            _anchorButtons[i] = btn;
        }
        SetAnchor(_anchorIndex);

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

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { sizeGrid, anchorLabel, anchorGrid, buttons }
        };
    }

    private void SetAnchor(int index)
    {
        _anchorIndex = index;
        for (int i = 0; i < 9; i++)
            _anchorButtons[i].BorderBrush = i == index ? Brushes.DodgerBlue : Brushes.Gray;
        for (int i = 0; i < 9; i++)
            _anchorButtons[i].BorderThickness = new Thickness(i == index ? 2 : 1);
    }
}
