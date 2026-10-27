// KawaPaint - asks for a radius for Grow / Shrink / Feather Selection. Each change is shown on the
// canvas right away through the preview callback; the caller puts the selection back on Cancel.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class SelectionRadiusDialog : Window
{
    private readonly NumericUpDown _radius;

    public int ResultRadius => (int)Math.Clamp(_radius.Value ?? 1, 1, Selection.MaxModifyRadius);

    public SelectionRadiusDialog(string title, int initialRadius, Action<int> preview)
    {
        Title = title;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _radius = new NumericUpDown
        {
            Name = "SelectionRadius", Minimum = 1, Maximum = Selection.MaxModifyRadius,
            Value = Math.Clamp(initialRadius, 1, Selection.MaxModifyRadius), Increment = 1, FormatString = "0"
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 8 };
        var label = new TextBlock { Text = "Radius (pixels)", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_radius, 1);
        grid.Children.Add(label); grid.Children.Add(_radius);

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

        _radius.ValueChanged += (_, _) => preview(ResultRadius);
        Opened += (_, _) => preview(ResultRadius);
        Content = new StackPanel { Margin = new Thickness(16), Children = { grid, buttons } };
    }
}
